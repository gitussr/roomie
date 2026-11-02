using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Roomie.Core.Connections;
using Roomie.Core.Discovery;
using Roomie.Core.Messaging;
using Roomie.Core.Protocol;
using Roomie.Core.Trust;
using Roomie.Infrastructure.Identity;
using Roomie.Infrastructure.Messaging;
using Roomie.Network.Connections;
using Roomie.Network.Discovery;
using Roomie.Network.Messaging;

namespace Roomie.Tests.Messaging;

/// <summary>Messaging between real connection managers over loopback TLS, and against a hand-driven peer.</summary>
public sealed class MessagingServiceTests : IAsyncLifetime
{
    private static readonly LanInterface Loopback = new("lo", "Loopback", new Ipv4Subnet(IPAddress.Loopback, 8));

    private readonly string _root = Path.Combine(Path.GetTempPath(), "roomie-tests", Guid.NewGuid().ToString("N"));
    private readonly List<IAsyncDisposable> _cleanup = [];

    public Task InitializeAsync() => Task.CompletedTask;

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

    [Fact]
    public async Task Message_IsDeliveredStoredAndAcknowledged()
    {
        var (alice, bob) = await PairedAsync();

        var sent = alice.Messaging.SendText(bob.Id, "Hello Bob!");
        Assert.Null(sent.Error);

        await WaitUntil(() => alice.StateOf(sent.Message!.MessageId) == DeliveryState.Delivered);

        var received = Assert.Single(bob.Store.GetConversation(alice.Id));
        Assert.Equal("Hello Bob!", received.Content);
        Assert.Equal(sent.Message!.MessageId, received.MessageId);
        Assert.Equal(MessageDirection.Incoming, received.Direction);
        Assert.Equal(1, bob.Store.GetActiveConversations().Single().UnreadCount);
    }

    [Fact]
    public async Task ReadReceipt_ReachesTheSender_OnlyWhenTheRecipientReads()
    {
        var (alice, bob) = await PairedAsync();
        var sent = alice.Messaging.SendText(bob.Id, "Hello").Message!;
        await WaitUntil(() => alice.StateOf(sent.MessageId) == DeliveryState.Delivered);

        await Task.Delay(100);
        Assert.Equal(DeliveryState.Delivered, alice.StateOf(sent.MessageId));

        bob.Messaging.MarkRead(alice.Id);

        await WaitUntil(() => alice.StateOf(sent.MessageId) == DeliveryState.Read);
        await WaitUntil(() => bob.Store.GetPendingReadReceipts(alice.Id).Count == 0);
    }

    [Fact]
    public async Task ManyReadReceipts_GoOutInBatches()
    {
        var (alice, bob) = await PairedAsync();
        var ids = Enumerable.Range(0, ContentPayloads.MaxReadReceiptsPerMessage + 5)
            .Select(i => alice.Messaging.SendText(bob.Id, $"m{i}").Message!.MessageId).ToList();
        await WaitUntil(() => ids.All(id => alice.StateOf(id) == DeliveryState.Delivered));

        bob.Messaging.MarkRead(alice.Id);

        await WaitUntil(() => ids.All(id => alice.StateOf(id) == DeliveryState.Read));
    }

    [Fact]
    public async Task MessagesArriveInSendingOrder_AndBothDirectionsWork()
    {
        var (alice, bob) = await PairedAsync();
        var texts = Enumerable.Range(0, 40).Select(i => $"message {i}").ToList();

        foreach (var text in texts)
        {
            Assert.Null(alice.Messaging.SendText(bob.Id, text).Error);
        }

        var reply = bob.Messaging.SendText(alice.Id, "Got them").Message!;

        await WaitUntil(() => alice.Store.GetUndelivered(bob.Id).Count == 0 && bob.Store.GetUndelivered(alice.Id).Count == 0);
        Assert.Equal(texts, bob.Store.GetConversation(alice.Id).Where(m => m.Direction == MessageDirection.Incoming).Select(m => m.Content));
        Assert.Contains(alice.Store.GetConversation(bob.Id), m => m.MessageId == reply.MessageId && m.Direction == MessageDirection.Incoming);
    }

