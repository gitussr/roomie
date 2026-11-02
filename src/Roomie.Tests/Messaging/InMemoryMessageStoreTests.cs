using Roomie.Core.Messaging;

namespace Roomie.Tests.Messaging;

public sealed class InMemoryMessageStoreTests
{
    private static readonly Guid Peer = Guid.Parse("a82f21c0-1111-4222-8333-444455556666");
    private static readonly DateTimeOffset Start = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private static StoredMessage Incoming(Guid? id = null, int seconds = 0, string text = "hi") =>
        new(id ?? Guid.NewGuid(), Peer, MessageDirection.Incoming, text, Start.AddSeconds(seconds), DeliveryState.Delivered);

    private static StoredMessage Outgoing(Guid? id = null, int seconds = 0) =>
        new(id ?? Guid.NewGuid(), Peer, MessageDirection.Outgoing, "hello", Start.AddSeconds(seconds), DeliveryState.Pending);

    [Fact]
    public void Duplicate_IsNotStoredTwice_AndTheFirstCopyWins()
    {
        var store = new InMemoryMessageStore();
        var id = Guid.NewGuid();

        Assert.True(store.TryAddIncoming(Incoming(id, text: "first")));
        Assert.False(store.TryAddIncoming(Incoming(id, text: "second")));

        Assert.Equal("first", Assert.Single(store.GetConversation(Peer)).Content);
    }

    [Fact]
    public void IncomingMessageWithTheIdOfAnOutgoingOne_IsADifferentMessage()
    {
        var store = new InMemoryMessageStore();
        var id = Guid.NewGuid();
        store.AddOutgoing(Outgoing(id));

        Assert.True(store.TryAddIncoming(Incoming(id)));
        Assert.Equal(2, store.GetConversation(Peer).Count);
        Assert.Equal(DeliveryState.Pending, store.GetUndelivered(Peer)[0].State);
    }

    [Fact]
    public void Conversation_IsInTimestampOrder_WhateverTheArrivalOrder()
    {
        var store = new InMemoryMessageStore();
        store.TryAddIncoming(Incoming(seconds: 30, text: "third"));
        store.TryAddIncoming(Incoming(seconds: 10, text: "first"));
        store.TryAddIncoming(Incoming(seconds: 20, text: "second"));

        Assert.Equal(["first", "second", "third"], store.GetConversation(Peer).Select(m => m.Content));
    }

    [Fact]
    public void MessagesWithTheSameTimestamp_KeepTheirArrivalOrder()
    {
        var store = new InMemoryMessageStore();
        var texts = Enumerable.Range(0, 50).Select(i => $"m{i}").ToList();
        foreach (var text in texts)
        {
            store.TryAddIncoming(Incoming(Guid.CreateVersion7(), text: text));
        }

        Assert.Equal(texts, store.GetConversation(Peer).Select(m => m.Content));
    }

    [Fact]
    public void Advance_OnlyMovesForward()
    {
        var store = new InMemoryMessageStore();
        var id = Guid.NewGuid();
        store.AddOutgoing(Outgoing(id));

        Assert.True(store.Advance(Peer, id, DeliveryState.Sent));
        Assert.True(store.Advance(Peer, id, DeliveryState.Read));
        Assert.False(store.Advance(Peer, id, DeliveryState.Delivered));
        Assert.False(store.Advance(Peer, id, DeliveryState.Sent));
        Assert.False(store.Advance(Peer, id, DeliveryState.Failed));

        Assert.Equal(DeliveryState.Read, store.GetConversation(Peer)[0].State);
    }

    [Fact]
    public void LateAcknowledgement_OverridesAFailure()
    {
        var store = new InMemoryMessageStore();
        var id = Guid.NewGuid();
        store.AddOutgoing(Outgoing(id));
        store.Advance(Peer, id, DeliveryState.Failed, "refused");

        Assert.False(store.Advance(Peer, id, DeliveryState.Sent));
        Assert.True(store.Advance(Peer, id, DeliveryState.Delivered));

        var message = store.GetConversation(Peer)[0];
        Assert.Equal(DeliveryState.Delivered, message.State);
        Assert.Null(message.Failure);
    }

    [Fact]
    public void Advance_IgnoresOtherDevicesConversationsAndIncomingMessages()
    {
        var store = new InMemoryMessageStore();
        var id = Guid.NewGuid();
        store.AddOutgoing(Outgoing(id));
        store.TryAddIncoming(Incoming(Guid.NewGuid()));

        Assert.False(store.Advance(Guid.NewGuid(), id, DeliveryState.Delivered));
        Assert.False(store.Advance(Peer, store.GetConversation(Peer).Single(m => m.Direction == MessageDirection.Incoming).MessageId, DeliveryState.Read));
    }

    [Fact]
    public void ReadTracking_CountsUnreadAndOwesReceiptsUntilCleared()
    {
        var store = new InMemoryMessageStore();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        store.TryAddIncoming(Incoming(first));
        store.TryAddIncoming(Incoming(second, seconds: 1));
        Assert.Equal(2, store.GetConversations().Single().UnreadCount);

        Assert.True(store.MarkIncomingRead(Peer));
        Assert.False(store.MarkIncomingRead(Peer));

        var summary = store.GetConversations().Single();
        Assert.Equal(0, summary.UnreadCount);
        Assert.True(summary.HasWaitingWork);
        Assert.Equal([first, second], store.GetPendingReadReceipts(Peer));

        store.ClearPendingReadReceipts(Peer, [first]);
        Assert.Equal([second], store.GetPendingReadReceipts(Peer));

        store.ClearPendingReadReceipts(Peer, [second]);
        Assert.False(store.GetConversations().Single().HasWaitingWork);
    }

    [Fact]
    public void ConversationSize_IsBounded_AndUndeliveredMessagesAreKept()
    {
        var store = new InMemoryMessageStore(maxPerConversation: 3);
        var waiting = Guid.NewGuid();
        store.AddOutgoing(Outgoing(waiting));
        for (var i = 1; i <= 5; i++)
        {
            store.TryAddIncoming(Incoming(seconds: i));
            store.MarkIncomingRead(Peer);
            store.ClearPendingReadReceipts(Peer, store.GetPendingReadReceipts(Peer));
        }

        Assert.Equal(3, store.GetConversation(Peer).Count);
        Assert.Contains(store.GetConversation(Peer), m => m.MessageId == waiting);
    }

    [Fact]
    public void DirectConversationId_IsTheSameFromBothSides_AndFitsTheIdentifierLimit()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        Assert.Equal(DirectConversation.IdFor(a, b), DirectConversation.IdFor(b, a));
        Assert.Equal(64, DirectConversation.IdFor(a, b).Length);
    }
}
