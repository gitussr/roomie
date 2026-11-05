using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Roomie.Core.Connections;
using Roomie.Core.Discovery;
using Roomie.Core.Groups;
using Roomie.Core.Messaging;
using Roomie.Core.Protocol;
using Roomie.Core.Trust;
using Roomie.Infrastructure.Identity;
using Roomie.Infrastructure.Messaging;
using Roomie.Network.Connections;
using Roomie.Network.Discovery;
using Roomie.Network.Groups;
using Roomie.Network.Messaging;

namespace Roomie.Tests.Groups;

/// <summary>Groups between real nodes over loopback TLS, and against hand-driven peers that can lie.</summary>
public sealed class GroupServiceTests : IAsyncLifetime
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

    // ---- Joining ----

    [Fact]
    public async Task InvitedDevice_JoinsOnlyAfterAccepting_AndEveryoneLearnsTheNewMembership()
    {
        var (alice, bob, charlie) = await MeshAsync();
        var groupId = alice.Groups.CreateGroup("Team").GroupId!;

        Assert.Null(alice.Groups.Invite(groupId, bob.Id).Error);
        await WaitUntil(() => bob.OpenInvite(groupId) is not null);

        // Invited but not accepted: not a member anywhere, and no group on Bob's side.
        Assert.Null(bob.Groups.GetGroup(groupId));
        Assert.Single(alice.Groups.GetGroup(groupId)!.State.Members);

        Assert.Null(bob.Groups.RespondToInvite(groupId, alice.Id, accept: true).Error);
        await WaitUntil(() => bob.Groups.GetGroup(groupId) is { IsMember: true });
        Assert.Equal(2, alice.Groups.GetGroup(groupId)!.Version);

        alice.Groups.Invite(groupId, charlie.Id);
        await WaitUntil(() => charlie.OpenInvite(groupId) is not null);
        charlie.Groups.RespondToInvite(groupId, alice.Id, accept: true);

        await WaitUntil(() => new[] { alice, bob, charlie }.All(n => n.Groups.GetGroup(groupId)?.Version == 3));
        Assert.Equal("Team", charlie.Groups.GetGroup(groupId)!.State.Name);
        Assert.Empty(bob.Groups.GetInvites());
    }

    [Fact]
    public async Task Declining_LeavesTheGroupUnchanged_AndARepeatDoesntAskAgain()
    {
        var (alice, bob, _) = await MeshAsync();
        var groupId = alice.Groups.CreateGroup("Team").GroupId!;
        alice.Groups.Invite(groupId, bob.Id);
        await WaitUntil(() => bob.OpenInvite(groupId) is not null);

        bob.Groups.RespondToInvite(groupId, alice.Id, accept: false);

        await WaitUntil(() => alice.Groups.GetInvites().Count == 0);
        Assert.Equal(1, alice.Groups.GetGroup(groupId)!.Version);
        Assert.Null(bob.Groups.GetGroup(groupId));
        Assert.Null(bob.OpenInvite(groupId));

        // Asking again is a new invitation, and this time it does ask.
        alice.Groups.Invite(groupId, bob.Id);
        await WaitUntil(() => bob.OpenInvite(groupId) is not null);
    }

    [Fact]
    public async Task InvitationToAnOfflineDevice_ArrivesWhenItConnects()
    {
        var alice = await StartAsync("Alice");
        var bob = await StartAsync("Bob");
        Pair(alice, bob);
        var groupId = alice.Groups.CreateGroup("Team").GroupId!;
        alice.Groups.Invite(groupId, bob.Id);
        await Task.Delay(100);
        Assert.Null(bob.OpenInvite(groupId));

        await ConnectAsync(alice, bob);

        await WaitUntil(() => bob.OpenInvite(groupId) is not null);
    }

    [Fact]
    public async Task OnlyPairedDevices_CanBeInvited_AndOnlyTheOwnerCanChangeMembership()
    {
        var (alice, bob, charlie) = await MeshAsync();
        var groupId = await GroupOfAsync(alice, bob);

        Assert.NotNull(alice.Groups.Invite(groupId, Guid.NewGuid()).Error);
        Assert.NotNull(bob.Groups.Invite(groupId, charlie.Id).Error);
        Assert.NotNull(bob.Groups.Remove(groupId, alice.Id).Error);
        Assert.NotNull(alice.Groups.Remove(groupId, alice.Id).Error);
        Assert.NotNull(alice.Groups.CreateGroup(" ").Error);
        Assert.NotNull(alice.Groups.CreateGroup(new string('x', GroupProtocol.MaxNameLength + 1)).Error);
    }

    // ---- Messages ----

    [Fact]
    public async Task GroupMessage_ReachesEveryMember_AndEachCopyIsTrackedSeparately()
    {
        var (alice, bob, charlie) = await MeshAsync();
        var groupId = await GroupOfAsync(alice, bob, charlie);

        var sent = alice.Messaging.SendGroupText(groupId, "Hello team").Message!;

        await WaitUntil(() => alice.RecipientState(groupId, sent.MessageId, bob.Id) == DeliveryState.Delivered
            && alice.RecipientState(groupId, sent.MessageId, charlie.Id) == DeliveryState.Delivered);
        foreach (var member in new[] { bob, charlie })
        {
            var received = Assert.Single(member.Store.GetGroupConversation(groupId));
            Assert.Equal("Hello team", received.Content);
            Assert.Equal(alice.Id, received.PeerId);
            Assert.Equal(sent.MessageId, received.MessageId);
            Assert.Equal(1, member.Store.GetGroupUnreadCounts()[groupId]);
            Assert.Empty(member.Store.GetConversation(alice.Id));
        }

        bob.Messaging.MarkGroupRead(groupId);

        await WaitUntil(() => alice.RecipientState(groupId, sent.MessageId, bob.Id) == DeliveryState.Read);
        Assert.Equal(DeliveryState.Delivered, alice.RecipientState(groupId, sent.MessageId, charlie.Id));
    }

    [Fact]
    public async Task OfflineMember_DoesntHoldUpTheOthers_AndGetsItsCopyLater_Once()
    {
        var (alice, bob, charlie) = await MeshAsync();
        var groupId = await GroupOfAsync(alice, bob, charlie);
        await alice.Connections.DisconnectAsync(charlie.Id);
        await WaitUntil(() => !charlie.Connections.TryGetSession(alice.Id, out _));

        var sent = alice.Messaging.SendGroupText(groupId, "Hello").Message!;

        await WaitUntil(() => alice.RecipientState(groupId, sent.MessageId, bob.Id) == DeliveryState.Delivered);
        Assert.Equal(DeliveryState.Pending, alice.RecipientState(groupId, sent.MessageId, charlie.Id));

        await alice.Connections.ConnectAsync(charlie.Id);

        await WaitUntil(() => alice.RecipientState(groupId, sent.MessageId, charlie.Id) == DeliveryState.Delivered);
        Assert.Single(charlie.Store.GetGroupConversation(groupId));
    }

    [Fact]
    public async Task MemberCanSendToTheWholeGroup_InTheSameOrder()
    {
        var (alice, bob, charlie) = await MeshAsync();
        var groupId = await GroupOfAsync(alice, bob, charlie);
        var texts = Enumerable.Range(0, 30).Select(i => $"m{i}").ToList();

        foreach (var text in texts)
        {
            bob.Messaging.SendGroupText(groupId, text);
        }

        await WaitUntil(() => alice.Store.GetGroupConversation(groupId).Count == 30 && charlie.Store.GetGroupConversation(groupId).Count == 30);
        Assert.Equal(texts, alice.Store.GetGroupConversation(groupId).Select(m => m.Content));
        Assert.Equal(texts, charlie.Store.GetGroupConversation(groupId).Select(m => m.Content));
    }

    [Fact]
    public async Task MembersWhoAreNotPaired_AreNotTrustedByBeingInAGroup_AndGetMessagesOncePaired()
    {
        // Alice is paired with both. Bob and Charlie aren't paired with each other.
        var alice = await StartAsync("Alice");
        var bob = await StartAsync("Bob");
        var charlie = await StartAsync("Charlie");
        Pair(alice, bob);
        Pair(alice, charlie);
        await ConnectAsync(alice, bob);
        await ConnectAsync(alice, charlie);
        var groupId = await GroupOfAsync(alice, bob, charlie);

        Assert.Null(bob.Trust.FindTrusted(charlie.Id));
        var sent = bob.Messaging.SendGroupText(groupId, "Hi").Message!;

        await WaitUntil(() => bob.RecipientState(groupId, sent.MessageId, alice.Id) == DeliveryState.Delivered);
        await Task.Delay(150);
        Assert.Equal(DeliveryState.Pending, bob.RecipientState(groupId, sent.MessageId, charlie.Id));
        Assert.Empty(charlie.Store.GetGroupConversation(groupId));

        Pair(bob, charlie);
        await ConnectAsync(bob, charlie);

        await WaitUntil(() => bob.RecipientState(groupId, sent.MessageId, charlie.Id) == DeliveryState.Delivered);
        Assert.Single(charlie.Store.GetGroupConversation(groupId));
    }

    [Fact]
    public async Task GroupsAndWaitingMessages_SurviveARestart()
    {
        var (alice, bob, _) = await MeshAsync();
        var groupId = await GroupOfAsync(alice, bob);
        await alice.Connections.DisconnectAsync(bob.Id);
        await WaitUntil(() => !bob.Connections.TryGetSession(alice.Id, out _));
        var sent = alice.Messaging.SendGroupText(groupId, "After the restart").Message!;

        alice = await RestartAsync(alice);
        bob = await RestartAsync(bob);

        Assert.Equal(2, alice.Groups.GetGroup(groupId)!.Version);
        Assert.True(bob.Groups.GetGroup(groupId)!.IsMember);
        await ConnectAsync(alice, bob);

        await WaitUntil(() => alice.RecipientState(groupId, sent.MessageId, bob.Id) == DeliveryState.Delivered);
        Assert.Equal(sent.MessageId, Assert.Single(bob.Store.GetGroupConversation(groupId)).MessageId);
    }

    // ---- Removing ----

    [Fact]
    public async Task RemovedMember_IsToldAndLearnsNothingNew_AndTheOthersRefuseIt()
    {
        var (alice, bob, charlie) = await MeshAsync();
        var groupId = await GroupOfAsync(alice, bob, charlie);
        var before = charlie.Groups.GetGroup(groupId)!;

        Assert.Null(alice.Groups.Remove(groupId, charlie.Id).Error);

        await WaitUntil(() => charlie.Groups.GetGroup(groupId) is { IsMember: false });
        await WaitUntil(() => bob.Groups.GetGroup(groupId)!.Version == before.Version + 1);
        Assert.False(bob.Groups.GetGroup(groupId)!.State.HasMember(charlie.Id));

        // Charlie keeps the membership it could already see.
        Assert.Equal(before.State.Members, charlie.Groups.GetGroup(groupId)!.State.Members);
        Assert.NotNull(charlie.Messaging.SendGroupText(groupId, "Still here?").Error);
    }

    [Fact]
    public async Task RemovedMember_WhoIsOffline_IsToldWhenItReturns()
    {
        var (alice, bob, charlie) = await MeshAsync();
        var groupId = await GroupOfAsync(alice, bob, charlie);
        await alice.Connections.DisconnectAsync(charlie.Id);
        await bob.Connections.DisconnectAsync(charlie.Id);
        await WaitUntil(() => !charlie.Connections.TryGetSession(alice.Id, out _) && !charlie.Connections.TryGetSession(bob.Id, out _));

        alice.Groups.Remove(groupId, charlie.Id);
        await Task.Delay(100);
        Assert.True(charlie.Groups.GetGroup(groupId)!.IsMember);

        await alice.Connections.ConnectAsync(charlie.Id);

        await WaitUntil(() => charlie.Groups.GetGroup(groupId) is { IsMember: false });
    }

    [Fact]
    public async Task WaitingCopiesToARemovedMember_AreGivenUp_AndNewMessagesSkipIt()
    {
        var (alice, bob, charlie) = await MeshAsync();
        var groupId = await GroupOfAsync(alice, bob, charlie);
        await alice.Connections.DisconnectAsync(charlie.Id);
        await WaitUntil(() => !charlie.Connections.TryGetSession(alice.Id, out _));
        var waiting = alice.Messaging.SendGroupText(groupId, "Before removal").Message!;

        alice.Groups.Remove(groupId, charlie.Id);

        Assert.Equal(DeliveryState.Failed, alice.RecipientState(groupId, waiting.MessageId, charlie.Id));
        Assert.Empty(alice.Store.GetUndelivered(charlie.Id));

        var next = alice.Messaging.SendGroupText(groupId, "After removal").Message!;
        var recipients = alice.Store.GetGroupConversation(groupId).Single(m => m.MessageId == next.MessageId).Recipients!;
        Assert.Equal(bob.Id, Assert.Single(recipients).DeviceId);
    }

    [Fact]
    public async Task RemovedMember_CanBeInvitedBack()
    {
        var (alice, bob, _) = await MeshAsync();
        var groupId = await GroupOfAsync(alice, bob);
        alice.Groups.Remove(groupId, bob.Id);
        await WaitUntil(() => bob.Groups.GetGroup(groupId) is { IsMember: false });

        alice.Groups.Invite(groupId, bob.Id);
        await WaitUntil(() => bob.OpenInvite(groupId) is not null);
        bob.Groups.RespondToInvite(groupId, alice.Id, accept: true);

        await WaitUntil(() => bob.Groups.GetGroup(groupId) is { IsMember: true });
        Assert.Null(bob.Messaging.SendGroupText(groupId, "Back").Error);
    }

    [Fact]
    public async Task MembershipChange_IsRelayedByAMember_WhenTheOwnerIsGone()
    {
        var (alice, bob, charlie) = await MeshAsync();
        var groupId = await GroupOfAsync(alice, bob);
        await alice.Connections.DisconnectAsync(charlie.Id);
        await WaitUntil(() => !charlie.Connections.TryGetSession(alice.Id, out _));
        alice.Groups.Invite(groupId, charlie.Id);
        await bob.Connections.DisconnectAsync(charlie.Id);
        await WaitUntil(() => !charlie.Connections.TryGetSession(bob.Id, out _));

        // Charlie accepts while connected to Alice, then only Bob can tell it about the next change.
        await alice.Connections.ConnectAsync(charlie.Id);
        await WaitUntil(() => charlie.OpenInvite(groupId) is not null);
        charlie.Groups.RespondToInvite(groupId, alice.Id, accept: true);
        await WaitUntil(() => charlie.Groups.GetGroup(groupId)?.Version == 3);
        await alice.Connections.DisconnectAsync(charlie.Id);
        await WaitUntil(() => !charlie.Connections.TryGetSession(alice.Id, out _));

        var dana = await StartAsync("Dana");
        Pair(alice, dana);
        await ConnectAsync(alice, dana);
        alice.Groups.Invite(groupId, dana.Id);
        await WaitUntil(() => dana.OpenInvite(groupId) is not null);
        dana.Groups.RespondToInvite(groupId, alice.Id, accept: true);
        await WaitUntil(() => bob.Groups.GetGroup(groupId)?.Version == 4);
        Assert.Equal(3, charlie.Groups.GetGroup(groupId)!.Version);

        await bob.Connections.ConnectAsync(charlie.Id);

        await WaitUntil(() => charlie.Groups.GetGroup(groupId)?.Version == 4);
        Assert.True(charlie.Groups.GetGroup(groupId)!.State.HasMember(dana.Id));
    }

    // ---- The version rules of architecture.md §7, against a peer that can say anything ----

    [Fact]
    public async Task MessageAtTheLocalVersion_IsAccepted()
    {
        var f = await RawGroupAsync();

        var ack = await f.Owner.SendGroupMessageAsync(f.Bob.Id, f.GroupId, 2, "Same version");

        Assert.Equal(ack, await f.Owner.ReadAckAsync());
        Assert.Single(f.Bob.Store.GetGroupConversation(f.GroupId));
    }

    [Fact]
    public async Task MessageAtAHigherVersion_FromAMember_IsStored_AndTheStateIsRequested()
    {
        var f = await RawGroupAsync();

        var id = await f.Owner.SendGroupMessageAsync(f.Bob.Id, f.GroupId, 5, "From the future");

        var request = await f.Owner.ReadUntilAsync(RmpMessageTypes.GroupStateRequest);
        Assert.Equal(f.GroupId, SessionPayloads.TryDeserialize<GroupStateRequestPayload>(request.Payload)!.GroupId);
        Assert.Equal(id, await f.Owner.ReadAckAsync());
        Assert.Equal(5, Assert.Single(f.Bob.Store.GetGroupConversation(f.GroupId)).MembershipVersion);
    }

    [Fact]
    public async Task MessageAtAHigherVersion_FromANonMember_IsNotStored_AndIsAcceptedOnceTheStateProvesIt()
    {
        var f = await RawGroupAsync();
        await using var dana = await RawPeerAsync(f.Bob, "Dana");

        var id = await dana.SendGroupMessageAsync(f.Bob.Id, f.GroupId, 3, "I'm new");

        Assert.Equal(RmpErrorCodes.MembershipUpdating, (await dana.ReadErrorAsync()).Code);
        Assert.Empty(f.Bob.Store.GetGroupConversation(f.GroupId));

        var next = f.State with { MembershipVersion = 3, EventId = Guid.NewGuid(), Members = [.. f.State.Members, new GroupMember(dana.Id, "Dana")] };
        await f.Owner.SendAsync(RmpMessageTypes.GroupState, SessionPayloads.ToElement(GroupProtocol.Sign(next, f.Owner.Identity)));
        await WaitUntil(() => f.Bob.Groups.GetGroup(f.GroupId)?.Version == 3);

        await dana.SendGroupMessageAsync(f.Bob.Id, f.GroupId, 3, "I'm new", id);

        Assert.Equal(id, await dana.ReadAckAsync());
    }

    [Fact]
    public async Task MessageAtALowerVersion_FromAMember_IsAccepted_AndTheSenderIsCaughtUp()
    {
        var f = await RawGroupAsync();

        var id = await f.Owner.SendGroupMessageAsync(f.Bob.Id, f.GroupId, 1, "I'm behind");

        var state = await f.Owner.ReadUntilAsync(RmpMessageTypes.GroupState);
        var document = SessionPayloads.TryDeserialize<SignedGroupDocument>(state.Payload)!;
        Assert.True(GroupProtocol.TryReadState(document, out var read));
        Assert.Equal(2, read.MembershipVersion);
        Assert.Equal(id, await f.Owner.ReadAckAsync());
        Assert.Single(f.Bob.Store.GetGroupConversation(f.GroupId));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task MessageAtOrBelowTheLocalVersion_FromANonMember_IsRefusedAndNotStored(int version)
    {
        var f = await RawGroupAsync();
        await using var mallory = await RawPeerAsync(f.Bob, "Mallory");

        await mallory.SendGroupMessageAsync(f.Bob.Id, f.GroupId, version, "Let me in");

        Assert.Equal(RmpErrorCodes.NotAMember, (await mallory.ReadErrorAsync()).Code);
        Assert.Empty(f.Bob.Store.GetGroupConversation(f.GroupId));
    }

    [Fact]
    public async Task MessageForAnUnknownGroup_IsRefused()
    {
        var bob = await StartAsync("Bob");
        await using var raw = await RawPeerAsync(bob, "Raw");

        await raw.SendGroupMessageAsync(bob.Id, "no-such-group", 1, "Hi");

        Assert.Equal(RmpErrorCodes.NotAMember, (await raw.ReadErrorAsync()).Code);
    }

    [Fact]
    public async Task GroupMessage_WhoseConversationIdDiffers_IsRefused()
    {
        var f = await RawGroupAsync();
        var payload = SessionPayloads.ToElement(new ChatMessagePayload("other", RmpContentTypes.Text, "Hi", f.GroupId, 2));

        await f.Owner.SendAsync(RmpMessageTypes.ChatMessage, payload);

        Assert.Equal(RmpErrorCodes.InvalidFrame, (await f.Owner.ReadErrorAsync()).Code);
    }

    [Fact]
    public async Task RefusalFromAMember_FailsOnlyThatCopy()
    {
        var alice = await StartAsync("Alice");
        await using var bob = await RawPeerAsync(alice, "Bob");
        await using var charlie = await RawPeerAsync(alice, "Charlie");
        var groupId = alice.Groups.CreateGroup("Team").GroupId!;
        await JoinRawAsync(alice, groupId, bob);
        await JoinRawAsync(alice, groupId, charlie);
        var sent = alice.Messaging.SendGroupText(groupId, "Hi").Message!;
        await bob.ReadUntilAsync(RmpMessageTypes.ChatMessage);

        await bob.SendAsync(RmpMessageTypes.Error, SessionPayloads.ToElement(new ErrorPayload(RmpErrorCodes.NotAMember, "No.", sent.MessageId)));

        await WaitUntil(() => alice.RecipientState(groupId, sent.MessageId, bob.Id) == DeliveryState.Failed);
        Assert.NotEqual(DeliveryState.Failed, alice.RecipientState(groupId, sent.MessageId, charlie.Id));
        Assert.Null(alice.Messaging.GetDeliveryProblem(bob.Id));
    }

    [Fact]
    public async Task TemporaryRefusal_IsSentAgain()
    {
        var alice = await StartAsync("Alice", new MessagingOptions { AckTimeout = TimeSpan.FromMilliseconds(100), SweepInterval = TimeSpan.FromMilliseconds(20) });
        await using var bob = await RawPeerAsync(alice, "Bob");
        var groupId = alice.Groups.CreateGroup("Team").GroupId!;
        await JoinRawAsync(alice, groupId, bob);
        var sent = alice.Messaging.SendGroupText(groupId, "Hi").Message!;
        var first = await bob.ReadUntilAsync(RmpMessageTypes.ChatMessage);

        await bob.SendAsync(RmpMessageTypes.Error, SessionPayloads.ToElement(new ErrorPayload(RmpErrorCodes.MembershipUpdating, "Wait.", sent.MessageId)));

        var again = await bob.ReadUntilAsync(RmpMessageTypes.ChatMessage);
        Assert.Equal(first.MessageId, again.MessageId);
        Assert.Equal(first.Timestamp, again.Timestamp);
        Assert.Equal(DeliveryState.Sent, alice.RecipientState(groupId, sent.MessageId, bob.Id));
    }

    // ---- Forgery ----

    [Fact]
    public async Task StateSignedByAnyoneButTheOwner_IsIgnored()
    {
        var f = await RawGroupAsync();
        await using var mallory = await RawPeerAsync(f.Bob, "Mallory");

        // Claiming to be from the real owner, but signed with Mallory's key.
        var forged = f.State with { MembershipVersion = 9, EventId = Guid.NewGuid(), Members = [.. f.State.Members, new GroupMember(mallory.Id, "Mallory")] };
        await mallory.SendAsync(RmpMessageTypes.GroupState, SessionPayloads.ToElement(GroupProtocol.Sign(forged, mallory.Identity)));

        // Naming Mallory's own key, to pass the signature check.
        var takeover = forged with { OwnerPublicKey = mallory.Identity.Identity.PublicKey };
        await mallory.SendAsync(RmpMessageTypes.GroupState, SessionPayloads.ToElement(GroupProtocol.Sign(takeover, mallory.Identity)));

        // Naming Mallory as the owner outright.
        var newOwner = takeover with { OwnerId = mallory.Id };
        await mallory.SendAsync(RmpMessageTypes.GroupState, SessionPayloads.ToElement(GroupProtocol.Sign(newOwner, mallory.Identity)));

        // A real signature over different text.
        var real = GroupProtocol.Sign(f.State with { MembershipVersion = 9 }, f.Owner.Identity);
        var tampered = real with { Document = real.Document.Replace("\"Team\"", "\"Tampered\"", StringComparison.Ordinal) };
        await mallory.SendAsync(RmpMessageTypes.GroupState, SessionPayloads.ToElement(tampered));

        await mallory.RoundTripAsync();
        var group = f.Bob.Groups.GetGroup(f.GroupId)!;
        Assert.Equal(2, group.Version);
        Assert.False(group.State.HasMember(mallory.Id));
        Assert.Equal(f.Owner.Id, group.State.OwnerId);
    }

    [Fact]
    public async Task StateFromAMemberRelayingForTheOwner_IsAccepted()
    {
        var f = await RawGroupAsync();
        await using var relay = await RawPeerAsync(f.Bob, "Relay");
        var next = f.State with { Name = "Renamed", MembershipVersion = 3, EventId = Guid.NewGuid() };

        await relay.SendAsync(RmpMessageTypes.GroupState, SessionPayloads.ToElement(GroupProtocol.Sign(next, f.Owner.Identity)));

        await WaitUntil(() => f.Bob.Groups.GetGroup(f.GroupId)?.Version == 3);
        Assert.Equal("Renamed", f.Bob.Groups.GetGroup(f.GroupId)!.State.Name);
    }

    [Fact]
    public async Task StateNamingAnotherOwner_IsIgnored_EvenIfTheOwnerSignedIt()
    {
        var f = await RawGroupAsync();
        await using var mallory = await RawPeerAsync(f.Bob, "Mallory");
        var transfer = f.State with
        {
            OwnerId = mallory.Id,
            MembershipVersion = 3,
            EventId = Guid.NewGuid(),
            Members = [.. f.State.Members, new GroupMember(mallory.Id, "Mallory")],
        };

        await f.Owner.SendAsync(RmpMessageTypes.GroupState, SessionPayloads.ToElement(GroupProtocol.Sign(transfer, f.Owner.Identity)));
        await f.Owner.RoundTripAsync();

        Assert.Equal(f.Owner.Id, f.Bob.Groups.GetGroup(f.GroupId)!.State.OwnerId);
        Assert.Equal(2, f.Bob.Groups.GetGroup(f.GroupId)!.Version);
    }

    [Fact]
    public async Task OlderSignedState_CantRollTheMembershipBack()
    {
        var f = await RawGroupAsync();
        var next = f.State with { MembershipVersion = 3, EventId = Guid.NewGuid() };
        await f.Owner.SendAsync(RmpMessageTypes.GroupState, SessionPayloads.ToElement(GroupProtocol.Sign(next, f.Owner.Identity)));
        await WaitUntil(() => f.Bob.Groups.GetGroup(f.GroupId)?.Version == 3);

        await f.Owner.SendAsync(RmpMessageTypes.GroupState, SessionPayloads.ToElement(f.Signed));
        await f.Owner.RoundTripAsync();

        Assert.Equal(3, f.Bob.Groups.GetGroup(f.GroupId)!.Version);
    }

    [Fact]
    public async Task StateNamingThisDevice_CantBringItIntoAGroupItNeverAccepted()
    {
        var bob = await StartAsync("Bob");
        await using var owner = await RawPeerAsync(bob, "Owner");
        var state = NewState(owner, ("Bob", bob.Id));

        await owner.SendAsync(RmpMessageTypes.GroupState, SessionPayloads.ToElement(GroupProtocol.Sign(state, owner.Identity)));
        await owner.RoundTripAsync();
        Assert.Null(bob.Groups.GetGroup(state.GroupId));

        // Nor does an invitation it hasn't answered.
        var signed = GroupProtocol.Sign(state, owner.Identity);
        await owner.SendAsync(RmpMessageTypes.GroupInvite, SessionPayloads.ToElement(new GroupInvitePayload(Guid.NewGuid(), signed.Document, signed.Signature)));
        await WaitUntil(() => bob.OpenInvite(state.GroupId) is not null);
        await owner.SendAsync(RmpMessageTypes.GroupState, SessionPayloads.ToElement(signed));
        await owner.RoundTripAsync();
        Assert.Null(bob.Groups.GetGroup(state.GroupId));
    }

    [Fact]
    public async Task InvitationNotFromItsOwner_OrNotWithThePairedKey_IsRefused()
    {
        var bob = await StartAsync("Bob");
        await using var owner = await RawPeerAsync(bob, "Owner");
        await using var mallory = await RawPeerAsync(bob, "Mallory");
        var state = NewState(owner);
        var signed = GroupProtocol.Sign(state, owner.Identity);

        // A genuine invitation, passed on by someone else.
        await mallory.SendAsync(RmpMessageTypes.GroupInvite, SessionPayloads.ToElement(new GroupInvitePayload(Guid.NewGuid(), signed.Document, signed.Signature)));
        Assert.Equal(RmpErrorCodes.InvalidFrame, (await mallory.ReadErrorAsync()).Code);

        // Mallory's own group, but naming a key other than the one pinned for Mallory.
        var wrongKey = NewState(mallory) with { OwnerPublicKey = owner.Identity.Identity.PublicKey };
        var signedWrong = GroupProtocol.Sign(wrongKey, owner.Identity);
        await mallory.SendAsync(RmpMessageTypes.GroupInvite, SessionPayloads.ToElement(new GroupInvitePayload(Guid.NewGuid(), signedWrong.Document, signedWrong.Signature)));
        Assert.Equal(RmpErrorCodes.InvalidFrame, (await mallory.ReadErrorAsync()).Code);

        // Mallory's real key and signature, but naming someone else as the owner.
        var otherOwner = NewState(mallory) with { OwnerId = owner.Id, Members = [new GroupMember(owner.Id, "Owner")] };
        var signedOther = GroupProtocol.Sign(otherOwner, mallory.Identity);
        await mallory.SendAsync(RmpMessageTypes.GroupInvite, SessionPayloads.ToElement(new GroupInvitePayload(Guid.NewGuid(), signedOther.Document, signedOther.Signature)));
        Assert.Equal(RmpErrorCodes.InvalidFrame, (await mallory.ReadErrorAsync()).Code);

        Assert.Empty(bob.Groups.GetInvites());
    }

    [Fact]
    public async Task RemovedMember_IsSentTheRemoval_ButNeverTheNewMemberList()
    {
        var alice = await StartAsync("Alice");
        await using var bob = await RawPeerAsync(alice, "Bob");
        await using var charlie = await RawPeerAsync(alice, "Charlie");
        var groupId = alice.Groups.CreateGroup("Team").GroupId!;
        await JoinRawAsync(alice, groupId, bob);
        await JoinRawAsync(alice, groupId, charlie);
        var version = alice.Groups.GetGroup(groupId)!.Version;
        await charlie.ReadUntilAsync(RmpMessageTypes.GroupState);

        alice.Groups.Remove(groupId, charlie.Id);

        var removal = await charlie.ReadUntilAsync(RmpMessageTypes.GroupRemove);
        Assert.True(GroupProtocol.TryReadRemoval(SessionPayloads.TryDeserialize<SignedGroupDocument>(removal.Payload)!, out var read));
        Assert.Equal(charlie.Id, read.RemovedDeviceId);
        Assert.Equal(version + 1, read.MembershipVersion);

        // Membership goes out before messages, so everything it would be sent arrives before this message.
        var marker = alice.Messaging.SendText(charlie.Id, "Marker").Message!;
        var seen = await charlie.ReadAllUntilAsync(RmpMessageTypes.ChatMessage);

        Assert.Equal(marker.MessageId, seen[^1].MessageId);
        Assert.DoesNotContain(seen, m => m.Type == RmpMessageTypes.GroupState);
    }

    [Fact]
    public async Task PairedDeviceOutsideAGroup_IsNeverSentItsMembership()
    {
        var alice = await StartAsync("Alice");
        await using var bob = await RawPeerAsync(alice, "Bob");
        await using var outsider = await RawPeerAsync(alice, "Outsider");
        var groupId = alice.Groups.CreateGroup("Team").GroupId!;
        await JoinRawAsync(alice, groupId, bob);

        var marker = alice.Messaging.SendText(outsider.Id, "Marker").Message!;
        var seen = await outsider.ReadAllUntilAsync(RmpMessageTypes.ChatMessage);

        Assert.Equal(marker.MessageId, seen[^1].MessageId);
        Assert.DoesNotContain(seen, m => m.Type is RmpMessageTypes.GroupState or RmpMessageTypes.GroupInvite);
    }

    [Fact]
    public async Task EachMembersCopy_HasItsOwnRetryTimer()
    {
        var alice = await StartAsync("Alice", new MessagingOptions { SweepInterval = TimeSpan.FromMilliseconds(20) });
        await using var bob = await RawPeerAsync(alice, "Bob");
        await using var charlie = await RawPeerAsync(alice, "Charlie");
        var groupId = alice.Groups.CreateGroup("Team").GroupId!;
        await JoinRawAsync(alice, groupId, bob);
        await JoinRawAsync(alice, groupId, charlie);

        var sent = alice.Messaging.SendGroupText(groupId, "Once each").Message!;
        await bob.ReadUntilAsync(RmpMessageTypes.ChatMessage);
        await charlie.ReadUntilAsync(RmpMessageTypes.ChatMessage);

        // Neither was acknowledged, but neither is due: the timeout is 30 seconds, and sweeps run constantly.
        await Task.Delay(300);
        var marker = alice.Messaging.SendText(bob.Id, "Marker").Message!;
        var seen = await bob.ReadAllUntilAsync(RmpMessageTypes.ChatMessage);

        Assert.Equal(marker.MessageId, seen.Single(m => m.Type == RmpMessageTypes.ChatMessage).MessageId);
        Assert.NotEqual(sent.MessageId, seen[^1].MessageId);
    }

    [Fact]
    public async Task InvitationReusingAGroupIdOfAnotherOwner_IsRefused()
    {
        var f = await RawGroupAsync();
        await using var mallory = await RawPeerAsync(f.Bob, "Mallory");
        var hijack = NewState(mallory) with { GroupId = f.GroupId };
        var signed = GroupProtocol.Sign(hijack, mallory.Identity);

        await mallory.SendAsync(RmpMessageTypes.GroupInvite, SessionPayloads.ToElement(new GroupInvitePayload(Guid.NewGuid(), signed.Document, signed.Signature)));

        Assert.Equal(RmpErrorCodes.InvalidFrame, (await mallory.ReadErrorAsync()).Code);
    }

    [Fact]
    public async Task RemovalNotSignedByTheOwner_OrForAnotherDevice_IsIgnored()
    {
        var f = await RawGroupAsync();
        await using var mallory = await RawPeerAsync(f.Bob, "Mallory");

        var forged = new GroupRemoval(f.GroupId, f.Owner.Id, f.Bob.Id, 3, Guid.NewGuid());
        await mallory.SendAsync(RmpMessageTypes.GroupRemove, SessionPayloads.ToElement(GroupProtocol.Sign(forged, mallory.Identity)));

        var someoneElse = new GroupRemoval(f.GroupId, f.Owner.Id, mallory.Id, 3, Guid.NewGuid());
        await f.Owner.SendAsync(RmpMessageTypes.GroupRemove, SessionPayloads.ToElement(GroupProtocol.Sign(someoneElse, f.Owner.Identity)));

        await mallory.RoundTripAsync();
        await f.Owner.RoundTripAsync();
        Assert.True(f.Bob.Groups.GetGroup(f.GroupId)!.IsMember);
        Assert.Equal(2, f.Bob.Groups.GetGroup(f.GroupId)!.Version);
    }

    [Fact]
    public async Task OldRemoval_CantUndoALaterReturn()
    {
        var f = await RawGroupAsync();
        var removal = GroupProtocol.Sign(new GroupRemoval(f.GroupId, f.Owner.Id, f.Bob.Id, 2, Guid.NewGuid()), f.Owner.Identity);

        await f.Owner.SendAsync(RmpMessageTypes.GroupRemove, SessionPayloads.ToElement(removal));
        await f.Owner.RoundTripAsync();

        Assert.True(f.Bob.Groups.GetGroup(f.GroupId)!.IsMember);
    }

    [Fact]
    public async Task StateRequest_OnlyAnswersMembers_AndNeverRevealsTheMemberList()
    {
        var f = await RawGroupAsync();
        await using var mallory = await RawPeerAsync(f.Bob, "Mallory");
        var request = SessionPayloads.ToElement(new GroupStateRequestPayload(f.GroupId));

        await mallory.SendAsync(RmpMessageTypes.GroupStateRequest, request);
        Assert.Equal(RmpErrorCodes.NotAMember, (await mallory.ReadErrorAsync()).Code);

        await f.Owner.SendAsync(RmpMessageTypes.GroupStateRequest, request);
        await f.Owner.ReadUntilAsync(RmpMessageTypes.GroupState);
    }

    [Fact]
    public async Task DeviceWithoutGroups_StillGetsItsOneToOneMessages()
    {
        var alice = await StartAsync("Alice");
        await using var bob = await RawPeerAsync(alice, "Bob");
        var groupId = alice.Groups.CreateGroup("Team").GroupId!;
        await JoinRawAsync(alice, groupId, bob);
        await using var old = await RawPeerAsync(alice, "Old", [RmpCapabilities.Text]);

        // Old is written into a group copy directly, as if it had been a member before downgrading.
        alice.Store.AddOutgoingGroup(new StoredMessage(Guid.NewGuid(), Guid.Empty, MessageDirection.Outgoing, "group", DateTimeOffset.UtcNow, DeliveryState.Delivered, GroupId: groupId, MembershipVersion: 2), [old.Id]);
        var direct = alice.Messaging.SendText(old.Id, "direct").Message!;

        var received = await old.ReadUntilAsync(RmpMessageTypes.ChatMessage);

        Assert.Equal(direct.MessageId, received.MessageId);
        Assert.Null(alice.Messaging.GetDeliveryProblem(old.Id));
    }

    // ---- Harness ----

    // ---- Deleting ----

    [Fact]
    public async Task OwnerDeleting_EndsTheGroupForEveryone_AndHistoryStaysUntilRemoved()
    {
        var (alice, bob, charlie) = await MeshAsync();
        var groupId = await GroupOfAsync(alice, bob, charlie);
        var sent = alice.Messaging.SendGroupText(groupId, "Before").Message!;
        await WaitUntil(() => bob.Store.GetGroupConversation(groupId).Any(m => m.MessageId == sent.MessageId));

        Assert.Null(alice.Groups.Delete(groupId).Error);

        await WaitUntil(() => new[] { alice, bob, charlie }.All(n => n.Groups.GetGroup(groupId) is { IsMember: false, IsDeleted: true }));
        foreach (var node in new[] { alice, bob, charlie })
        {
            Assert.NotNull(node.Messaging.SendGroupText(groupId, "After").Error);
            Assert.Contains(node.Groups.GetGroups(), g => g.State.GroupId == groupId);
        }

        Assert.Single(bob.Store.GetGroupConversation(groupId));
        Assert.Null(bob.Groups.RemoveFromThisComputer(groupId).Error);
        Assert.Empty(bob.Store.GetGroupConversation(groupId));
        Assert.DoesNotContain(bob.Groups.GetGroups(), g => g.State.GroupId == groupId);
    }

    [Fact]
    public async Task Deleting_ReachesAMemberThroughAnotherMember_WhileTheOwnerCantReachIt()
    {
        var (alice, bob, charlie) = await MeshAsync();
        var groupId = await GroupOfAsync(alice, bob, charlie);
        await alice.Connections.DisconnectAsync(charlie.Id);
        await WaitUntil(() => alice.Connections.GetInfo(charlie.Id).State != PeerConnectionState.ConnectedTrusted);

        alice.Groups.Delete(groupId);

        await WaitUntil(() => charlie.Groups.GetGroup(groupId) is { IsDeleted: true });
    }

    [Fact]
    public async Task OwnerRemovingADeletedGroup_StillTellsADeviceThatWasOffline_AfterARestart()
    {
        var alice = await StartAsync("Alice");
        var bob = await StartAsync("Bob");
        Pair(alice, bob);
        await ConnectAsync(alice, bob);
        var groupId = await GroupOfAsync(alice, bob);
        await alice.Connections.DisconnectAsync(bob.Id);
        await WaitUntil(() => alice.Connections.GetInfo(bob.Id).State != PeerConnectionState.ConnectedTrusted);

        alice.Groups.Delete(groupId);
        alice.Groups.RemoveFromThisComputer(groupId);
        Assert.Empty(alice.Groups.GetGroups());
        alice = await RestartAsync(alice);
        Assert.Empty(alice.Groups.GetGroups());
        Assert.False(bob.Groups.GetGroup(groupId)!.IsDeleted);

        await ConnectAsync(alice, bob);

        await WaitUntil(() => bob.Groups.GetGroup(groupId) is { IsDeleted: true });
    }

    [Fact]
    public async Task Deleting_ClosesAnInvitationNotYetAnswered_AndAnAnswerCantJoin()
    {
        var (alice, bob, _) = await MeshAsync();
        var groupId = alice.Groups.CreateGroup("Team").GroupId!;
        alice.Groups.Invite(groupId, bob.Id);
        await WaitUntil(() => bob.OpenInvite(groupId) is not null);

        alice.Groups.Delete(groupId);

        await WaitUntil(() => bob.OpenInvite(groupId) is null);
        Assert.Null(bob.Groups.GetGroup(groupId));
        Assert.NotNull(bob.Groups.RespondToInvite(groupId, alice.Id, accept: true).Error);
        Assert.False(alice.Groups.GetGroup(groupId)!.State.HasMember(bob.Id));
    }

    [Fact]
    public async Task AnAnswerArrivingAfterDeletion_DoesntAddTheDevice()
    {
        var alice = await StartAsync("Alice");
        var peer = await RawPeerAsync(alice, "Late", [RmpCapabilities.Text, RmpCapabilities.Groups, RmpCapabilities.GroupEnd]);
        _cleanup.Add(peer);
        var groupId = alice.Groups.CreateGroup("Team").GroupId!;
        alice.Groups.Invite(groupId, peer.Id);
        var invite = SessionPayloads.TryDeserialize<GroupInvitePayload>((await peer.ReadUntilAsync(RmpMessageTypes.GroupInvite)).Payload)!;

        alice.Groups.Delete(groupId);
        await peer.ReadUntilAsync(RmpMessageTypes.GroupDelete);
        await peer.SendAsync(RmpMessageTypes.GroupAccept, SessionPayloads.ToElement(new GroupAcceptPayload(groupId, invite.InviteId, true)));
        await peer.RoundTripAsync();

        Assert.False(alice.Groups.GetGroup(groupId)!.State.HasMember(peer.Id));
        Assert.True(alice.Groups.GetGroup(groupId)!.IsDeleted);
    }

    [Fact]
    public async Task ADeletionNotSignedByTheOwner_IsIgnored()
    {
        var raw = await RawGroupAsync();
        var forger = await RawPeerAsync(raw.Bob, "Forger", [RmpCapabilities.Text, RmpCapabilities.Groups, RmpCapabilities.GroupEnd]);
        _cleanup.Add(forger);

        var forged = GroupProtocol.Sign(new GroupDeletion(raw.GroupId, raw.Owner.Id, 3, Guid.NewGuid()), forger.Identity);
        await forger.SendAsync(RmpMessageTypes.GroupDelete, SessionPayloads.ToElement(forged));
        await forger.RoundTripAsync();

        Assert.False(raw.Bob.Groups.GetGroup(raw.GroupId)!.IsDeleted);
        Assert.True(raw.Bob.Groups.GetGroup(raw.GroupId)!.IsMember);
    }

    [Fact]
    public async Task OnlyTheOwnerDeletes_OnlyMembersLeave_AndADeletedGroupCantChange()
    {
        var (alice, bob, charlie) = await MeshAsync();
        var groupId = await GroupOfAsync(alice, bob);

        Assert.NotNull(bob.Groups.Delete(groupId).Error);
        Assert.NotNull(alice.Groups.Leave(groupId).Error);
        Assert.NotNull(alice.Groups.RemoveFromThisComputer(groupId).Error);
        Assert.True(alice.Groups.GetGroup(groupId)!.IsMember);

        alice.Groups.Delete(groupId);
        Assert.NotNull(alice.Groups.Invite(groupId, charlie.Id).Error);
        Assert.NotNull(alice.Groups.Remove(groupId, bob.Id).Error);
        Assert.NotNull(alice.Groups.Delete(groupId).Error);
    }

    [Fact]
    public async Task ADeviceWithoutTheFeature_IsNotSentADeletion()
    {
        var alice = await StartAsync("Alice");
        var peer = await RawPeerAsync(alice, "Old");
        _cleanup.Add(peer);
        var groupId = alice.Groups.CreateGroup("Team").GroupId!;
        await JoinRawAsync(alice, groupId, peer);

        alice.Groups.Delete(groupId);
        await Task.Delay(300);
        await peer.SendAsync(RmpMessageTypes.Heartbeat, SessionPayloads.Empty);

        var seen = await peer.ReadAllUntilAsync(RmpMessageTypes.HeartbeatAck);
        Assert.DoesNotContain(seen, m => m.Type == RmpMessageTypes.GroupDelete);
    }

    // ---- Leaving ----

    [Fact]
    public async Task MemberLeaving_IsRemovedByTheOwner_AndEveryoneElseLearnsIt()
    {
        var (alice, bob, charlie) = await MeshAsync();
        var groupId = await GroupOfAsync(alice, bob, charlie);
        var before = alice.Groups.GetGroup(groupId)!.Version;

        Assert.Null(bob.Groups.Leave(groupId).Error);
        Assert.NotNull(bob.Messaging.SendGroupText(groupId, "Still here?").Error);

        await WaitUntil(() => !alice.Groups.GetGroup(groupId)!.State.HasMember(bob.Id));
        await WaitUntil(() => !charlie.Groups.GetGroup(groupId)!.State.HasMember(bob.Id));
        await WaitUntil(() => bob.Groups.GetGroup(groupId)!.Version == before + 1);
        Assert.True(bob.Groups.GetGroup(groupId) is { IsMember: false, HasLeft: true });
        Assert.True(charlie.Groups.GetGroup(groupId)!.IsMember);
    }

    [Fact]
    public async Task Leaving_ReachesTheOwner_OnceItIsConnectedAgain()
    {
        var alice = await StartAsync("Alice");
        var bob = await StartAsync("Bob");
        Pair(alice, bob);
        await ConnectAsync(alice, bob);
        var groupId = await GroupOfAsync(alice, bob);
        await alice.Connections.DisconnectAsync(bob.Id);
        await WaitUntil(() => bob.Connections.GetInfo(alice.Id).State != PeerConnectionState.ConnectedTrusted);

        bob.Groups.Leave(groupId);
        bob.Groups.RemoveFromThisComputer(groupId);
        Assert.Empty(bob.Groups.GetGroups());
        Assert.True(alice.Groups.GetGroup(groupId)!.State.HasMember(bob.Id));

        await ConnectAsync(alice, bob);

        await WaitUntil(() => !alice.Groups.GetGroup(groupId)!.State.HasMember(bob.Id));
    }

    [Fact]
    public async Task AfterLeaving_TheOwnerCanInviteThemBack()
    {
        var alice = await StartAsync("Alice");
        var bob = await StartAsync("Bob");
        Pair(alice, bob);
        await ConnectAsync(alice, bob);
        var groupId = await GroupOfAsync(alice, bob);
        bob.Groups.Leave(groupId);
        await WaitUntil(() => !alice.Groups.GetGroup(groupId)!.State.HasMember(bob.Id));
        var version = alice.Groups.GetGroup(groupId)!.Version;
        await WaitUntil(() => bob.Groups.GetGroup(groupId)!.Version == version);
        bob.Groups.RemoveFromThisComputer(groupId);

        Assert.Null(alice.Groups.Invite(groupId, bob.Id).Error);
        await WaitUntil(() => bob.OpenInvite(groupId) is not null);
        bob.Groups.RespondToInvite(groupId, alice.Id, accept: true);

        await WaitUntil(() => bob.Groups.GetGroup(groupId) is { IsMember: true, HasLeft: false, IsHidden: false });
        Assert.Contains(bob.Groups.GetGroups(), g => g.State.GroupId == groupId);
    }

    [Fact]
    public async Task ALeaveMessage_OnlyEverTakesOutTheDeviceThatSentIt()
    {
        var alice = await StartAsync("Alice");
        var member = await RawPeerAsync(alice, "Member", [RmpCapabilities.Text, RmpCapabilities.Groups, RmpCapabilities.GroupEnd]);
        var outsider = await RawPeerAsync(alice, "Outsider", [RmpCapabilities.Text, RmpCapabilities.Groups, RmpCapabilities.GroupEnd]);
        _cleanup.Add(member);
        _cleanup.Add(outsider);
        var groupId = alice.Groups.CreateGroup("Team").GroupId!;
        await JoinRawAsync(alice, groupId, member);
        var version = alice.Groups.GetGroup(groupId)!.Version;

        await outsider.SendAsync(RmpMessageTypes.GroupLeave, SessionPayloads.ToElement(new GroupLeavePayload(groupId)));
        await outsider.RoundTripAsync();

        Assert.Equal(version, alice.Groups.GetGroup(groupId)!.Version);
        Assert.True(alice.Groups.GetGroup(groupId)!.State.HasMember(member.Id));

        await member.SendAsync(RmpMessageTypes.GroupLeave, SessionPayloads.ToElement(new GroupLeavePayload(groupId)));
        await WaitUntil(() => !alice.Groups.GetGroup(groupId)!.State.HasMember(member.Id));
        Assert.True(alice.Groups.GetGroup(groupId)!.State.HasMember(alice.Id));
    }

    // ---- Names ----

    [Fact]
    public async Task AMemberRenamed_ReachesMembersNotPairedWithIt_ThroughTheOwner()
    {
        var alice = await StartAsync("Alice");
        var bob = await StartAsync("Bob");
        var charlie = await StartAsync("Charlie");
        Pair(alice, bob);
        Pair(alice, charlie);
        await ConnectAsync(alice, bob);
        await ConnectAsync(alice, charlie);
        var groupId = await GroupOfAsync(alice, bob, charlie);
        var before = alice.Groups.GetGroup(groupId)!.Version;

        Assert.True(alice.Trust.UpdateUsername(bob.Id, "Robert"));

        await WaitUntil(() => charlie.Groups.GetGroup(groupId)!.State.Members.Single(m => m.DeviceId == bob.Id).Username == "Robert");
        Assert.Equal(before + 1, alice.Groups.GetGroup(groupId)!.Version);
    }

    [Fact]
    public async Task TheOwnerRenamed_ReachesItsMembers_AndAnUnchangedNameChangesNothing()
    {
        var alice = await StartAsync("Alice");
        var bob = await StartAsync("Bob");
        Pair(alice, bob);
        await ConnectAsync(alice, bob);
        var groupId = await GroupOfAsync(alice, bob);
        var before = alice.Groups.GetGroup(groupId)!.Version;

        alice.Groups.RefreshOwnedNames();
        Assert.Equal(before, alice.Groups.GetGroup(groupId)!.Version);

        alice.Identity.ChangeUsername("Alicia");
        alice.Groups.RefreshOwnedNames();

        await WaitUntil(() => bob.Groups.GetGroup(groupId)!.State.Members.Single(m => m.DeviceId == alice.Id).Username == "Alicia");
    }

    private sealed record RawGroup(Node Bob, RawPeer Owner, string GroupId, GroupState State, SignedGroupDocument Signed);

    /// <summary>Bob has joined a group at version 2, owned by a peer driven by hand.</summary>
    private async Task<RawGroup> RawGroupAsync()
    {
        var bob = await StartAsync("Bob");
        var owner = await RawPeerAsync(bob, "Owner");
        _cleanup.Add(owner);
        var first = NewState(owner) with { Name = "Team" };
        var signed = GroupProtocol.Sign(first, owner.Identity);
        var inviteId = Guid.NewGuid();
        await owner.SendAsync(RmpMessageTypes.GroupInvite, SessionPayloads.ToElement(new GroupInvitePayload(inviteId, signed.Document, signed.Signature)));
        await WaitUntil(() => bob.OpenInvite(first.GroupId) is not null);
        bob.Groups.RespondToInvite(first.GroupId, owner.Id, accept: true);
        var answer = await owner.ReadUntilAsync(RmpMessageTypes.GroupAccept);
        Assert.True(SessionPayloads.TryDeserialize<GroupAcceptPayload>(answer.Payload)!.Accepted);

        var joined = first with { MembershipVersion = 2, EventId = Guid.NewGuid(), Members = [.. first.Members, new GroupMember(bob.Id, "Bob")] };
        var signedJoined = GroupProtocol.Sign(joined, owner.Identity);
        await owner.SendAsync(RmpMessageTypes.GroupState, SessionPayloads.ToElement(signedJoined));
        await WaitUntil(() => bob.Groups.GetGroup(first.GroupId) is { IsMember: true, Version: 2 });
        return new RawGroup(bob, owner, first.GroupId, joined, signed);
    }

    /// <summary>A hand-driven peer accepts an invitation from a real owner.</summary>
    private static async Task JoinRawAsync(Node owner, string groupId, RawPeer peer)
    {
        Assert.Null(owner.Groups.Invite(groupId, peer.Id).Error);
        var invite = await peer.ReadUntilAsync(RmpMessageTypes.GroupInvite);
        var payload = SessionPayloads.TryDeserialize<GroupInvitePayload>(invite.Payload)!;
        await peer.SendAsync(RmpMessageTypes.GroupAccept, SessionPayloads.ToElement(new GroupAcceptPayload(groupId, payload.InviteId, true)));
        await WaitUntil(() => owner.Groups.GetGroup(groupId)!.State.HasMember(peer.Id));
    }

    private static GroupState NewState(RawPeer owner, params (string Name, Guid Id)[] others) => new(
        Guid.NewGuid().ToString("N"),
        "Team",
        owner.Id,
        owner.Identity.Identity.PublicKey,
        1,
        Guid.NewGuid(),
        [new GroupMember(owner.Id, owner.Identity.Identity.Username), .. others.Select(o => new GroupMember(o.Id, o.Name))]);

    private async Task<(Node Alice, Node Bob, Node Charlie)> MeshAsync()
    {
        var alice = await StartAsync("Alice");
        var bob = await StartAsync("Bob");
        var charlie = await StartAsync("Charlie");
        Pair(alice, bob);
        Pair(alice, charlie);
        Pair(bob, charlie);
        await ConnectAsync(alice, bob);
        await ConnectAsync(alice, charlie);
        await ConnectAsync(bob, charlie);
        return (alice, bob, charlie);
    }

    private static async Task<string> GroupOfAsync(Node owner, params Node[] members)
    {
        var groupId = owner.Groups.CreateGroup("Team").GroupId!;
        foreach (var member in members)
        {
            Assert.Null(owner.Groups.Invite(groupId, member.Id).Error);
            await WaitUntil(() => member.OpenInvite(groupId) is not null);
            Assert.Null(member.Groups.RespondToInvite(groupId, owner.Id, accept: true).Error);
            await WaitUntil(() => member.Groups.GetGroup(groupId) is { IsMember: true });
        }

        var version = owner.Groups.GetGroup(groupId)!.Version;
        await WaitUntil(() => members.All(m => m.Groups.GetGroup(groupId)?.Version == version));
        return groupId;
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

    private async Task<RawPeer> RawPeerAsync(Node target, string name, IReadOnlyList<string>? capabilities = null)
    {
        var identity = new DeviceIdentityStore(Path.Combine(_root, Guid.NewGuid().ToString("N")));
        identity.LoadOrCreate(name);
        target.Trust.Trust(new TrustedPeer(identity.Identity.DeviceId, name, identity.Identity.CertificateFingerprint, DateTimeOffset.UtcNow));

        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, target.Port);
        var credentials = SslStreamCertificateContext.Create(identity.TlsCertificate, null, offline: true);
        var (ssl, _) = await TlsHandshake.AuthenticateAsClientAsync(client.GetStream(), credentials, target.Id, TimeProvider.System, CancellationToken.None);

        var peer = new RawPeer(identity, ssl, target.Id);
        await peer.SendAsync(RmpMessageTypes.Hello, SessionPayloads.ToElement(new HelloPayload(1, 1, "0.8.0", capabilities ?? [RmpCapabilities.Text, RmpCapabilities.Groups])));
        await peer.ReadUntilAsync(RmpMessageTypes.Hello);
        await WaitUntil(() => target.Connections.TryGetSession(peer.Id, out _));
        return peer;
    }

    private Task<Node> StartAsync(string username, MessagingOptions? messaging = null)
    {
        var identity = new DeviceIdentityStore(Path.Combine(_root, Guid.NewGuid().ToString("N")));
        identity.LoadOrCreate(username);
        return StartAsync(identity, new InMemoryTrustStore(), Path.Combine(_root, $"{Guid.NewGuid():N}.db"), messaging);
    }

    private async Task<Node> RestartAsync(Node node)
    {
        _cleanup.Remove(node);
        await node.StopAsync();
        return await StartAsync(node.Identity, node.Trust, node.DatabasePath, null);
    }

    private async Task<Node> StartAsync(DeviceIdentityStore identity, InMemoryTrustStore trust, string databasePath, MessagingOptions? messaging)
    {
        var registry = new PeerRegistry(TimeSpan.FromMinutes(1));
        var store = new SqliteMessageStore(databasePath);
        var connections = new ConnectionManager(identity, identity, new StaticInterfaces(), registry, trust,
            new ConnectionOptions
            {
                Port = 0,
                AppVersion = "0.8.0",
                Capabilities = [RmpCapabilities.Text, RmpCapabilities.Groups, RmpCapabilities.GroupEnd],
                MaintenanceInterval = TimeSpan.FromMilliseconds(20),
                ReconnectDelays = [TimeSpan.FromHours(1)],
                LargerIdDialDelay = TimeSpan.FromHours(1),
            },
            TimeProvider.System, NullLogger<ConnectionManager>.Instance);
        var groups = new GroupService(connections, store, trust, identity, identity, TimeProvider.System, NullLogger<GroupService>.Instance);
        var service = new MessagingService(connections, store, trust, identity, groups, messaging ?? new MessagingOptions { SweepInterval = TimeSpan.FromMilliseconds(50) }, TimeProvider.System, NullLogger<MessagingService>.Instance);

        var node = new Node(identity, registry, trust, store, databasePath, connections, groups, service);
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

    private sealed class Node(DeviceIdentityStore identity, PeerRegistry registry, InMemoryTrustStore trust, SqliteMessageStore store, string databasePath, ConnectionManager connections, GroupService groups, MessagingService messaging) : IAsyncDisposable
    {
        public DeviceIdentityStore Identity { get; } = identity;

        public InMemoryTrustStore Trust { get; } = trust;

        public SqliteMessageStore Store { get; } = store;

        public string DatabasePath { get; } = databasePath;

        public ConnectionManager Connections { get; } = connections;

        public GroupService Groups { get; } = groups;

        public MessagingService Messaging { get; } = messaging;

        public Guid Id => Identity.Identity.DeviceId;

        public int Port => Connections.ListeningEndPoints[0].Port;

        public StoredInvite? OpenInvite(string groupId) =>
            Groups.GetInvites().FirstOrDefault(i => i.GroupId == groupId && i is { Direction: InviteDirection.Incoming, Response: InviteResponse.None });

        public DeliveryState? RecipientState(string groupId, Guid messageId, Guid recipient) =>
            Store.GetGroupConversation(groupId).Single(m => m.MessageId == messageId).Recipients!.Single(r => r.DeviceId == recipient).State;

        public void Sees(Node other) => registry.Observe(
            new DiscoveryPacket(DiscoveryPacketType.Announce, other.Id, other.Identity.Identity.Username, other.Port, "0.8.0"),
            IPAddress.Loopback,
            DateTimeOffset.UtcNow);

        public async Task StopAsync()
        {
            await Messaging.StopAsync(CancellationToken.None);
            Messaging.Dispose();
            Groups.Dispose();
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

    private sealed class RawPeer(DeviceIdentityStore identity, SslStream ssl, Guid remoteId) : IAsyncDisposable
    {
        private bool _disposed;

        public DeviceIdentityStore Identity { get; } = identity;

        public Guid Id => Identity.Identity.DeviceId;

        public async Task<Guid> SendGroupMessageAsync(Guid recipient, string groupId, int version, string content, Guid? messageId = null)
        {
            Assert.Equal(remoteId, recipient);
            var id = messageId ?? Guid.CreateVersion7();
            await SendAsync(RmpMessageTypes.ChatMessage, SessionPayloads.ToElement(new ChatMessagePayload(groupId, RmpContentTypes.Text, content, groupId, version)), id);
            return id;
        }

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

        /// <summary>Everything received up to and including the next message of this type.</summary>
        public async Task<List<RmpEnvelope>> ReadAllUntilAsync(string type)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var seen = new List<RmpEnvelope>();
            while (true)
            {
                var frame = await RmpFraming.ReadAsync(ssl, timeout.Token) ?? throw new EndOfStreamException();
                Assert.True(RmpEnvelopeCodec.TryDecode(frame.Payload, out var envelope, out _));
                seen.Add(envelope);
                if (envelope.Type == type)
                {
                    return seen;
                }
            }
        }

        public async Task<Guid> ReadAckAsync()
        {
            var ack = await ReadUntilAsync(RmpMessageTypes.MessageAck);
            Assert.True(ContentPayloads.TryReadMessageAck(ack.Payload, out var read));
            return read.MessageId;
        }

        public async Task<ErrorPayload> ReadErrorAsync()
        {
            var error = await ReadUntilAsync(RmpMessageTypes.Error);
            Assert.True(SessionPayloads.TryReadError(error.Payload, out var payload));
            return payload;
        }

        /// <summary>Messages are handled in order, so when this returns, everything sent before it was processed.</summary>
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
                Identity.Dispose();
            }
        }
    }
}