    [Fact]
    public async Task FullSizeUnicodeMessage_ArrivesIntact()
    {
        var (alice, bob) = await PairedAsync();
        var body = string.Concat(Enumerable.Repeat("नमस्ते \"😀\"\n", 512))[..4000];
        body = ContentPayloads.NormalizeText(body);

        var sent = alice.Messaging.SendText(bob.Id, body).Message!;

        await WaitUntil(() => alice.StateOf(sent.MessageId) == DeliveryState.Delivered);
        Assert.Equal(body, bob.Store.GetConversation(alice.Id).Single().Content);
    }

    [Fact]
    public async Task MessageToAnOfflineDevice_WaitsAndIsDeliveredOnConnecting()
    {
        var (alice, bob) = await PairedAsync(connect: false);

        var sent = alice.Messaging.SendText(bob.Id, "While you were out").Message!;
        await Task.Delay(100);
        Assert.Equal(DeliveryState.Pending, alice.StateOf(sent.MessageId));

        alice.Sees(bob);
        await alice.Connections.ConnectAsync(bob.Id);

        await WaitUntil(() => alice.StateOf(sent.MessageId) == DeliveryState.Delivered);
        Assert.Equal("While you were out", bob.Store.GetConversation(alice.Id).Single().Content);
    }

    [Fact]
    public async Task LostAcknowledgement_IsRetriedWithTheSameId_AndStoredOnce()
    {
        var alice = await StartAsync("Alice", new MessagingOptions { AckTimeout = TimeSpan.FromMilliseconds(100), SweepInterval = TimeSpan.FromMilliseconds(20) });
        await using var raw = await RawPeerAsync(alice, ["text"]);
        var sent = alice.Messaging.SendText(raw.Id, "Retry me").Message!;

        var first = await raw.ReadUntilAsync(RmpMessageTypes.ChatMessage);
        var second = await raw.ReadUntilAsync(RmpMessageTypes.ChatMessage);

        Assert.Equal(sent.MessageId, first.MessageId);
        Assert.Equal(first.MessageId, second.MessageId);
        Assert.Equal(first.Timestamp, second.Timestamp);

        await raw.SendAsync(RmpMessageTypes.MessageAck, SessionPayloads.ToElement(new MessageAckPayload(sent.MessageId)));
        await WaitUntil(() => alice.StateOf(sent.MessageId) == DeliveryState.Delivered);
    }

    [Fact]
    public async Task DuplicateMessage_IsStoredOnce_AndAcknowledgedAgain()
    {
        var bob = await StartAsync("Bob");
        await using var raw = await RawPeerAsync(bob, ["text"]);
        var id = Guid.CreateVersion7();
        var payload = raw.ChatPayload(bob.Id, "Once");

        await raw.SendAsync(RmpMessageTypes.ChatMessage, payload, id);
        await raw.SendAsync(RmpMessageTypes.ChatMessage, payload, id);

        for (var i = 0; i < 2; i++)
        {
            var ack = await raw.ReadUntilAsync(RmpMessageTypes.MessageAck);
            Assert.True(ContentPayloads.TryReadMessageAck(ack.Payload, out var read));
            Assert.Equal(id, read.MessageId);
        }

        Assert.Single(bob.Store.GetConversation(raw.Id));
    }

    [Fact]
    public async Task ConnectionLostBeforeTheAcknowledgement_MessageIsSentAgainOnTheNextSession()
    {
        var alice = await StartAsync("Alice");
        var store = CreateStore("Raw");
        alice.Trust.Trust(new TrustedPeer(store.Identity.DeviceId, "Raw", store.Identity.CertificateFingerprint, DateTimeOffset.UtcNow));

        var firstRaw = await RawPeer.ConnectAsync(store, alice, ["text"], ownsStore: false);
        await WaitUntil(() => alice.Connections.TryGetSession(firstRaw.Id, out _));
        var sent = alice.Messaging.SendText(firstRaw.Id, "Are you there?").Message!;
        await firstRaw.ReadUntilAsync(RmpMessageTypes.ChatMessage);
        await firstRaw.DisposeAsync();
        await WaitUntil(() => !alice.Connections.TryGetSession(firstRaw.Id, out _));
        Assert.Equal(DeliveryState.Sent, alice.StateOf(sent.MessageId));

        await using var secondRaw = await RawPeer.ConnectAsync(store, alice, ["text"], ownsStore: true);

        var resent = await secondRaw.ReadUntilAsync(RmpMessageTypes.ChatMessage);
        Assert.Equal(sent.MessageId, resent.MessageId);
    }

