using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Roomie.Core.Connections;
using Roomie.Core.Discovery;
using Roomie.Core.Files;
using Roomie.Core.Messaging;
using Roomie.Core.Protocol;
using Roomie.Core.Trust;
using Roomie.Infrastructure.Identity;
using Roomie.Infrastructure.Messaging;
using Roomie.Network.Connections;
using Roomie.Network.Discovery;
using Roomie.Network.Files;
using Roomie.Network.Groups;
using Roomie.Network.Messaging;

namespace Roomie.Tests.Files;

/// <summary>Moving large files is heavy on the disk, which would starve the timing-sensitive tests running beside it.</summary>
[CollectionDefinition(nameof(HeavyFileTests), DisableParallelization = true)]
public sealed class HeavyFileTests;

/// <summary>Files between real nodes over loopback TLS, and from hand-driven peers that can lie.</summary>
[Collection(nameof(HeavyFileTests))]
public sealed class FileTransferServiceTests : IAsyncLifetime
{
    private static readonly LanInterface Loopback = new("lo", "Loopback", new Ipv4Subnet(IPAddress.Loopback, 8));
    private static readonly string[] FileCapabilities = [RmpCapabilities.Text, RmpCapabilities.FileTransfer];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "roomie-tests", Guid.NewGuid().ToString("N"));
    private readonly List<IAsyncDisposable> _cleanup = [];

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var item in Enumerable.Reverse(_cleanup))
        {
            await item.DisposeAsync();
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ---- The normal path ----

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(FileChunk.MaxDataLength - 1)]
    [InlineData(FileChunk.MaxDataLength)]
    [InlineData(FileChunk.MaxDataLength + 1)]
    [InlineData((FileChunk.MaxDataLength * 3) + 17)]
    public async Task AcceptedFile_ArrivesIdentical_AndBothSidesSeeItComplete(int size)
    {
        var (alice, bob) = await PairedAsync();
        var (path, content) = CreateFile("notes.pdf", size);

        var id = (await alice.Files.SendFileAsync(bob.Id, path)).TransferId!.Value;
        await WaitUntil(() => bob.Incoming(alice, id)?.State == FileTransferState.Offered);
        Assert.Null((await bob.Files.AcceptAsync(alice.Id, id)).Error);

        await WaitUntil(() => alice.Outgoing(bob, id)?.State == FileTransferState.Completed);
        var received = bob.Incoming(alice, id)!;
        Assert.Equal(FileTransferState.Completed, received.State);
        Assert.Equal(Path.Combine(bob.Downloads, "notes.pdf"), received.LocalPath);
        Assert.Equal(content, File.ReadAllBytes(received.LocalPath!));
        Assert.Empty(Directory.GetFiles(bob.IncomingFolder));
    }

    [Fact]
    public async Task LargeFile_ArrivesIdentical_WithMessagesFlowingAtTheSameTime()
    {
        var (alice, bob) = await PairedAsync();
        var (path, content) = CreateFile("big.bin", 8 * 1024 * 1024);

        var id = (await alice.Files.SendFileAsync(bob.Id, path)).TransferId!.Value;
        await WaitUntil(() => bob.Incoming(alice, id) is not null);
        await bob.Files.AcceptAsync(alice.Id, id);
        var message = alice.Messaging.SendText(bob.Id, "While the file moves").Message!;

        await WaitUntil(() => alice.Outgoing(bob, id)?.State == FileTransferState.Completed);
        await WaitUntil(() => alice.Store.GetConversation(bob.Id).Single().State == DeliveryState.Delivered);
        Assert.Equal(message.MessageId, bob.Store.GetConversation(alice.Id).Single().MessageId);
        Assert.Equal(content, File.ReadAllBytes(bob.Incoming(alice, id)!.LocalPath!));
    }

    [Fact]
    public async Task ReceivedFile_IsMarkedAsComingFromElsewhere()
    {
        var (alice, bob) = await PairedAsync();
        var received = await TransferAsync(alice, bob, CreateFile("setup.exe", 100).Path);

        Assert.Contains("ZoneId=3", File.ReadAllText(received + ":Zone.Identifier"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SameNameTwice_KeepsBothFiles()
    {
        var (alice, bob) = await PairedAsync();
        var first = CreateFile("notes.pdf", 100);
        var firstSaved = await TransferAsync(alice, bob, first.Path);
        var second = CreateFile("notes.pdf", 200);
        var secondSaved = await TransferAsync(alice, bob, second.Path);

        Assert.NotEqual(firstSaved, secondSaved);
        Assert.Equal("notes (1).pdf", Path.GetFileName(secondSaved));
        Assert.Equal(first.Content, File.ReadAllBytes(firstSaved));
        Assert.Equal(second.Content, File.ReadAllBytes(secondSaved));
    }

    [Fact]
    public async Task OfferToAnOfflineDevice_ArrivesWhenItConnects_AndNothingMovesUntilAccepted()
    {
        var alice = await StartAsync("Alice");
        var bob = await StartAsync("Bob");
        Pair(alice, bob);
        var id = (await alice.Files.SendFileAsync(bob.Id, CreateFile("notes.pdf", 100).Path)).TransferId!.Value;
        Assert.Equal(FileTransferState.Waiting, alice.Outgoing(bob, id)!.State);

        await ConnectAsync(alice, bob);

        await WaitUntil(() => bob.Incoming(alice, id)?.State == FileTransferState.Offered);
        await Task.Delay(100);
        Assert.Equal(FileTransferState.Offered, alice.Outgoing(bob, id)!.State);
        Assert.Empty(Directory.GetFiles(bob.Downloads));
        Assert.Empty(Directory.GetFiles(bob.IncomingFolder));
    }

    [Fact]
    public async Task Declining_TellsTheSender_AndSavesNothing()
    {
        var (alice, bob) = await PairedAsync();
        var id = (await alice.Files.SendFileAsync(bob.Id, CreateFile("notes.pdf", 100).Path)).TransferId!.Value;
        await WaitUntil(() => bob.Incoming(alice, id) is not null);

        Assert.Null((await bob.Files.DeclineAsync(alice.Id, id)).Error);

        await WaitUntil(() => alice.Outgoing(bob, id)?.State == FileTransferState.Rejected);
        Assert.Empty(Directory.GetFiles(bob.Downloads));
        Assert.NotNull((await bob.Files.AcceptAsync(alice.Id, id)).Error);
    }

    [Fact]
    public async Task OnlyTwoFilesMoveAtOnce_AndTheRestFollow()
    {
        var alice = await StartAsync("Alice");
        await using var raw = await RawPeerAsync(alice, "Raw");
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var id = (await alice.Files.SendFileAsync(raw.Id, CreateFile($"f{i}.bin", 4 * 1024 * 1024).Path)).TransferId!.Value;
            await raw.ReadUntilAsync(RmpMessageTypes.FileOffer);
            ids.Add(id);
        }

        foreach (var id in ids)
        {
            await raw.SendAsync(RmpMessageTypes.FileAccept, SessionPayloads.ToElement(new FileAcceptPayload(id)));
        }

        // Watches the wire: a transfer counts from its first chunk until its FILE_COMPLETE.
        var moving = new HashSet<Guid>();
        var finished = new HashSet<Guid>();
        var most = 0;
        while (finished.Count < ids.Count)
        {
            var frame = await raw.ReadFrameAsync();
            if (FileChunk.TryParse(frame.Payload, out var chunk) && frame.Type == RmpFrameType.FileChunk)
            {
                moving.Add(chunk.TransferId);
                most = Math.Max(most, moving.Count);
            }
            else if (frame.Type == RmpFrameType.Control && RmpEnvelopeCodec.TryDecode(frame.Payload, out var envelope, out _) && envelope.Type == RmpMessageTypes.FileComplete)
            {
                var done = SessionPayloads.TryDeserialize<FileCompletePayload>(envelope.Payload)!.TransferId;
                moving.Remove(done);
                finished.Add(done);
            }
        }

        Assert.Equal(2, most);
        Assert.Equal(ids.ToHashSet(), finished);
    }

    // ---- What the sender can get wrong, or lie about ----

    [Fact]
    public async Task ChunkBeforeAccepting_IsRefused_AndWritesNothing()
    {
        var bob = await StartAsync("Bob");
        await using var raw = await RawPeerAsync(bob, "Raw");
        var content = RandomBytes(100);
        var id = await raw.OfferAsync(bob, "notes.pdf", content);

        await raw.SendChunkAsync(id, 0, content);

        var error = await raw.ReadErrorAsync();
        Assert.Equal(RmpErrorCodes.TransferRejected, error.Code);
        Assert.Empty(Directory.GetFiles(bob.IncomingFolder));
        Assert.Empty(Directory.GetFiles(bob.Downloads));
        Assert.Equal(FileTransferState.Offered, bob.IncomingOf(raw.Id, id)!.State);
    }

    [Fact]
    public async Task ChunkForAnUnknownTransfer_IsRefused_AndTheSessionStays()
    {
        var bob = await StartAsync("Bob");
        await using var raw = await RawPeerAsync(bob, "Raw");

        await raw.SendChunkAsync(Guid.NewGuid(), 0, RandomBytes(10));

        Assert.Equal(RmpErrorCodes.TransferRejected, (await raw.ReadErrorAsync()).Code);
        await raw.RoundTripAsync();
    }

    [Fact]
    public async Task EndlessChunksForNothing_EventuallyCloseTheSession()
    {
        var bob = await StartAsync("Bob");
        await using var raw = await RawPeerAsync(bob, "Raw");

        for (var i = 0; i < 31; i++)
        {
            await raw.SendChunkAsync(Guid.NewGuid(), 0, RandomBytes(10));
        }

        // The named reason matters: a silent peer's session also closes, after a heartbeat timeout.
        for (var i = 0; i < 30; i++)
        {
            Assert.Equal(RmpErrorCodes.TransferRejected, (await raw.ReadErrorAsync()).Code);
        }

        Assert.Equal(RmpErrorCodes.RateLimited, (await raw.ReadErrorAsync()).Code);
        await WaitUntil(() => !bob.Connections.TryGetSession(raw.Id, out _));
    }

    [Fact]
    public async Task MalformedChunk_ClosesTheSession()
    {
        var bob = await StartAsync("Bob");
        await using var raw = await RawPeerAsync(bob, "Raw");

        await raw.SendFrameAsync(RmpFraming.Encode(RmpFrameType.FileChunk, new byte[10]));

        Assert.Equal(RmpErrorCodes.InvalidFrame, (await raw.ReadErrorAsync()).Code);
        await WaitUntil(() => !bob.Connections.TryGetSession(raw.Id, out _));
    }

    [Fact]
    public async Task ChunkFromAPeerWithoutTheFeature_ClosesTheSession()
    {
        var bob = await StartAsync("Bob");
        await using var raw = await RawPeerAsync(bob, "Raw", [RmpCapabilities.Text]);

        await raw.SendChunkAsync(Guid.NewGuid(), 0, RandomBytes(10));

        Assert.Equal(RmpErrorCodes.InvalidFrame, (await raw.ReadErrorAsync()).Code);
        await WaitUntil(() => !bob.Connections.TryGetSession(raw.Id, out _));
    }

    [Fact]
    public async Task ChunkFromAnUnpairedPeer_ClosesTheSession()
    {
        var bob = await StartAsync("Bob");
        await using var raw = await RawPeerAsync(bob, "Raw", trusted: false);

        await raw.SendChunkAsync(Guid.NewGuid(), 0, RandomBytes(10));

        Assert.Equal(RmpErrorCodes.InvalidFrame, (await raw.ReadErrorAsync()).Code);
        await WaitUntil(() => !bob.Connections.TryGetSession(raw.Id, out _));
    }

    [Fact]
    public async Task OfferFromAnUnpairedPeer_IsRefused()
    {
        var bob = await StartAsync("Bob");
        await using var raw = await RawPeerAsync(bob, "Raw", trusted: false);
        var offer = new FileOfferPayload(Guid.NewGuid(), "evil.exe", 1, new string('a', 64), "application/octet-stream");

        await raw.SendAsync(RmpMessageTypes.FileOffer, SessionPayloads.ToElement(offer));

        Assert.Equal(RmpErrorCodes.PeerNotTrusted, (await raw.ReadErrorAsync()).Code);
        Assert.Null(bob.IncomingOf(raw.Id, offer.TransferId));
    }

    [Fact]
    public async Task ChunkOutOfOrder_FailsTheTransfer_AndRemovesWhatArrived()
    {
        var bob = await StartAsync("Bob");
        await using var raw = await RawPeerAsync(bob, "Raw");
        var content = RandomBytes(100);
        var id = await raw.OfferAndAcceptAsync(bob, "notes.pdf", content);

        await raw.SendChunkAsync(id, 0, content.AsSpan(0, 50).ToArray());
        await raw.SendChunkAsync(id, 2, content.AsSpan(50).ToArray());

        var reject = await raw.ReadUntilAsync(RmpMessageTypes.FileReject);
        Assert.Equal(id, SessionPayloads.TryDeserialize<FileRejectPayload>(reject.Payload)!.TransferId);
        Assert.Equal(FileTransferState.Failed, bob.IncomingOf(raw.Id, id)!.State);
        Assert.Empty(Directory.GetFiles(bob.IncomingFolder));
        Assert.Empty(Directory.GetFiles(bob.Downloads));
    }

    [Fact]
    public async Task MoreDataThanOffered_FailsTheTransfer()
    {
        var bob = await StartAsync("Bob");
        await using var raw = await RawPeerAsync(bob, "Raw");
        var content = RandomBytes(100);
        var id = await raw.OfferAndAcceptAsync(bob, "notes.pdf", content);

        await raw.SendChunkAsync(id, 0, RandomBytes(101));

        await raw.ReadUntilAsync(RmpMessageTypes.FileReject);
        Assert.Equal(FileTransferState.Failed, bob.IncomingOf(raw.Id, id)!.State);
        Assert.Empty(Directory.GetFiles(bob.IncomingFolder));
    }

    [Fact]
    public async Task ChunkFromAnotherDevice_CantWriteIntoATransfer()
    {
        var bob = await StartAsync("Bob");
        await using var sender = await RawPeerAsync(bob, "Sender");
        await using var mallory = await RawPeerAsync(bob, "Mallory");
        var content = RandomBytes(100);
        var id = await sender.OfferAndAcceptAsync(bob, "notes.pdf", content);

        await mallory.SendChunkAsync(id, 0, RandomBytes(100));
        Assert.Equal(RmpErrorCodes.TransferRejected, (await mallory.ReadErrorAsync()).Code);

        // The real sender is unaffected.
        await sender.SendChunkAsync(id, 0, content);
        await sender.SendAsync(RmpMessageTypes.FileComplete, SessionPayloads.ToElement(new FileCompletePayload(id)));
        var ack = await sender.ReadUntilAsync(RmpMessageTypes.FileAck);
        Assert.True(SessionPayloads.TryDeserialize<FileAckPayload>(ack.Payload)!.Verified);
        Assert.Equal(content, File.ReadAllBytes(bob.IncomingOf(sender.Id, id)!.LocalPath!));
    }

    [Fact]
    public async Task AThirdFileFromOneDevice_IsRefusedWhileTwoAreMoving()
    {
        var bob = await StartAsync("Bob");
        await using var raw = await RawPeerAsync(bob, "Raw");
        var content = RandomBytes(100);
        var first = await raw.OfferAndAcceptAsync(bob, "a.txt", content);
        var second = await raw.OfferAndAcceptAsync(bob, "b.txt", content);
        var third = await raw.OfferAndAcceptAsync(bob, "c.txt", content);
        await raw.SendChunkAsync(first, 0, content.AsSpan(0, 10).ToArray());
        await raw.SendChunkAsync(second, 0, content.AsSpan(0, 10).ToArray());

        await raw.SendChunkAsync(third, 0, content.AsSpan(0, 10).ToArray());

        var reject = await raw.ReadUntilAsync(RmpMessageTypes.FileReject);
        Assert.Equal(third, SessionPayloads.TryDeserialize<FileRejectPayload>(reject.Payload)!.TransferId);
        Assert.Equal(FileTransferState.Failed, bob.IncomingOf(raw.Id, third)!.State);
        Assert.Equal(FileTransferState.Transferring, bob.IncomingOf(raw.Id, first)!.State);
    }

    [Fact]
    public async Task ChunksAlreadyOnTheWay_AfterATransferEnded_AreIgnored()
    {
        var bob = await StartAsync("Bob");
        await using var raw = await RawPeerAsync(bob, "Raw");
        var content = RandomBytes(100);
        var id = await raw.OfferAndAcceptAsync(bob, "notes.pdf", content);
        await bob.Files.CancelAsync(raw.Id, MessageDirection.Incoming, id);
        await raw.ReadUntilAsync(RmpMessageTypes.FileReject);

        for (var i = 0; i < 40; i++)
        {
            await raw.SendChunkAsync(id, (uint)i, content);
        }

        await raw.RoundTripAsync();
        Assert.True(bob.Connections.TryGetSession(raw.Id, out _));
        Assert.Empty(Directory.GetFiles(bob.IncomingFolder));
    }

    [Fact]
    public async Task HashMismatch_RemovesTheFile_AndTellsTheSender()
    {
        var bob = await StartAsync("Bob");
        await using var raw = await RawPeerAsync(bob, "Raw");
        var offered = RandomBytes(100);
        var id = await raw.OfferAndAcceptAsync(bob, "notes.pdf", offered);

        await raw.SendChunkAsync(id, 0, RandomBytes(100));
        await raw.SendAsync(RmpMessageTypes.FileComplete, SessionPayloads.ToElement(new FileCompletePayload(id)));

        var ack = await raw.ReadUntilAsync(RmpMessageTypes.FileAck);
        Assert.False(SessionPayloads.TryDeserialize<FileAckPayload>(ack.Payload)!.Verified);
        await WaitUntil(() => bob.IncomingOf(raw.Id, id)!.State == FileTransferState.Failed);
        Assert.Empty(Directory.GetFiles(bob.Downloads));
        Assert.Empty(Directory.GetFiles(bob.IncomingFolder));
    }

    [Fact]
    public async Task CompleteBeforeAllTheData_FailsTheTransfer()
    {
        var bob = await StartAsync("Bob");
        await using var raw = await RawPeerAsync(bob, "Raw");
        var content = RandomBytes(100);
        var id = await raw.OfferAndAcceptAsync(bob, "notes.pdf", content);

        await raw.SendChunkAsync(id, 0, content.AsSpan(0, 50).ToArray());
        await raw.SendAsync(RmpMessageTypes.FileComplete, SessionPayloads.ToElement(new FileCompletePayload(id)));

        var ack = await raw.ReadUntilAsync(RmpMessageTypes.FileAck);
        Assert.False(SessionPayloads.TryDeserialize<FileAckPayload>(ack.Payload)!.Verified);
        Assert.Empty(Directory.GetFiles(bob.Downloads));
    }

    [Fact]
    public async Task ShortFileWhoseHashMatches_IsStillRefused_BecauseItIsSmallerThanOffered()
    {
        var bob = await StartAsync("Bob");
        await using var raw = await RawPeerAsync(bob, "Raw");
        var half = RandomBytes(50);
        var id = Guid.CreateVersion7();
        var offer = new FileOfferPayload(id, "notes.pdf", 100, Convert.ToHexStringLower(SHA256.HashData(half)), "application/octet-stream");
        await raw.SendAsync(RmpMessageTypes.FileOffer, SessionPayloads.ToElement(offer));
        await WaitUntil(() => bob.IncomingOf(raw.Id, id) is not null);
        await bob.Files.AcceptAsync(raw.Id, id);
        await raw.ReadUntilAsync(RmpMessageTypes.FileAccept);

        await raw.SendChunkAsync(id, 0, half);
        await raw.SendAsync(RmpMessageTypes.FileComplete, SessionPayloads.ToElement(new FileCompletePayload(id)));

        var ack = await raw.ReadUntilAsync(RmpMessageTypes.FileAck);
        Assert.False(SessionPayloads.TryDeserialize<FileAckPayload>(ack.Payload)!.Verified);
        Assert.Empty(Directory.GetFiles(bob.Downloads));
    }

    [Theory]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("evil.exe.", "evil.exe")]
    [InlineData("photo\u202Egpj.exe", "photogpj.exe")]
    public async Task OfferedName_IsAlwaysMadeSafe(string offered, string saved)
    {
        var bob = await StartAsync("Bob");
        await using var raw = await RawPeerAsync(bob, "Raw");
        var content = RandomBytes(10);
        var id = await raw.OfferAndAcceptAsync(bob, offered, content);

        await raw.SendChunkAsync(id, 0, content);
        await raw.SendAsync(RmpMessageTypes.FileComplete, SessionPayloads.ToElement(new FileCompletePayload(id)));

        await raw.ReadUntilAsync(RmpMessageTypes.FileAck);
        Assert.Equal(saved, Path.GetFileName(bob.IncomingOf(raw.Id, id)!.LocalPath));
        Assert.Equal(bob.Downloads, Path.GetDirectoryName(bob.IncomingOf(raw.Id, id)!.LocalPath));
    }

    [Theory]
    [InlineData("..\\..\\evil.exe")]
    [InlineData("C:\\Windows\\evil.exe")]
    [InlineData("../evil.exe")]
    [InlineData("file.txt:stream")]
    public async Task OfferWithAPathInItsName_IsRefused(string filename)
    {
        var bob = await StartAsync("Bob");
        await using var raw = await RawPeerAsync(bob, "Raw");
        var offer = new FileOfferPayload(Guid.NewGuid(), filename, 1, new string('a', 64), "application/octet-stream");

        await raw.SendAsync(RmpMessageTypes.FileOffer, SessionPayloads.ToElement(offer));

        Assert.Equal(RmpErrorCodes.InvalidFrame, (await raw.ReadErrorAsync()).Code);
        Assert.Null(bob.IncomingOf(raw.Id, offer.TransferId));
    }

    [Fact]
    public async Task RepeatedOffer_DoesntAskTwice_AndAnswersWhatWasDecided()
    {
        var bob = await StartAsync("Bob");
        await using var raw = await RawPeerAsync(bob, "Raw");
        var content = RandomBytes(10);
        var id = await raw.OfferAsync(bob, "notes.pdf", content);
        var offers = 0;
        bob.Files.IncomingOffer += (_, _) => offers++;

        await raw.SendAsync(RmpMessageTypes.FileOffer, RawPeer.OfferPayload(id, "other-name.exe", content));
        await raw.RoundTripAsync();

        Assert.Equal(0, offers);
        Assert.Equal("notes.pdf", bob.IncomingOf(raw.Id, id)!.Filename);

        await bob.Files.DeclineAsync(raw.Id, id);
        await raw.ReadUntilAsync(RmpMessageTypes.FileReject);
        await raw.SendAsync(RmpMessageTypes.FileOffer, RawPeer.OfferPayload(id, "notes.pdf", content));

        var again = await raw.ReadUntilAsync(RmpMessageTypes.FileReject);
        Assert.Equal(FileRejectReasons.Declined, SessionPayloads.TryDeserialize<FileRejectPayload>(again.Payload)!.Reason);
    }

    [Fact]
    public async Task TooManyOpenOffers_AreDeclined()
    {
        var bob = await StartAsync("Bob", new FileOptionsOverride(MaxOpenOffers: 2));
        await using var raw = await RawPeerAsync(bob, "Raw");
        await raw.OfferAsync(bob, "a.txt", RandomBytes(1));
        await raw.OfferAsync(bob, "b.txt", RandomBytes(1));
        var third = Guid.NewGuid();

        await raw.SendAsync(RmpMessageTypes.FileOffer, RawPeer.OfferPayload(third, "c.txt", RandomBytes(1)));

        var reject = await raw.ReadUntilAsync(RmpMessageTypes.FileReject);
        Assert.Equal(FileRejectReasons.Busy, SessionPayloads.TryDeserialize<FileRejectPayload>(reject.Payload)!.Reason);
        Assert.Null(bob.IncomingOf(raw.Id, third));
    }

    [Fact]
    public async Task AcceptingWithoutEnoughDiskSpace_IsRefused_AndTheOfferStaysOpen()
    {
        var bob = await StartAsync("Bob", new FileOptionsOverride(MinFree: long.MaxValue / 2));
        await using var raw = await RawPeerAsync(bob, "Raw");
        var id = await raw.OfferAsync(bob, "a.txt", RandomBytes(1));

        Assert.Contains("disk space", (await bob.Files.AcceptAsync(raw.Id, id)).Error, StringComparison.Ordinal);

        Assert.Equal(FileTransferState.Offered, bob.IncomingOf(raw.Id, id)!.State);
        Assert.Empty(Directory.GetFiles(bob.IncomingFolder));
    }

    // ---- What a receiver can lie about ----

    [Fact]
    public async Task OnlyTheDeviceOfferedAFile_CanAcceptIt()
    {
        var alice = await StartAsync("Alice");
        await using var intended = await RawPeerAsync(alice, "Intended");
        await using var other = await RawPeerAsync(alice, "Other");
        var id = (await alice.Files.SendFileAsync(intended.Id, CreateFile("secret.txt", 100).Path)).TransferId!.Value;
        await intended.ReadUntilAsync(RmpMessageTypes.FileOffer);

        await other.SendAsync(RmpMessageTypes.FileAccept, SessionPayloads.ToElement(new FileAcceptPayload(id)));

        await other.ReadUntilAsync(RmpMessageTypes.FileReject);
        Assert.Equal(FileTransferState.Offered, alice.OutgoingOf(intended.Id, id)!.State);
        var seen = await other.ReadAllUntilHeartbeatAckAsync();
        Assert.DoesNotContain(seen, f => f.Type == RmpFrameType.FileChunk);
    }

    [Fact]
    public async Task OffersCantBeAcceptedByTheirOwnSender()
    {
        var bob = await StartAsync("Bob");
        await using var raw = await RawPeerAsync(bob, "Raw");
        var id = await raw.OfferAsync(bob, "a.txt", RandomBytes(1));

        // Raw offered this to Bob. Accepting it itself must not make Bob's incoming record think anything happened.
        await raw.SendAsync(RmpMessageTypes.FileAccept, SessionPayloads.ToElement(new FileAcceptPayload(id)));
        await raw.ReadUntilAsync(RmpMessageTypes.FileReject);

        Assert.Equal(FileTransferState.Offered, bob.IncomingOf(raw.Id, id)!.State);
    }

    [Fact]
    public async Task FalseConfirmation_ForAnotherDevicesTransfer_ChangesNothing()
    {
        var alice = await StartAsync("Alice");
        await using var intended = await RawPeerAsync(alice, "Intended");
        await using var other = await RawPeerAsync(alice, "Other");
        var id = (await alice.Files.SendFileAsync(intended.Id, CreateFile("a.txt", 10).Path)).TransferId!.Value;
        await intended.ReadUntilAsync(RmpMessageTypes.FileOffer);
        await intended.SendAsync(RmpMessageTypes.FileAccept, SessionPayloads.ToElement(new FileAcceptPayload(id)));
        await intended.ReadUntilAsync(RmpMessageTypes.FileComplete);
        await WaitUntil(() => alice.OutgoingOf(intended.Id, id)!.State == FileTransferState.Verifying);

        await other.SendAsync(RmpMessageTypes.FileAck, SessionPayloads.ToElement(new FileAckPayload(id, true)));
        await other.SendAsync(RmpMessageTypes.FileReject, SessionPayloads.ToElement(new FileRejectPayload(id, FileRejectReasons.Failed)));
        await other.RoundTripAsync();

        Assert.Equal(FileTransferState.Verifying, alice.OutgoingOf(intended.Id, id)!.State);
    }

    // ---- Stopping, and things going wrong ----

    [Fact]
    public async Task SenderCanceling_StopsBothSides_AndRemovesWhatArrived()
    {
        var (alice, bob) = await PairedAsync();
        var id = (await alice.Files.SendFileAsync(bob.Id, CreateFile("big.bin", 40 * 1024 * 1024).Path)).TransferId!.Value;
        await WaitUntil(() => bob.Incoming(alice, id) is not null);
        await bob.Files.AcceptAsync(alice.Id, id);
        await WaitUntil(() => alice.Files.GetProgress(bob.Id, id) > 0);

        Assert.Null((await alice.Files.CancelAsync(bob.Id, MessageDirection.Outgoing, id)).Error);

        await WaitUntil(() => bob.Incoming(alice, id)!.State == FileTransferState.Canceled);
        Assert.Equal(FileTransferState.Canceled, alice.Outgoing(bob, id)!.State);
        await WaitUntil(() => Directory.GetFiles(bob.IncomingFolder).Length == 0);
        Assert.Empty(Directory.GetFiles(bob.Downloads));
        Assert.Null(alice.Messaging.GetDeliveryProblem(bob.Id));
        Assert.True(alice.Connections.TryGetSession(bob.Id, out _));
    }

    [Fact]
    public async Task QuickAnswers_NeverLeaveATransferStuck()
    {
        var (alice, bob) = await PairedAsync();

        // Empty files move as fast as a transfer can, which once let a late write undo the sender's progress.
        for (var i = 0; i < 40; i++)
        {
            var id = (await alice.Files.SendFileAsync(bob.Id, CreateFile($"empty{i}.txt", 0).Path)).TransferId!.Value;
            await WaitUntil(() => bob.Incoming(alice, id) is not null);
            Assert.Null((await bob.Files.AcceptAsync(alice.Id, id)).Error);
            await WaitUntil(() => alice.Outgoing(bob, id)!.State == FileTransferState.Completed);
        }
    }

    [Fact]
    public async Task CancelingAtAnyMoment_AlwaysEndsTheTransferOnBothSides()
    {
        var (alice, bob) = await PairedAsync();
        var (path, _) = CreateFile("small.bin", 200_000);

        for (var i = 0; i < 30; i++)
        {
            var id = (await alice.Files.SendFileAsync(bob.Id, path)).TransferId!.Value;
            await WaitUntil(() => bob.Incoming(alice, id) is not null);
            await bob.Files.AcceptAsync(alice.Id, id);
            await Task.Delay(i % 5);
            await alice.Files.CancelAsync(bob.Id, MessageDirection.Outgoing, id);

            await WaitUntil(() => alice.Outgoing(bob, id)!.State >= FileTransferState.Completed && bob.Incoming(alice, id)!.State >= FileTransferState.Completed);
        }

        await WaitUntil(() => Directory.GetFiles(bob.IncomingFolder).Length == 0);
        Assert.True(alice.Connections.TryGetSession(bob.Id, out _));
    }

    [Fact]
    public async Task EndedTransfer_StillKnowsItsSourceFile_SoItCanBeSentAgain()
    {
        var alice = await StartAsync("Alice");
        await using var raw = await RawPeerAsync(alice, "Raw");
        var (path, _) = CreateFile("a.txt", 10);

        var canceled = (await alice.Files.SendFileAsync(raw.Id, path)).TransferId!.Value;
        await alice.Files.CancelAsync(raw.Id, MessageDirection.Outgoing, canceled);
        Assert.Equal(path, alice.OutgoingOf(raw.Id, canceled)!.LocalPath);

        var declined = (await alice.Files.SendFileAsync(raw.Id, path)).TransferId!.Value;
        await raw.SendAsync(RmpMessageTypes.FileReject, SessionPayloads.ToElement(new FileRejectPayload(declined, FileRejectReasons.Declined)));
        await WaitUntil(() => alice.OutgoingOf(raw.Id, declined)!.State == FileTransferState.Rejected);
        Assert.Equal(path, alice.OutgoingOf(raw.Id, declined)!.LocalPath);
    }

    [Fact]
    public async Task CancelingManyTransfers_NeverDamagesTheSession()
    {
        var (alice, bob) = await PairedAsync();
        var (path, _) = CreateFile("big.bin", 20 * 1024 * 1024);
        var sessions = new List<Guid>();

        for (var i = 0; i < 12; i++)
        {
            var id = (await alice.Files.SendFileAsync(bob.Id, path)).TransferId!.Value;
            await WaitUntil(() => bob.Incoming(alice, id) is not null);
            await bob.Files.AcceptAsync(alice.Id, id);
            await WaitUntil(() => alice.Files.GetProgress(bob.Id, id) > 0);
            await alice.Files.CancelAsync(bob.Id, MessageDirection.Outgoing, id);
            await WaitUntil(() => bob.Incoming(alice, id)!.State == FileTransferState.Canceled);
            Assert.True(bob.Connections.TryGetSession(alice.Id, out var current));
            sessions.Add(current.SessionId);
        }

        // Compared with the first sample, not with a read right after pairing: the session seen then was once replaced
        // before any transfer began, which says nothing about canceling.
        Assert.All(sessions, s => Assert.Equal(sessions[0], s));
        var message = alice.Messaging.SendText(bob.Id, "Still fine").Message!;
        await WaitUntil(() => alice.Store.GetConversation(bob.Id).Single(m => m.MessageId == message.MessageId).State == DeliveryState.Delivered);
    }

    [Fact]
    public async Task ReceiverCanceling_StopsTheSender()
    {
        var (alice, bob) = await PairedAsync();
        var id = (await alice.Files.SendFileAsync(bob.Id, CreateFile("big.bin", 40 * 1024 * 1024).Path)).TransferId!.Value;
        await WaitUntil(() => bob.Incoming(alice, id) is not null);
        await bob.Files.AcceptAsync(alice.Id, id);
        await WaitUntil(() => bob.Files.GetProgress(alice.Id, id) > 0);

        Assert.Null((await bob.Files.CancelAsync(alice.Id, MessageDirection.Incoming, id)).Error);

        await WaitUntil(() => alice.Outgoing(bob, id)!.State == FileTransferState.Canceled);
        Assert.Empty(Directory.GetFiles(bob.IncomingFolder));
        Assert.True(alice.Connections.TryGetSession(bob.Id, out _));
    }

    [Fact]
    public async Task AcceptingAnOfferTheSenderCanceled_EndsCleanly()
    {
        var alice = await StartAsync("Alice");
        var bob = await StartAsync("Bob");
        Pair(alice, bob);
        await ConnectAsync(alice, bob);
        var id = (await alice.Files.SendFileAsync(bob.Id, CreateFile("a.txt", 10).Path)).TransferId!.Value;
        await WaitUntil(() => bob.Incoming(alice, id) is not null);
        await alice.Connections.DisconnectAsync(bob.Id);
        await WaitUntil(() => !bob.Connections.TryGetSession(alice.Id, out _));
        await alice.Files.CancelAsync(bob.Id, MessageDirection.Outgoing, id);
        Assert.Equal(FileTransferState.Offered, bob.Incoming(alice, id)!.State);
        await alice.Connections.ConnectAsync(bob.Id);
        await WaitUntil(() => bob.Connections.TryGetSession(alice.Id, out _));

        await bob.Files.AcceptAsync(alice.Id, id);

        await WaitUntil(() => bob.Incoming(alice, id)!.State == FileTransferState.Canceled);
        Assert.Empty(Directory.GetFiles(bob.IncomingFolder));
    }

    // ---- Interruptions: nothing starts over ----

    [Fact]
    public async Task LostConnection_PausesBothSides_AndTheFileStillArrivesIdentical()
    {
        var (alice, bob) = await PairedAsync();
        var (path, content) = CreateFile("big.bin", 60 * 1024 * 1024);
        var id = await StartMovingAsync(alice, bob, path);

        await alice.Connections.DisconnectAsync(bob.Id);

        await WaitUntil(() => bob.Files.IsPaused(bob.Incoming(alice, id)!) && alice.Files.IsPaused(alice.Outgoing(bob, id)!));
        Assert.Equal(FileTransferState.Transferring, bob.Incoming(alice, id)!.State);
        Assert.Equal(FileTransferState.Transferring, alice.Outgoing(bob, id)!.State);
        Assert.Single(Directory.GetFiles(bob.IncomingFolder));
        Assert.True(bob.Files.GetProgress(alice.Id, id) > 0);
        Assert.Empty(Directory.GetFiles(bob.Downloads));

        await alice.Connections.ConnectAsync(bob.Id);

        await WaitUntil(() => alice.Outgoing(bob, id)!.State == FileTransferState.Completed);
        Assert.Equal(content, File.ReadAllBytes(bob.Incoming(alice, id)!.LocalPath!));
        Assert.Empty(Directory.GetFiles(bob.IncomingFolder));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Restarting_EitherSide_Continues_AndTheFileStillArrivesIdentical(bool restartReceiver)
    {
        var (alice, bob) = await PairedAsync();
        var (path, content) = CreateFile("big.bin", 60 * 1024 * 1024);
        var id = await StartMovingAsync(alice, bob, path);

        if (restartReceiver)
        {
            bob = await RestartAsync(bob);
            Assert.True(bob.Files.IsPaused(bob.Incoming(alice, id)!));
        }
        else
        {
            alice = await RestartAsync(alice);
            Assert.True(alice.Files.IsPaused(alice.Outgoing(bob, id)!));
        }

        Assert.Single(Directory.GetFiles(bob.IncomingFolder));
        await ConnectAsync(alice, bob);

        await WaitUntil(() => alice.Outgoing(bob, id)!.State == FileTransferState.Completed);
        Assert.Equal(content, File.ReadAllBytes(bob.Incoming(alice, id)!.LocalPath!));
    }

    // ---- The folder the user chose ----

    [Fact]
    public async Task ChosenFolder_GetsTheFile_AndNoUnfinishedFilesAreLeftThere()
    {
        var (alice, bob) = await PairedAsync();
        var chosen = Path.Combine(_root, "chosen");
        Assert.Null(bob.Files.TrySetReceiveFolder(chosen));
        var (path, content) = CreateFile("video.mp4", 200_000);

        var id = (await alice.Files.SendFileAsync(bob.Id, path)).TransferId!.Value;
        await WaitUntil(() => bob.Incoming(alice, id)?.State == FileTransferState.Offered);
        Assert.Null((await bob.Files.AcceptAsync(alice.Id, id)).Error);
        await WaitUntil(() => bob.Incoming(alice, id)?.State == FileTransferState.Completed);

        Assert.Equal(Path.Combine(chosen, "video.mp4"), bob.Incoming(alice, id)!.LocalPath);
        Assert.Equal(content, File.ReadAllBytes(Path.Combine(chosen, "video.mp4")));
        Assert.Equal(["video.mp4"], new DirectoryInfo(chosen).GetFileSystemInfos().Select(f => f.Name));
        Assert.Empty(Directory.GetFiles(bob.Downloads));
    }

    [Fact]
    public async Task ChangingTheFolder_LeavesATransferAlreadyAcceptedWhereItStarted()
    {
        var (alice, bob) = await PairedAsync();
        var first = Path.Combine(_root, "first");
        var second = Path.Combine(_root, "second");
        Assert.Null(bob.Files.TrySetReceiveFolder(first));
        var (path, content) = CreateFile("big.bin", 8 * 1024 * 1024);

        var id = (await alice.Files.SendFileAsync(bob.Id, path)).TransferId!.Value;
        await WaitUntil(() => bob.Incoming(alice, id)?.State == FileTransferState.Offered);
        await bob.Files.AcceptAsync(alice.Id, id);
        Assert.Null(bob.Files.TrySetReceiveFolder(second));
        await WaitUntil(() => bob.Incoming(alice, id)?.State == FileTransferState.Completed);

        Assert.Equal(content, File.ReadAllBytes(Path.Combine(first, "big.bin")));
        Assert.Empty(Directory.GetFileSystemEntries(second));
    }

    [Fact]
    public async Task ChosenFolder_ContinuesAfterARestart_FromTheSamePlace()
    {
        var (alice, bob) = await PairedAsync();
        var chosen = Path.Combine(_root, "chosen");
        Assert.Null(bob.Files.TrySetReceiveFolder(chosen));
        var (path, content) = CreateFile("big.bin", 60 * 1024 * 1024);
        var id = await StartMovingAsync(alice, bob, path);

        bob = await RestartAsync(bob);

        // Nothing here tells the restarted node about the folder: the transfer's own record has to.
        var partial = bob.Incoming(alice, id)!.PartialPath;
        Assert.NotNull(partial);
        Assert.StartsWith(chosen, partial, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(partial));

        await ConnectAsync(alice, bob);
        await WaitUntil(() => alice.Outgoing(bob, id)!.State == FileTransferState.Completed);
        Assert.Equal(content, File.ReadAllBytes(Path.Combine(chosen, "big.bin")));
        Assert.False(File.Exists(partial));
    }

    [Fact]
    public async Task CancelingInAChosenFolder_RemovesTheUnfinishedFile()
    {
        var (alice, bob) = await PairedAsync();
        var chosen = Path.Combine(_root, "chosen");
        Assert.Null(bob.Files.TrySetReceiveFolder(chosen));
        var (path, _) = CreateFile("big.bin", 60 * 1024 * 1024);
        var id = await StartMovingAsync(alice, bob, path);

        Assert.Null((await bob.Files.CancelAsync(alice.Id, MessageDirection.Incoming, id)).Error);

        Assert.Empty(Directory.GetFileSystemEntries(chosen));
    }

    [Fact]
    public async Task AFolderThatCantBeUsed_IsRefused_AndTheOldOneStays()
    {
        var bob = await StartAsync("Bob");
        var before = bob.Files.ReceiveFolder;
        var notAFolder = Path.Combine(_root, "file.txt");
        File.WriteAllText(notAFolder, "x");

        Assert.NotNull(bob.Files.TrySetReceiveFolder(notAFolder));

        Assert.Equal(before, bob.Files.ReceiveFolder);
        Assert.Equal("x", File.ReadAllText(notAFolder));
    }

    [Fact]
    public async Task AtStartup_LeftoversAreRemoved_ButNothingElseInTheChosenFolder()
    {
        var chosen = Path.Combine(_root, "chosen");
        var partials = Path.Combine(chosen, ".roomie-partial");
        Directory.CreateDirectory(partials);
        var leftover = Path.Combine(partials, $"{Guid.NewGuid():N}.part");
        var theirs = Path.Combine(chosen, "their-backup.part");
        File.WriteAllText(leftover, "x");
        File.WriteAllText(theirs, "keep me");

        await StartAsync("Bob", new FileOptionsOverride(ReceiveFolder: chosen));

        Assert.False(File.Exists(leftover));
        Assert.Equal("keep me", File.ReadAllText(theirs));
    }

    [Fact]
    public async Task AFileTooBigForTheChosenDrive_IsRefusedBeforeAnythingIsWritten()
    {
        var chosen = Path.Combine(_root, "chosen");
        Directory.CreateDirectory(chosen);
        var alice = await StartAsync("Alice");
        var bob = await StartAsync("Bob", new FileOptionsOverride(MinFree: long.MaxValue / 2, ReceiveFolder: chosen));
        Pair(alice, bob);
        await ConnectAsync(alice, bob);
        var (path, _) = CreateFile("a.bin", 100);

        var id = (await alice.Files.SendFileAsync(bob.Id, path)).TransferId!.Value;
        await WaitUntil(() => bob.Incoming(alice, id)?.State == FileTransferState.Offered);

        Assert.Contains("disk space", (await bob.Files.AcceptAsync(alice.Id, id)).Error, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(chosen));
    }

    [Fact]
    public async Task AFolderThatHasGoneAway_RefusesTheAnswer_AndTheOfferStaysOpen()
    {
        var blocker = Path.Combine(_root, "not-a-folder");
        File.WriteAllText(blocker, "x");
        var alice = await StartAsync("Alice");
        var bob = await StartAsync("Bob", new FileOptionsOverride(ReceiveFolder: Path.Combine(blocker, "gone")));
        Pair(alice, bob);
        await ConnectAsync(alice, bob);
        var (path, _) = CreateFile("a.bin", 100);

        var id = (await alice.Files.SendFileAsync(bob.Id, path)).TransferId!.Value;
        await WaitUntil(() => bob.Incoming(alice, id)?.State == FileTransferState.Offered);

        Assert.Contains("isn't available", (await bob.Files.AcceptAsync(alice.Id, id)).Error, StringComparison.Ordinal);
        Assert.Equal(FileTransferState.Offered, bob.Incoming(alice, id)!.State);
    }

    [Fact]
    public async Task GetRecentTransfers_ListsEveryDevice_NewestFirst()
    {
        var (alice, bob) = await PairedAsync();
        var (first, _) = CreateFile("first.txt", 10);
        var (second, _) = CreateFile("second.txt", 10);

        var one = (await alice.Files.SendFileAsync(bob.Id, first)).TransferId!.Value;
        await Task.Delay(5);
        var two = (await alice.Files.SendFileAsync(bob.Id, second)).TransferId!.Value;

        Assert.Equal([two, one], alice.Files.GetRecentTransfers(10).Select(t => t.TransferId));
        Assert.Equal([two], alice.Files.GetRecentTransfers(1).Select(t => t.TransferId));
    }

    [Fact]
    public async Task IncomingMessage_IsRaisedOnce_WithTheMessage()
    {
        var (alice, bob) = await PairedAsync();
        var heard = new List<StoredMessage>();
        bob.Messaging.IncomingMessage += (_, message) =>
        {
            lock (heard)
            {
                heard.Add(message);
            }
        };

        var sent = alice.Messaging.SendText(bob.Id, "Hello").Message!;
        await WaitUntil(() => alice.Store.GetConversation(bob.Id).Single().State == DeliveryState.Delivered);

        lock (heard)
        {
            Assert.Equal(sent.MessageId, Assert.Single(heard).MessageId);
            Assert.Equal("Hello", heard[0].Content);
        }
    }

    [Fact]
    public async Task Sender_ContinuesFromTheByteTheReceiverAsksFor()
    {
        var alice = await StartAsync("Alice");
        var identity = TrustedIdentity(alice, "Raw");
        var first = await RawPeer.ConnectAsync(identity, alice, FileCapabilities);
        await WaitUntil(() => alice.Connections.TryGetSession(first.Id, out _));
        var (path, content) = CreateFile("a.bin", 300_000);
        var id = (await alice.Files.SendFileAsync(first.Id, path)).TransferId!.Value;
        await first.ReadUntilAsync(RmpMessageTypes.FileOffer);
        await first.SendAsync(RmpMessageTypes.FileAccept, SessionPayloads.ToElement(new FileAcceptPayload(id)));
        await first.ReadFrameAsync();
        await first.DisposeAsync();
        await WaitUntil(() => !alice.Connections.TryGetSession(first.Id, out _));

        await using var second = await RawPeer.ConnectAsync(identity, alice, FileCapabilities, ownsIdentity: true);
        await WaitUntil(() => alice.Connections.TryGetSession(second.Id, out _));
        await second.SendAsync(RmpMessageTypes.FileResume, SessionPayloads.ToElement(new FileResumePayload(id, 100_000)));

        var received = new List<byte>();
        var sequences = new List<uint>();

        // A FILE_COMPLETE may come first, if the first session got everything out: that one only asks how it ended.
        while (received.Count < content.Length - 100_000)
        {
            var frame = await second.ReadFrameAsync();
            if (frame.Type == RmpFrameType.FileChunk && FileChunk.TryParse(frame.Payload, out var chunk) && chunk.TransferId == id)
            {
                sequences.Add(chunk.Sequence);
                received.AddRange(chunk.Data.ToArray());
            }
        }

        await second.ReadUntilAsync(RmpMessageTypes.FileComplete);

        // Only what was missing, numbered from 0 again.
        Assert.Equal(content[100_000..], received.ToArray());
        Assert.Equal(Enumerable.Range(0, sequences.Count).Select(i => (uint)i), sequences);

        await second.SendAsync(RmpMessageTypes.FileAck, SessionPayloads.ToElement(new FileAckPayload(id, true)));
        await WaitUntil(() => alice.OutgoingOf(second.Id, id)!.State == FileTransferState.Completed);
    }

    [Fact]
    public async Task Receiver_AsksOnlyForWhatItIsMissing_AndAcceptsTheRest()
    {
        var bob = await StartAsync("Bob");
        var identity = TrustedIdentity(bob, "Raw");
        var content = RandomBytes(300_000);
        var first = await RawPeer.ConnectAsync(identity, bob, FileCapabilities);
        await WaitUntil(() => bob.Connections.TryGetSession(first.Id, out _));
        var id = await first.OfferAndAcceptAsync(bob, "a.bin", content);
        await first.SendChunkAsync(id, 0, content[..FileChunk.MaxDataLength]);
        await first.SendChunkAsync(id, 1, content[FileChunk.MaxDataLength..(2 * FileChunk.MaxDataLength)]);
        await WaitUntil(() => bob.Files.GetProgress(first.Id, id) == 2 * FileChunk.MaxDataLength);
        await first.DisposeAsync();
        await WaitUntil(() => bob.Files.IsPaused(bob.IncomingOf(identity.Identity.DeviceId, id)!));

        await using var second = await RawPeer.ConnectAsync(identity, bob, FileCapabilities, ownsIdentity: true);

        var resume = SessionPayloads.TryDeserialize<FileResumePayload>((await second.ReadUntilAsync(RmpMessageTypes.FileResume)).Payload)!;
        Assert.Equal(id, resume.TransferId);
        Assert.Equal(2 * FileChunk.MaxDataLength, resume.Offset);

        await second.SendChunksAsync(id, content, resume.Offset);
        await second.SendAsync(RmpMessageTypes.FileComplete, SessionPayloads.ToElement(new FileCompletePayload(id)));
        var ack = await second.ReadUntilAsync(RmpMessageTypes.FileAck);

        Assert.True(SessionPayloads.TryDeserialize<FileAckPayload>(ack.Payload)!.Verified);
        Assert.Equal(content, File.ReadAllBytes(bob.IncomingOf(second.Id, id)!.LocalPath!));
    }

    [Fact]
    public async Task AfterACrash_OnlyWhatWasRecordedCounts_AndTheRestIsSentAgain()
    {
        var bob = await StartAsync("Bob");
        var identity = TrustedIdentity(bob, "Raw");
        var content = RandomBytes(300_000);
        var first = await RawPeer.ConnectAsync(identity, bob, FileCapabilities);
        await WaitUntil(() => bob.Connections.TryGetSession(first.Id, out _));
        var id = await first.OfferAndAcceptAsync(bob, "a.bin", content);
        await first.SendChunkAsync(id, 0, content[..FileChunk.MaxDataLength]);
        await first.SendChunkAsync(id, 1, content[FileChunk.MaxDataLength..(2 * FileChunk.MaxDataLength)]);
        await WaitUntil(() => bob.Files.GetProgress(first.Id, id) == 2 * FileChunk.MaxDataLength);
        await first.DisposeAsync();
        await WaitUntil(() => bob.Files.IsPaused(bob.IncomingOf(identity.Identity.DeviceId, id)!));

        // The second chunk may never have reached the disk, as a power cut would leave things.
        bob.Store.SaveReceivedBytes(identity.Identity.DeviceId, id, FileChunk.MaxDataLength);
        await File.WriteAllBytesAsync(Path.Combine(bob.IncomingFolder, "crash.part"), [1, 2, 3]);
        bob = await RestartAsync(bob);
        Assert.Equal([$"{id:N}.part"], Directory.GetFiles(bob.IncomingFolder).Select(Path.GetFileName));

        await using var second = await RawPeer.ConnectAsync(identity, bob, FileCapabilities, ownsIdentity: true);

        var resume = SessionPayloads.TryDeserialize<FileResumePayload>((await second.ReadUntilAsync(RmpMessageTypes.FileResume)).Payload)!;
        Assert.Equal(FileChunk.MaxDataLength, resume.Offset);
        await second.SendChunksAsync(id, content, resume.Offset);
        await second.SendAsync(RmpMessageTypes.FileComplete, SessionPayloads.ToElement(new FileCompletePayload(id)));
        await second.ReadUntilAsync(RmpMessageTypes.FileAck);
        Assert.Equal(content, File.ReadAllBytes(bob.IncomingOf(second.Id, id)!.LocalPath!));
    }

    [Fact]
    public async Task ClosingDuringTheCheck_ChecksAgainWithoutSendingAnythingAgain()
    {
        var bob = await StartAsync("Bob");
        var identity = TrustedIdentity(bob, "Raw");
        var content = RandomBytes(1000);
        var first = await RawPeer.ConnectAsync(identity, bob, FileCapabilities);
        await WaitUntil(() => bob.Connections.TryGetSession(first.Id, out _));
        var id = await first.OfferAndAcceptAsync(bob, "a.bin", content);
        await first.SendChunkAsync(id, 0, content);
        await WaitUntil(() => bob.Files.GetProgress(first.Id, id) == content.Length);
        await first.DisposeAsync();
        await WaitUntil(() => bob.Files.IsPaused(bob.IncomingOf(identity.Identity.DeviceId, id)!));

        bob.Store.UpdateTransfer(identity.Identity.DeviceId, MessageDirection.Incoming, id, FileTransferState.Verifying, null, null);
        bob = await RestartAsync(bob);
        Assert.Equal(FileTransferState.Transferring, bob.IncomingOf(identity.Identity.DeviceId, id)!.State);

        await using var second = await RawPeer.ConnectAsync(identity, bob, FileCapabilities, ownsIdentity: true);

        var resume = SessionPayloads.TryDeserialize<FileResumePayload>((await second.ReadUntilAsync(RmpMessageTypes.FileResume)).Payload)!;
        Assert.Equal(content.Length, resume.Offset);
        await second.SendAsync(RmpMessageTypes.FileComplete, SessionPayloads.ToElement(new FileCompletePayload(id)));
        var ack = await second.ReadUntilAsync(RmpMessageTypes.FileAck);
        Assert.True(SessionPayloads.TryDeserialize<FileAckPayload>(ack.Payload)!.Verified);
    }

    [Fact]
    public async Task SenderWhoThinksItFinished_DoesntFailATransferThatIsStillMissingData()
    {
        var bob = await StartAsync("Bob");
        var identity = TrustedIdentity(bob, "Raw");
        var content = RandomBytes(1000);
        var first = await RawPeer.ConnectAsync(identity, bob, FileCapabilities);
        await WaitUntil(() => bob.Connections.TryGetSession(first.Id, out _));
        var id = await first.OfferAndAcceptAsync(bob, "a.bin", content);
        await first.SendChunkAsync(id, 0, content[..400]);
        await WaitUntil(() => bob.Files.GetProgress(first.Id, id) == 400);
        await first.DisposeAsync();
        await WaitUntil(() => bob.Files.IsPaused(bob.IncomingOf(identity.Identity.DeviceId, id)!));

        await using var second = await RawPeer.ConnectAsync(identity, bob, FileCapabilities, ownsIdentity: true);
        await second.SendAsync(RmpMessageTypes.FileComplete, SessionPayloads.ToElement(new FileCompletePayload(id)));

        var resume = SessionPayloads.TryDeserialize<FileResumePayload>((await second.ReadUntilAsync(RmpMessageTypes.FileResume)).Payload)!;
        await second.SendAsync(RmpMessageTypes.FileComplete, SessionPayloads.ToElement(new FileCompletePayload(id)));
        await second.RoundTripAsync();
        Assert.Equal(FileTransferState.Transferring, bob.IncomingOf(second.Id, id)!.State);

        await second.SendChunksAsync(id, content, resume.Offset);
        await second.SendAsync(RmpMessageTypes.FileComplete, SessionPayloads.ToElement(new FileCompletePayload(id)));
        var ack = await second.ReadUntilAsync(RmpMessageTypes.FileAck);
        Assert.True(SessionPayloads.TryDeserialize<FileAckPayload>(ack.Payload)!.Verified);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancelingWhileApart_RemovesWhatArrived_AndTheOtherSideLearnsOnReconnecting(bool receiverCancels)
    {
        var (alice, bob) = await PairedAsync();
        var id = await StartMovingAsync(alice, bob, CreateFile("big.bin", 60 * 1024 * 1024).Path);
        await alice.Connections.DisconnectAsync(bob.Id);
        await WaitUntil(() => bob.Files.IsPaused(bob.Incoming(alice, id)!) && alice.Files.IsPaused(alice.Outgoing(bob, id)!));

        if (receiverCancels)
        {
            Assert.Null((await bob.Files.CancelAsync(alice.Id, MessageDirection.Incoming, id)).Error);
            Assert.Empty(Directory.GetFiles(bob.IncomingFolder));
        }
        else
        {
            Assert.Null((await alice.Files.CancelAsync(bob.Id, MessageDirection.Outgoing, id)).Error);
        }

        await alice.Connections.ConnectAsync(bob.Id);

        await WaitUntil(() => alice.Outgoing(bob, id)!.State == FileTransferState.Canceled && bob.Incoming(alice, id)!.State == FileTransferState.Canceled);
        await WaitUntil(() => Directory.GetFiles(bob.IncomingFolder).Length == 0);
        Assert.Empty(Directory.GetFiles(bob.Downloads));
    }

    [Fact]
    public async Task SourceFileChangedWhileApart_FailsWithAnExplanation_InsteadOfSendingAMixture()
    {
        var (alice, bob) = await PairedAsync();
        var (path, content) = CreateFile("big.bin", 60 * 1024 * 1024);
        var id = await StartMovingAsync(alice, bob, path);
        await alice.Connections.DisconnectAsync(bob.Id);
        await WaitUntil(() => alice.Files.IsPaused(alice.Outgoing(bob, id)!) && bob.Files.IsPaused(bob.Incoming(alice, id)!));

        // The same size, so only the time gives it away.
        content[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(path, content);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        await alice.Connections.ConnectAsync(bob.Id);

        await WaitUntil(() => alice.Outgoing(bob, id)!.State == FileTransferState.Failed && bob.Incoming(alice, id)!.State == FileTransferState.Failed);
        Assert.Contains("changed", alice.Outgoing(bob, id)!.Failure, StringComparison.Ordinal);
        await WaitUntil(() => Directory.GetFiles(bob.IncomingFolder).Length == 0);
        Assert.Empty(Directory.GetFiles(bob.Downloads));
    }

    [Fact]
    public async Task ResumePastTheEndOfTheFile_FailsTheTransfer()
    {
        var alice = await StartAsync("Alice");
        await using var raw = await RawPeerAsync(alice, "Raw");
        var id = (await alice.Files.SendFileAsync(raw.Id, CreateFile("a.bin", 100).Path)).TransferId!.Value;
        await raw.ReadUntilAsync(RmpMessageTypes.FileOffer);

        await raw.SendAsync(RmpMessageTypes.FileResume, SessionPayloads.ToElement(new FileResumePayload(id, 101)));

        await raw.ReadUntilAsync(RmpMessageTypes.FileReject);
        Assert.Equal(FileTransferState.Failed, alice.OutgoingOf(raw.Id, id)!.State);
    }

    [Fact]
    public async Task ResumeWithANegativeOffset_IsMalformed()
    {
        var alice = await StartAsync("Alice");
        await using var raw = await RawPeerAsync(alice, "Raw");
        var id = (await alice.Files.SendFileAsync(raw.Id, CreateFile("a.bin", 100).Path)).TransferId!.Value;
        await raw.ReadUntilAsync(RmpMessageTypes.FileOffer);

        await raw.SendAsync(RmpMessageTypes.FileResume, SessionPayloads.ToElement(new FileResumePayload(id, -1)));

        Assert.Equal(RmpErrorCodes.InvalidFrame, (await raw.ReadErrorAsync()).Code);
        Assert.Equal(FileTransferState.Offered, alice.OutgoingOf(raw.Id, id)!.State);
    }

    [Fact]
    public async Task OnlyTheDeviceOfferedAFile_CanResumeIt()
    {
        var alice = await StartAsync("Alice");
        await using var intended = await RawPeerAsync(alice, "Intended");
        await using var other = await RawPeerAsync(alice, "Other");
        var id = (await alice.Files.SendFileAsync(intended.Id, CreateFile("secret.txt", 100).Path)).TransferId!.Value;
        await intended.ReadUntilAsync(RmpMessageTypes.FileOffer);

        await other.SendAsync(RmpMessageTypes.FileResume, SessionPayloads.ToElement(new FileResumePayload(id, 0)));

        await other.ReadUntilAsync(RmpMessageTypes.FileReject);
        Assert.Equal(FileTransferState.Offered, alice.OutgoingOf(intended.Id, id)!.State);
        var seen = await other.ReadAllUntilHeartbeatAckAsync();
        Assert.DoesNotContain(seen, f => f.Type == RmpFrameType.FileChunk);
    }

    [Fact]
    public async Task SourceFileThatShrinks_FailsWithAnExplanation()
    {
        var alice = await StartAsync("Alice");
        await using var raw = await RawPeerAsync(alice, "Raw");
        var (path, _) = CreateFile("a.bin", 200_000);
        var id = (await alice.Files.SendFileAsync(raw.Id, path)).TransferId!.Value;
        await raw.ReadUntilAsync(RmpMessageTypes.FileOffer);
        File.WriteAllBytes(path, RandomBytes(10));

        await raw.SendAsync(RmpMessageTypes.FileAccept, SessionPayloads.ToElement(new FileAcceptPayload(id)));

        await WaitUntil(() => alice.OutgoingOf(raw.Id, id)!.State == FileTransferState.Failed);
        Assert.Contains("changed", alice.OutgoingOf(raw.Id, id)!.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendingAFileThatIsTooLargeOrMissing_ExplainsWhy()
    {
        var alice = await StartAsync("Alice", new FileOptionsOverride(MaxFile: 1000));
        var bob = await StartAsync("Bob");
        Pair(alice, bob);
        await ConnectAsync(alice, bob);

        Assert.Contains("too large", (await alice.Files.SendFileAsync(bob.Id, CreateFile("huge.bin", 1001).Path)).Error, StringComparison.Ordinal);
        Assert.Null((await alice.Files.SendFileAsync(bob.Id, CreateFile("fits.bin", 1000).Path)).Error);
        Assert.NotNull((await alice.Files.SendFileAsync(bob.Id, Path.Combine(_root, "missing.bin"))).Error);
        Assert.NotNull((await alice.Files.SendFileAsync(Guid.NewGuid(), CreateFile("a.txt", 1).Path)).Error);
    }

    [Fact]
    public async Task DeviceWithoutTheFeature_DoesntGetOffers_AndStillGetsMessages()
    {
        var alice = await StartAsync("Alice");
        await using var old = await RawPeerAsync(alice, "Old", [RmpCapabilities.Text]);
        var id = (await alice.Files.SendFileAsync(old.Id, CreateFile("a.txt", 1).Path)).TransferId!.Value;

        var message = alice.Messaging.SendText(old.Id, "Hi").Message!;
        var seen = await old.ReadAllUntilAsync(RmpMessageTypes.ChatMessage);

        Assert.Equal(message.MessageId, seen[^1].MessageId);
        Assert.DoesNotContain(seen, m => m.Type == RmpMessageTypes.FileOffer);
        Assert.Equal(FileTransferState.Waiting, alice.OutgoingOf(old.Id, id)!.State);
    }

    // ---- Restarts ----

    [Fact]
    public async Task WaitingOffer_SurvivesARestart()
    {
        var alice = await StartAsync("Alice");
        var bob = await StartAsync("Bob");
        Pair(alice, bob);
        var id = (await alice.Files.SendFileAsync(bob.Id, CreateFile("a.txt", 10).Path)).TransferId!.Value;

        alice = await RestartAsync(alice);
        await ConnectAsync(alice, bob);

        await WaitUntil(() => bob.Incoming(alice, id)?.State == FileTransferState.Offered);
    }

    [Fact]
    public async Task UnansweredOffer_SurvivesTheReceiverRestarting_AndCanStillBeAccepted()
    {
        var (alice, bob) = await PairedAsync();
        var (path, content) = CreateFile("a.txt", 10);
        var id = (await alice.Files.SendFileAsync(bob.Id, path)).TransferId!.Value;
        await WaitUntil(() => bob.Incoming(alice, id) is not null);

        bob = await RestartAsync(bob);
        await ConnectAsync(alice, bob);
        await WaitUntil(() => bob.Connections.TryGetSession(alice.Id, out _));
        Assert.Equal(FileTransferState.Offered, bob.Incoming(alice, id)!.State);

        await bob.Files.AcceptAsync(alice.Id, id);

        await WaitUntil(() => alice.Outgoing(bob, id)?.State == FileTransferState.Completed);
        Assert.Equal(content, File.ReadAllBytes(bob.Incoming(alice, id)!.LocalPath!));
    }

    [Fact]
    public async Task SenderThatRestartsAfterSendingEverything_FindsOutHowItEnded()
    {
        var (alice, bob) = await PairedAsync();
        var id = (await alice.Files.SendFileAsync(bob.Id, CreateFile("a.txt", 10).Path)).TransferId!.Value;
        await WaitUntil(() => bob.Incoming(alice, id) is not null);
        await bob.Files.AcceptAsync(alice.Id, id);
        await WaitUntil(() => bob.Incoming(alice, id)!.State == FileTransferState.Completed);
        await WaitUntil(() => alice.Outgoing(bob, id)!.State == FileTransferState.Completed);

        // As if the answer had never arrived.
        alice.Store.UpdateTransfer(bob.Id, MessageDirection.Outgoing, id, FileTransferState.Verifying, null, null);
        alice = await RestartAsync(alice);
        Assert.Equal(FileTransferState.Verifying, alice.Outgoing(bob, id)!.State);

        await ConnectAsync(alice, bob);

        await WaitUntil(() => alice.Outgoing(bob, id)!.State == FileTransferState.Completed);
    }

    // ---- Harness ----

    private static byte[] RandomBytes(int length) => RandomNumberGenerator.GetBytes(length);

    private (string Path, byte[] Content) CreateFile(string name, int size)
    {
        var folder = Path.Combine(_root, "source", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        var content = RandomBytes(size);
        File.WriteAllBytes(path, content);
        return (path, content);
    }

    /// <summary>Returns once data is moving and a good part of the file is still to come.</summary>
    private static async Task<Guid> StartMovingAsync(Node sender, Node receiver, string path)
    {
        var id = (await sender.Files.SendFileAsync(receiver.Id, path)).TransferId!.Value;
        await WaitUntil(() => receiver.Incoming(sender, id) is not null);
        Assert.Null((await receiver.Files.AcceptAsync(sender.Id, id)).Error);
        await WaitUntil(() => receiver.Files.GetProgress(sender.Id, id) > 0);
        return id;
    }

    /// <summary>An identity that stays trusted across several raw connections.</summary>
    private DeviceIdentityStore TrustedIdentity(Node target, string name)
    {
        var identity = NewIdentity(name);
        target.Trust.Trust(new TrustedPeer(identity.Identity.DeviceId, name, identity.Identity.CertificateFingerprint, DateTimeOffset.UtcNow));
        return identity;
    }

    private static async Task<string> TransferAsync(Node sender, Node receiver, string path)
    {
        var id = (await sender.Files.SendFileAsync(receiver.Id, path)).TransferId!.Value;
        await WaitUntil(() => receiver.Incoming(sender, id) is not null);
        Assert.Null((await receiver.Files.AcceptAsync(sender.Id, id)).Error);
        await WaitUntil(() => sender.Outgoing(receiver, id)?.State == FileTransferState.Completed);
        return receiver.Incoming(sender, id)!.LocalPath!;
    }

    private async Task<(Node Alice, Node Bob)> PairedAsync()
    {
        var alice = await StartAsync("Alice");
        var bob = await StartAsync("Bob");
        Pair(alice, bob);
        await ConnectAsync(alice, bob);
        return (alice, bob);
    }

    private static void Pair(Node a, Node b)
    {
        a.Trust.Trust(new TrustedPeer(b.Id, b.Identity.Identity.Username, b.Identity.Identity.CertificateFingerprint, DateTimeOffset.UtcNow));
        b.Trust.Trust(new TrustedPeer(a.Id, a.Identity.Identity.Username, a.Identity.Identity.CertificateFingerprint, DateTimeOffset.UtcNow));
    }

    private static async Task ConnectAsync(Node a, Node b)
    {
        a.Sees(b);
        b.Sees(a);
        await a.Connections.ConnectAsync(b.Id);
        await WaitUntil(() => b.Connections.GetInfo(a.Id).State == PeerConnectionState.ConnectedTrusted
            && a.Connections.GetInfo(b.Id).State == PeerConnectionState.ConnectedTrusted);
    }

    private DeviceIdentityStore NewIdentity(string name)
    {
        var identity = new DeviceIdentityStore(Path.Combine(_root, Guid.NewGuid().ToString("N")));
        identity.LoadOrCreate(name);
        return identity;
    }

    private async Task<RawPeer> RawPeerAsync(Node target, string name, IReadOnlyList<string>? capabilities = null, bool trusted = true)
    {
        var identity = NewIdentity(name);
        if (trusted)
        {
            target.Trust.Trust(new TrustedPeer(identity.Identity.DeviceId, name, identity.Identity.CertificateFingerprint, DateTimeOffset.UtcNow));
        }

        var peer = await RawPeer.ConnectAsync(identity, target, capabilities ?? [RmpCapabilities.Text, RmpCapabilities.FileTransfer], ownsIdentity: true);
        await WaitUntil(() => target.Connections.TryGetSession(peer.Id, out _));
        return peer;
    }

    private sealed record FileOptionsOverride(int? MaxOpenOffers = null, long? MinFree = null, long? MaxFile = null, string? ReceiveFolder = null);

    private Task<Node> StartAsync(string username, FileOptionsOverride? overrides = null)
    {
        var folder = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        return StartAsync(NewIdentity(username), new InMemoryTrustStore(), folder, overrides);
    }

    private async Task<Node> RestartAsync(Node node)
    {
        _cleanup.Remove(node);
        await node.StopAsync();
        return await StartAsync(node.Identity, node.Trust, node.Folder, null);
    }

    private async Task<Node> StartAsync(DeviceIdentityStore identity, InMemoryTrustStore trust, string folder, FileOptionsOverride? overrides)
    {
        Directory.CreateDirectory(folder);
        var registry = new PeerRegistry(TimeSpan.FromMinutes(1));
        var store = new SqliteMessageStore(Path.Combine(folder, "messenger.db"));
        var connections = new ConnectionManager(identity, identity, new StaticInterfaces(), registry, trust,
            new ConnectionOptions
            {
                Port = 0,
                AppVersion = "0.9.0",
                Capabilities = [RmpCapabilities.Text, RmpCapabilities.FileTransfer],
                MaintenanceInterval = TimeSpan.FromMilliseconds(20),
                ReconnectDelays = [TimeSpan.FromHours(1)],
                LargerIdDialDelay = TimeSpan.FromHours(1),
            },
            TimeProvider.System, NullLogger<ConnectionManager>.Instance);
        var groups = new GroupService(connections, store, trust, identity, identity, TimeProvider.System, NullLogger<GroupService>.Instance);
        var messaging = new MessagingService(connections, store, trust, identity, groups, new MessagingOptions { SweepInterval = TimeSpan.FromMilliseconds(50) }, TimeProvider.System, NullLogger<MessagingService>.Instance);
        var defaults = new FileTransferOptions { DownloadsDirectory = Path.Combine(folder, "downloads"), IncomingDirectory = Path.Combine(folder, "incoming") };
        var options = new FileTransferOptions
        {
            DownloadsDirectory = defaults.DownloadsDirectory,
            IncomingDirectory = defaults.IncomingDirectory,
            ReceiveFolder = overrides?.ReceiveFolder,
            MaxOpenOffersPerDevice = overrides?.MaxOpenOffers ?? defaults.MaxOpenOffersPerDevice,
            MinFreeBytesAfterAccept = overrides?.MinFree ?? 0,
            MaxFileSizeBytes = overrides?.MaxFile ?? defaults.MaxFileSizeBytes,
        };
        Directory.CreateDirectory(options.DownloadsDirectory);
        var files = new FileTransferService(connections, store, trust, options, TimeProvider.System, NullLogger<FileTransferService>.Instance);

        var node = new Node(identity, registry, trust, store, folder, connections, groups, messaging, files);
        _cleanup.Add(node);
        await connections.StartAsync(CancellationToken.None);
        await messaging.StartAsync(CancellationToken.None);
        await files.StartAsync(CancellationToken.None);
        await WaitUntil(() => connections.ListeningEndPoints.Count == 1);
        return node;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(30))
            {
                Assert.Fail("Timed out waiting for the condition.");
            }

            await Task.Delay(20);
        }
    }

    private sealed class StaticInterfaces : ILanInterfaceProvider
    {
        public event EventHandler? InterfacesChanged
        {
            add { }
            remove { }
        }

        public IReadOnlyList<LanInterface> GetEligibleInterfaces() => [Loopback];
    }

    private sealed class Node(DeviceIdentityStore identity, PeerRegistry registry, InMemoryTrustStore trust, SqliteMessageStore store, string folder, ConnectionManager connections, GroupService groups, MessagingService messaging, FileTransferService files) : IAsyncDisposable
    {
        public DeviceIdentityStore Identity { get; } = identity;

        public InMemoryTrustStore Trust { get; } = trust;

        public SqliteMessageStore Store { get; } = store;

        public string Folder { get; } = folder;

        public string Downloads => Path.Combine(Folder, "downloads");

        public string IncomingFolder => Path.Combine(Folder, "incoming");

        public ConnectionManager Connections { get; } = connections;

        public MessagingService Messaging { get; } = messaging;

        public FileTransferService Files { get; } = files;

        public Guid Id => Identity.Identity.DeviceId;

        public int Port => Connections.ListeningEndPoints[0].Port;

        public StoredTransfer? Incoming(Node from, Guid id) => IncomingOf(from.Id, id);

        public StoredTransfer? Outgoing(Node to, Guid id) => OutgoingOf(to.Id, id);

        public StoredTransfer? IncomingOf(Guid from, Guid id) => Store.GetTransfer(from, MessageDirection.Incoming, id);

        public StoredTransfer? OutgoingOf(Guid to, Guid id) => Store.GetTransfer(to, MessageDirection.Outgoing, id);

        public void Sees(Node other) => registry.Observe(
            new DiscoveryPacket(DiscoveryPacketType.Announce, other.Id, other.Identity.Identity.Username, other.Port, "0.9.0"),
            IPAddress.Loopback,
            DateTimeOffset.UtcNow);

        public async Task StopAsync()
        {
            await Files.StopAsync(CancellationToken.None);
            Files.Dispose();
            await Messaging.StopAsync(CancellationToken.None);
            Messaging.Dispose();
            groups.Dispose();
            await Connections.StopAsync(CancellationToken.None);
            Connections.Dispose();
            await Task.Delay(50);
            Store.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            Identity.Dispose();
        }
    }

    private sealed class RawPeer(DeviceIdentityStore identity, SslStream ssl, Guid remoteId, bool ownsIdentity) : IAsyncDisposable
    {
        private bool _disposed;

        public Guid Id => identity.Identity.DeviceId;

        public static async Task<RawPeer> ConnectAsync(DeviceIdentityStore identity, Node target, IReadOnlyList<string> capabilities, bool ownsIdentity = false)
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, target.Port);
            var credentials = SslStreamCertificateContext.Create(identity.TlsCertificate, null, offline: true);
            var (ssl, _) = await TlsHandshake.AuthenticateAsClientAsync(client.GetStream(), credentials, target.Id, TimeProvider.System, CancellationToken.None);
            var peer = new RawPeer(identity, ssl, target.Id, ownsIdentity);
            await peer.SendAsync(RmpMessageTypes.Hello, SessionPayloads.ToElement(new HelloPayload(1, 1, "0.9.0", capabilities)));
            await peer.ReadUntilAsync(RmpMessageTypes.Hello);
            return peer;
        }

        public static JsonElement OfferPayload(Guid id, string name, byte[] content) => SessionPayloads.ToElement(
            new FileOfferPayload(id, name, content.Length, Convert.ToHexStringLower(SHA256.HashData(content)), "application/octet-stream"));

        public async Task<Guid> OfferAsync(Node target, string name, byte[] content)
        {
            var id = Guid.CreateVersion7();
            await SendAsync(RmpMessageTypes.FileOffer, OfferPayload(id, name, content));
            await WaitUntil(() => target.IncomingOf(Id, id) is not null);
            return id;
        }

        public async Task<Guid> OfferAndAcceptAsync(Node target, string name, byte[] content)
        {
            var id = await OfferAsync(target, name, content);
            Assert.Null((await target.Files.AcceptAsync(Id, id)).Error);
            await ReadUntilAsync(RmpMessageTypes.FileAccept);
            return id;
        }

        public Task SendChunkAsync(Guid id, uint sequence, byte[] data) => SendFrameAsync(FileChunk.Encode(id, sequence, data));

        /// <summary>Everything from an offset on, numbered from 0 as after a FILE_RESUME.</summary>
        public async Task SendChunksAsync(Guid id, byte[] content, long offset)
        {
            uint sequence = 0;
            for (var at = (int)offset; at < content.Length; at += FileChunk.MaxDataLength)
            {
                await SendChunkAsync(id, sequence++, content[at..Math.Min(at + FileChunk.MaxDataLength, content.Length)]);
            }
        }

        public async Task SendFrameAsync(byte[] frame)
        {
            await ssl.WriteAsync(frame);
            await ssl.FlushAsync();
        }

        public Task SendAsync(string type, JsonElement payload, Guid? messageId = null) => SendFrameAsync(RmpFraming.Encode(
            RmpFrameType.Control,
            RmpEnvelopeCodec.Encode(new RmpEnvelope(type, messageId ?? Guid.CreateVersion7(), Id, remoteId, DateTimeOffset.UtcNow, payload))));

        public async Task<RmpEnvelope> ReadUntilAsync(string type) => (await ReadAllUntilAsync(type))[^1];

        public async Task<List<RmpEnvelope>> ReadAllUntilAsync(string type)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var seen = new List<RmpEnvelope>();
            while (true)
            {
                var frame = await RmpFraming.ReadAsync(ssl, timeout.Token) ?? throw new EndOfStreamException();
                if (frame.Type != RmpFrameType.Control)
                {
                    continue;
                }

                Assert.True(RmpEnvelopeCodec.TryDecode(frame.Payload, out var envelope, out _));
                seen.Add(envelope);
                if (envelope.Type == type)
                {
                    return seen;
                }
            }
        }

        public async Task<RmpFrame> ReadFrameAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            return await RmpFraming.ReadAsync(ssl, timeout.Token) ?? throw new EndOfStreamException();
        }

        public async Task<List<RmpFrame>> ReadAllUntilHeartbeatAckAsync()
        {
            await SendAsync(RmpMessageTypes.Heartbeat, SessionPayloads.Empty);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var seen = new List<RmpFrame>();
            while (true)
            {
                var frame = await RmpFraming.ReadAsync(ssl, timeout.Token) ?? throw new EndOfStreamException();
                seen.Add(frame);
                if (frame.Type == RmpFrameType.Control
                    && RmpEnvelopeCodec.TryDecode(frame.Payload, out var envelope, out _)
                    && envelope.Type == RmpMessageTypes.HeartbeatAck)
                {
                    return seen;
                }
            }
        }

        public async Task<ErrorPayload> ReadErrorAsync()
        {
            var error = await ReadUntilAsync(RmpMessageTypes.Error);
            Assert.True(SessionPayloads.TryReadError(error.Payload, out var payload));
            return payload;
        }

        public async Task RoundTripAsync()
        {
            await SendAsync(RmpMessageTypes.Heartbeat, SessionPayloads.Empty);
            await ReadUntilAsync(RmpMessageTypes.HeartbeatAck);
        }

        public async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                await ssl.DisposeAsync();
                if (ownsIdentity)
                {
                    identity.Dispose();
                }
            }
        }
    }
}