    [Fact]
    public async Task UnacknowledgedMessages_AreLimited_AndAnAckReleasesTheNext()
    {
        var alice = await StartAsync("Alice", new MessagingOptions { MaxInFlight = 3 });
        await using var raw = await RawPeerAsync(alice, ["text"]);
        var ids = Enumerable.Range(0, 5).Select(i => alice.Messaging.SendText(raw.Id, $"m{i}").Message!.MessageId).ToList();

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(ids[i], (await raw.ReadUntilAsync(RmpMessageTypes.ChatMessage)).MessageId);
        }

        await WaitUntil(() => alice.StateOf(ids[2]) == DeliveryState.Sent);
        await Task.Delay(100);
        Assert.Equal(DeliveryState.Pending, alice.StateOf(ids[3]));

        await raw.SendAsync(RmpMessageTypes.MessageAck, SessionPayloads.ToElement(new MessageAckPayload(ids[0])));

        Assert.Equal(ids[3], (await raw.ReadUntilAsync(RmpMessageTypes.ChatMessage)).MessageId);
    }

    [Fact]
    public async Task AcknowledgementFromTheWrongDevice_ChangesNothing()
    {
        var alice = await StartAsync("Alice");
        await using var intended = await RawPeerAsync(alice, ["text"]);
        await using var other = await RawPeerAsync(alice, ["text"]);
        var sent = alice.Messaging.SendText(intended.Id, "For you only").Message!;
        await intended.ReadUntilAsync(RmpMessageTypes.ChatMessage);

        await other.SendAsync(RmpMessageTypes.MessageAck, SessionPayloads.ToElement(new MessageAckPayload(sent.MessageId)));
        await other.SendAsync(RmpMessageTypes.MessageRead, SessionPayloads.ToElement(new MessageReadPayload([sent.MessageId])));
        await other.SendAsync(RmpMessageTypes.Heartbeat, SessionPayloads.Empty);
        await other.ReadUntilAsync(RmpMessageTypes.HeartbeatAck);

        Assert.Equal(DeliveryState.Sent, alice.StateOf(sent.MessageId));
    }

    [Theory]
    [InlineData(RmpErrorCodes.MessageTooLarge)]
    [InlineData(RmpErrorCodes.InvalidFrame)]
    public async Task PermanentRefusal_MarksTheMessageFailed(string code)
    {
        var alice = await StartAsync("Alice");
        await using var raw = await RawPeerAsync(alice, ["text"]);
        var sent = alice.Messaging.SendText(raw.Id, "Refuse me").Message!;
        await raw.ReadUntilAsync(RmpMessageTypes.ChatMessage);

        await raw.SendAsync(RmpMessageTypes.Error, SessionPayloads.ToElement(new ErrorPayload(code, "No.", sent.MessageId)));

        await WaitUntil(() => alice.StateOf(sent.MessageId) == DeliveryState.Failed);
        Assert.Empty(alice.Store.GetUndelivered(raw.Id));
    }

    [Fact]
    public async Task PeerThatNoLongerTrustsUs_StopsSendingOnThatSession_UntilPairedAgain()
    {
        var alice = await StartAsync("Alice");
        await using var raw = await RawPeerAsync(alice, ["text"]);
        var first = alice.Messaging.SendText(raw.Id, "First").Message!;
        await raw.ReadUntilAsync(RmpMessageTypes.ChatMessage);

        await raw.SendAsync(RmpMessageTypes.Error, SessionPayloads.ToElement(new ErrorPayload(RmpErrorCodes.PeerNotTrusted, "Not paired.", first.MessageId)));
        await WaitUntil(() => alice.Messaging.GetDeliveryProblem(raw.Id) is not null);
        Assert.Contains("no longer trusts", alice.Messaging.GetDeliveryProblem(raw.Id), StringComparison.Ordinal);

        var second = alice.Messaging.SendText(raw.Id, "Second").Message!;
        await Task.Delay(100);
        Assert.Equal(DeliveryState.Pending, alice.StateOf(second.MessageId));

        // Any trust change, such as pairing again, lets messages flow.
        alice.Trust.Block(Guid.NewGuid());

        Assert.Equal(second.MessageId, (await raw.ReadUntilAsync(RmpMessageTypes.ChatMessage)).MessageId);
        Assert.Null(alice.Messaging.GetDeliveryProblem(raw.Id));
    }

    [Fact]
    public async Task PeerWithoutMessaging_MessagesWaitWithAnExplanation()
    {
        var alice = await StartAsync("Alice");
        await using var raw = await RawPeerAsync(alice, []);
        await WaitUntil(() => alice.Connections.TryGetSession(raw.Id, out _));

        var sent = alice.Messaging.SendText(raw.Id, "Can you read this?").Message!;

        await Task.Delay(100);
        Assert.Equal(DeliveryState.Pending, alice.StateOf(sent.MessageId));
        Assert.Contains("without messaging", alice.Messaging.GetDeliveryProblem(raw.Id), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_ToAnUnpairedDevice_IsRefusedAndNothingIsStored()
    {
        var alice = await StartAsync("Alice");
        var stranger = Guid.NewGuid();

        var result = alice.Messaging.SendText(stranger, "Hi");

        Assert.NotNull(result.Error);
        Assert.Empty(alice.Store.GetConversation(stranger));
    }

    [Fact]
    public async Task Send_ToABlockedDevice_IsRefused()
    {
        var (alice, bob) = await PairedAsync(connect: false);
        alice.Trust.Block(bob.Id);

        Assert.NotNull(alice.Messaging.SendText(bob.Id, "Hi").Error);
    }

    [Fact]
    public async Task Send_ChecksAndCleansTheText()
    {
        var (alice, bob) = await PairedAsync(connect: false);

        Assert.NotNull(alice.Messaging.SendText(bob.Id, "  \r\n ").Error);
        var tooLong = alice.Messaging.SendText(bob.Id, new string('x', RmpContentLimits.MaxTextBodyBytes + 1));
        Assert.Contains("too long", tooLong.Error, StringComparison.Ordinal);
        Assert.Empty(alice.Store.GetConversation(bob.Id));

        var cleaned = alice.Messaging.SendText(bob.Id, " one\u0007\r\ntwo\u001b ");
        Assert.Equal("one\r\ntwo", cleaned.Message!.Content);
    }

    [Fact]
    public async Task UnpairedPeer_CantDeliverAMessage()
    {
        var bob = await StartAsync("Bob");
        var store = CreateStore("Raw");
        await using var raw = await RawPeer.ConnectAsync(store, bob, ["text"], ownsStore: true);

        await raw.SendAsync(RmpMessageTypes.ChatMessage, raw.ChatPayload(bob.Id, "Let me in"));

        Assert.Equal(RmpErrorCodes.PeerNotTrusted, (await raw.ReadErrorAsync()).Code);
        Assert.Empty(bob.Store.GetConversation(raw.Id));
    }

    [Fact]
    public async Task MessageForAnotherConversation_IsRefusedAndNotStored()
    {
        var bob = await StartAsync("Bob");
        await using var raw = await RawPeerAsync(bob, ["text"]);
        var wrong = SessionPayloads.ToElement(new ChatMessagePayload("someone-elses-chat", RmpContentTypes.Text, "Hi"));

        await raw.SendAsync(RmpMessageTypes.ChatMessage, wrong);

        Assert.Equal(RmpErrorCodes.InvalidFrame, (await raw.ReadErrorAsync()).Code);
        Assert.Empty(bob.Store.GetConversation(raw.Id));
    }

    [Fact]
    public async Task GroupMessage_IsRefusedUntilGroupsExist()
    {
        var bob = await StartAsync("Bob");
        await using var raw = await RawPeerAsync(bob, ["text"]);
        var group = SessionPayloads.ToElement(new ChatMessagePayload(DirectConversation.IdFor(raw.Id, bob.Id), RmpContentTypes.Text, "Hi", "group-1", 1));

        await raw.SendAsync(RmpMessageTypes.ChatMessage, group);

        Assert.Equal(RmpErrorCodes.UnsupportedMessage, (await raw.ReadErrorAsync()).Code);
        Assert.Empty(bob.Store.GetConversation(raw.Id));
    }

    [Fact]
    public async Task WaitingMessage_SurvivesARestart_AndIsDeliveredWithTheSameIdAndTimestamp()
    {
        var (alice, bob) = await PairedAsync(connect: false);
        var sent = alice.Messaging.SendText(bob.Id, "Sent before closing").Message!;
        await Task.Delay(100);

        alice = await RestartAsync(alice);
        Assert.Equal(DeliveryState.Pending, alice.StateOf(sent.MessageId));

        alice.Sees(bob);
        await alice.Connections.ConnectAsync(bob.Id);

        await WaitUntil(() => alice.StateOf(sent.MessageId) == DeliveryState.Delivered);
        var received = bob.Store.GetConversation(alice.Id).Single();
        Assert.Equal(sent.MessageId, received.MessageId);
        Assert.Equal(sent.Timestamp, received.Timestamp);
        Assert.Equal("Sent before closing", received.Content);
    }

    [Fact]
    public async Task UnacknowledgedMessage_IsSentAgainAfterTheSenderRestarts()
    {
        var alice = await StartAsync("Alice");
        var rawIdentity = CreateStore("Raw");
        alice.Trust.Trust(new TrustedPeer(rawIdentity.Identity.DeviceId, "Raw", rawIdentity.Identity.CertificateFingerprint, DateTimeOffset.UtcNow));
        var firstRaw = await RawPeer.ConnectAsync(rawIdentity, alice, ["text"], ownsStore: false);
        await WaitUntil(() => alice.Connections.TryGetSession(firstRaw.Id, out _));
        var sent = alice.Messaging.SendText(firstRaw.Id, "No ack for this").Message!;
        var first = await firstRaw.ReadUntilAsync(RmpMessageTypes.ChatMessage);
        await WaitUntil(() => alice.StateOf(sent.MessageId) == DeliveryState.Sent);
        await firstRaw.DisposeAsync();

        alice = await RestartAsync(alice);
        await using var secondRaw = await RawPeer.ConnectAsync(rawIdentity, alice, ["text"], ownsStore: true);

        var again = await secondRaw.ReadUntilAsync(RmpMessageTypes.ChatMessage);
        Assert.Equal(first.MessageId, again.MessageId);
        Assert.Equal(first.Timestamp, again.Timestamp);
    }

    [Fact]
    public async Task DuplicateAfterTheRecipientRestarts_IsStoredOnce_AndAcknowledgedAgain()
    {
        var bob = await StartAsync("Bob");
        var rawIdentity = CreateStore("Raw");
        bob.Trust.Trust(new TrustedPeer(rawIdentity.Identity.DeviceId, "Raw", rawIdentity.Identity.CertificateFingerprint, DateTimeOffset.UtcNow));
        var firstRaw = await RawPeer.ConnectAsync(rawIdentity, bob, ["text"], ownsStore: false);
        var id = Guid.CreateVersion7();
        await firstRaw.SendAsync(RmpMessageTypes.ChatMessage, firstRaw.ChatPayload(bob.Id, "Once"), id);
        await firstRaw.ReadUntilAsync(RmpMessageTypes.MessageAck);
        await firstRaw.DisposeAsync();

        bob = await RestartAsync(bob);
        await using var secondRaw = await RawPeer.ConnectAsync(rawIdentity, bob, ["text"], ownsStore: true);
        await secondRaw.SendAsync(RmpMessageTypes.ChatMessage, secondRaw.ChatPayload(bob.Id, "Once"), id);

        var ack = await secondRaw.ReadUntilAsync(RmpMessageTypes.MessageAck);
        Assert.True(ContentPayloads.TryReadMessageAck(ack.Payload, out var read));
        Assert.Equal(id, read.MessageId);
        Assert.Single(bob.Store.GetConversation(secondRaw.Id));
    }

    [Fact]
    public async Task ReadReceiptOwed_SurvivesARestart()
    {
        var (alice, bob) = await PairedAsync();
        var sent = alice.Messaging.SendText(bob.Id, "Read me later").Message!;
        await WaitUntil(() => alice.StateOf(sent.MessageId) == DeliveryState.Delivered);
        await alice.Connections.DisconnectAsync(bob.Id);
        await WaitUntil(() => !bob.Connections.TryGetSession(alice.Id, out _));

        bob.Messaging.MarkRead(alice.Id);
        bob = await RestartAsync(bob);
        Assert.Equal(DeliveryState.Delivered, alice.StateOf(sent.MessageId));
        Assert.Single(bob.Store.GetPendingReadReceipts(alice.Id));

        bob.Sees(alice);
        await bob.Connections.ConnectAsync(alice.Id);

        await WaitUntil(() => alice.StateOf(sent.MessageId) == DeliveryState.Read);
        await WaitUntil(() => bob.Store.GetPendingReadReceipts(alice.Id).Count == 0);
    }

    [Fact]
    public async Task MessageThatCantBeSaved_IsNotAcknowledged_AndArrivesOnceTheDiskRecovers()
    {
        var bob = await StartAsync("Bob");
        await using var raw = await RawPeerAsync(bob, ["text"]);
        var id = Guid.CreateVersion7();
        bob.Store.Fail = true;

        await raw.SendAsync(RmpMessageTypes.ChatMessage, raw.ChatPayload(bob.Id, "Save me"), id);

        Assert.Equal(RmpErrorCodes.InternalError, (await raw.ReadErrorAsync()).Code);

        bob.Store.Fail = false;
        await raw.SendAsync(RmpMessageTypes.ChatMessage, raw.ChatPayload(bob.Id, "Save me"), id);

        var ack = await raw.ReadUntilAsync(RmpMessageTypes.MessageAck);
        Assert.True(ContentPayloads.TryReadMessageAck(ack.Payload, out var read));
        Assert.Equal(id, read.MessageId);
        Assert.Single(bob.Store.GetConversation(raw.Id));
    }

    [Fact]
    public async Task SendingWhileTheDiskFails_ReportsAnError_AndSendingWorksAgainAfterwards()
    {
        var (alice, bob) = await PairedAsync();
        alice.Store.Fail = true;

        var failed = alice.Messaging.SendText(bob.Id, "Not saved");
        Assert.Contains("couldn't save", failed.Error, StringComparison.Ordinal);
        Assert.Null(failed.Message);

        // Sweeps and the worker run into the failure too, and must survive it.
        alice.Messaging.MarkRead(bob.Id);
        await Task.Delay(200);
        alice.Store.Fail = false;

        var sent = alice.Messaging.SendText(bob.Id, "Saved").Message!;

        await WaitUntil(() => alice.StateOf(sent.MessageId) == DeliveryState.Delivered);
        Assert.Equal("Saved", bob.Store.GetConversation(alice.Id).Single().Content);
    }

    [Fact]
    public async Task WorkerThatFailsToReadTheStore_RetriesAfterTheDiskRecovers()
    {
        var (alice, bob) = await PairedAsync();
        var sent = alice.Messaging.SendText(bob.Id, "First").Message!;
        await WaitUntil(() => alice.StateOf(sent.MessageId) == DeliveryState.Delivered);
        await alice.Connections.DisconnectAsync(bob.Id);
        var waiting = alice.Messaging.SendText(bob.Id, "Second").Message!;

        alice.Store.Fail = true;
        alice.Sees(bob);
        await alice.Connections.ConnectAsync(bob.Id);
        await Task.Delay(200);
        alice.Store.Fail = false;

        await WaitUntil(() => alice.StateOf(waiting.MessageId) == DeliveryState.Delivered);
    }

    private async Task<(Node Alice, Node Bob)> PairedAsync(bool connect = true)
    {
        var alice = await StartAsync("Alice");
        var bob = await StartAsync("Bob");
        alice.Trusts(bob);
        bob.Trusts(alice);

        // Once paired devices can see each other, they connect on their own.
        if (connect)
        {
            alice.Sees(bob);
            bob.Sees(alice);
            await alice.Connections.ConnectAsync(bob.Id);
            await WaitUntil(() => bob.Connections.GetInfo(alice.Id).State == PeerConnectionState.ConnectedTrusted);
        }

        return (alice, bob);
    }

    private DeviceIdentityStore CreateStore(string username)
    {
        var store = new DeviceIdentityStore(Path.Combine(_root, Guid.NewGuid().ToString("N")));
        store.LoadOrCreate(username);
        return store;
    }

    /// <summary>A peer that <paramref name="target"/> trusts, driven by hand.</summary>
    private async Task<RawPeer> RawPeerAsync(Node target, IReadOnlyList<string> capabilities)
    {
        var store = CreateStore("Raw");
        target.Trust.Trust(new TrustedPeer(store.Identity.DeviceId, "Raw", store.Identity.CertificateFingerprint, DateTimeOffset.UtcNow));
        var raw = await RawPeer.ConnectAsync(store, target, capabilities, ownsStore: true);
        await WaitUntil(() => target.Connections.TryGetSession(raw.Id, out _));
        return raw;
    }

    private Task<Node> StartAsync(string username, MessagingOptions? messaging = null) =>
        StartAsync(CreateStore(username), new InMemoryTrustStore(), Path.Combine(_root, $"{Guid.NewGuid():N}.db"), messaging);

    /// <summary>Stops everything and starts again from what was saved, as closing and reopening Roomie does.</summary>
    private async Task<Node> RestartAsync(Node node)
    {
        _cleanup.Remove(node);
        await node.StopAsync();
        return await StartAsync(node.Identity, node.Trust, node.DatabasePath, null);
    }

    private async Task<Node> StartAsync(DeviceIdentityStore identity, InMemoryTrustStore trust, string databasePath, MessagingOptions? messaging)
    {
        var registry = new PeerRegistry(TimeSpan.FromMinutes(1));
        var store = new FlakyStore(new SqliteMessageStore(databasePath));
        var connections = new ConnectionManager(identity, identity, new StaticInterfaces(), registry, trust,
            new ConnectionOptions
            {
                Port = 0,
                AppVersion = "0.6.0",
                Capabilities = [RmpCapabilities.Text],
                MaintenanceInterval = TimeSpan.FromMilliseconds(20),
                ReconnectDelays = [TimeSpan.FromHours(1)],
                LargerIdDialDelay = TimeSpan.FromHours(1),
            },
            TimeProvider.System, NullLogger<ConnectionManager>.Instance);
        var service = new MessagingService(connections, store, trust, identity, messaging ?? new MessagingOptions { SweepInterval = TimeSpan.FromMilliseconds(50) }, TimeProvider.System, NullLogger<MessagingService>.Instance);

        var node = new Node(identity, registry, trust, store, databasePath, connections, service);
        _cleanup.Add(node);
        await connections.StartAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);
        await WaitUntil(() => connections.ListeningEndPoints.Count == 1);
        return node;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(10))
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

    /// <summary>The real store, which can be made to fail as a full or failing disk would.</summary>
    private sealed class FlakyStore(SqliteMessageStore inner) : IMessageStore, IDisposable
    {
        public volatile bool Fail;

        public void AddOutgoing(StoredMessage message)
        {
            Check();
            inner.AddOutgoing(message);
        }

        public bool TryAddIncoming(StoredMessage message)
        {
            Check();
            return inner.TryAddIncoming(message);
        }

        public IReadOnlyList<StoredMessage> GetConversation(Guid peerId, int maxMessages = int.MaxValue)
        {
            Check();
            return inner.GetConversation(peerId, maxMessages);
        }

        public IReadOnlyList<StoredMessage> GetUndelivered(Guid peerId)
        {
            Check();
            return inner.GetUndelivered(peerId);
        }

        public bool Advance(Guid peerId, Guid messageId, DeliveryState state, string? failure = null)
        {
            Check();
            return inner.Advance(peerId, messageId, state, failure);
        }

        public bool MarkIncomingRead(Guid peerId)
        {
            Check();
            return inner.MarkIncomingRead(peerId);
        }

        public IReadOnlyList<Guid> GetPendingReadReceipts(Guid peerId)
        {
            Check();
            return inner.GetPendingReadReceipts(peerId);
        }

        public void ClearPendingReadReceipts(Guid peerId, IReadOnlyCollection<Guid> messageIds)
        {
            Check();
            inner.ClearPendingReadReceipts(peerId, messageIds);
        }

        public IReadOnlyList<ConversationSummary> GetActiveConversations()
        {
            Check();
            return inner.GetActiveConversations();
        }

        public void Dispose() => inner.Dispose();

        private void Check()
        {
            if (Fail)
            {
                throw new MessageStoreException("The disk is full.");
            }
        }
    }

    private sealed class Node(DeviceIdentityStore identity, PeerRegistry registry, InMemoryTrustStore trust, FlakyStore store, string databasePath, ConnectionManager connections, MessagingService messaging) : IAsyncDisposable
    {
        public DeviceIdentityStore Identity { get; } = identity;

        public InMemoryTrustStore Trust { get; } = trust;

        public FlakyStore Store { get; } = store;

        public string DatabasePath { get; } = databasePath;

        public ConnectionManager Connections { get; } = connections;

        public MessagingService Messaging { get; } = messaging;

        public Guid Id => Identity.Identity.DeviceId;

        public int Port => Connections.ListeningEndPoints[0].Port;

        public DeliveryState? StateOf(Guid messageId) =>
            Trust.TrustedPeers.SelectMany(p => Store.GetConversation(p.DeviceId)).FirstOrDefault(m => m.MessageId == messageId)?.State;

        public void Sees(Node other) => registry.Observe(
            new DiscoveryPacket(DiscoveryPacketType.Announce, other.Id, other.Identity.Identity.Username, other.Port, "0.6.0"),
            IPAddress.Loopback,
            DateTimeOffset.UtcNow);

        public void Trusts(Node other) => Trust.Trust(new TrustedPeer(
            other.Id, other.Identity.Identity.Username, other.Identity.Identity.CertificateFingerprint, DateTimeOffset.UtcNow));

        /// <summary>Everything except the identity, which a restarted node carries on with.</summary>
        public async Task StopAsync()
        {
            await Messaging.StopAsync(CancellationToken.None);
            Messaging.Dispose();
            await Connections.StopAsync(CancellationToken.None);
            Connections.Dispose();
            Store.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            Identity.Dispose();
        }
    }

    private sealed class RawPeer(DeviceIdentityStore store, SslStream ssl, Guid remoteId, bool ownsStore) : IAsyncDisposable
    {
        public Guid Id => store.Identity.DeviceId;

        public static async Task<RawPeer> ConnectAsync(DeviceIdentityStore store, Node target, IReadOnlyList<string> capabilities, bool ownsStore)
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, target.Port);
            var credentials = SslStreamCertificateContext.Create(store.TlsCertificate, null, offline: true);
            var (ssl, _) = await TlsHandshake.AuthenticateAsClientAsync(client.GetStream(), credentials, target.Id, TimeProvider.System, CancellationToken.None);

            var peer = new RawPeer(store, ssl, target.Id, ownsStore);
            await peer.SendAsync(RmpMessageTypes.Hello, SessionPayloads.ToElement(new HelloPayload(1, 1, "0.6.0", capabilities)));
            await peer.ReadUntilAsync(RmpMessageTypes.Hello);
            return peer;
        }

        public JsonElement ChatPayload(Guid other, string content) =>
            SessionPayloads.ToElement(new ChatMessagePayload(DirectConversation.IdFor(Id, other), RmpContentTypes.Text, content));

        public async Task SendAsync(string type, JsonElement payload, Guid? messageId = null)
        {
            var envelope = new RmpEnvelope(type, messageId ?? Guid.CreateVersion7(), Id, remoteId, DateTimeOffset.UtcNow, payload);
            await ssl.WriteAsync(RmpFraming.Encode(RmpFrameType.Control, RmpEnvelopeCodec.Encode(envelope)));
            await ssl.FlushAsync();
        }

        public async Task<RmpEnvelope> ReadUntilAsync(string type)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                var frame = await RmpFraming.ReadAsync(ssl, timeout.Token) ?? throw new EndOfStreamException();
                Assert.True(RmpEnvelopeCodec.TryDecode(frame.Payload, out var envelope, out _));
                if (envelope.Type == type)
                {
                    return envelope;
                }
            }
        }

        public async Task<ErrorPayload> ReadErrorAsync()
        {
            var error = await ReadUntilAsync(RmpMessageTypes.Error);
            Assert.True(SessionPayloads.TryReadError(error.Payload, out var payload));
            return payload;
        }

        public async ValueTask DisposeAsync()
        {
            await ssl.DisposeAsync();
            if (ownsStore)
            {
                store.Dispose();
            }
        }
    }
}
