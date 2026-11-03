using Microsoft.Data.Sqlite;
using Roomie.Core.Messaging;
using Roomie.Infrastructure.Messaging;

namespace Roomie.Tests.Messaging;

public sealed class SqliteMessageStoreTests : IDisposable
{
    private static readonly Guid Peer = Guid.Parse("a82f21c0-1111-4222-8333-444455556666");
    private static readonly DateTimeOffset Start = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "roomie-tests", Guid.NewGuid().ToString("N"));
    private readonly List<SqliteMessageStore> _opened = [];

    private string DatabasePath => Path.Combine(_root, "database", "messenger.db");

    public void Dispose()
    {
        foreach (var store in _opened)
        {
            store.Dispose();
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private SqliteMessageStore Open()
    {
        var store = new SqliteMessageStore(DatabasePath);
        _opened.Add(store);
        return store;
    }

    private SqliteMessageStore Reopen(SqliteMessageStore store)
    {
        store.Dispose();
        return Open();
    }

    private static StoredMessage Incoming(Guid? id = null, int seconds = 0, string text = "hi") =>
        new(id ?? Guid.NewGuid(), Peer, MessageDirection.Incoming, text, Start.AddSeconds(seconds), DeliveryState.Delivered);

    private static StoredMessage Outgoing(Guid? id = null, int seconds = 0) =>
        new(id ?? Guid.NewGuid(), Peer, MessageDirection.Outgoing, "hello", Start.AddSeconds(seconds), DeliveryState.Pending);

    [Fact]
    public void Duplicate_IsNotStoredTwice_AndTheFirstCopyWins()
    {
        var store = Open();
        var id = Guid.NewGuid();

        Assert.True(store.TryAddIncoming(Incoming(id, text: "first")));
        Assert.False(store.TryAddIncoming(Incoming(id, text: "second")));

        Assert.Equal("first", Assert.Single(store.GetConversation(Peer)).Content);
    }

    [Fact]
    public void Duplicate_IsRecognizedAfterARestart()
    {
        var store = Open();
        var id = Guid.NewGuid();
        store.TryAddIncoming(Incoming(id));

        store = Reopen(store);

        Assert.False(store.TryAddIncoming(Incoming(id)));
        Assert.Single(store.GetConversation(Peer));
    }

    [Fact]
    public void SameMessageIdFromAnotherDevice_IsADifferentMessage()
    {
        var store = Open();
        var id = Guid.NewGuid();
        var other = Guid.NewGuid();
        store.TryAddIncoming(Incoming(id));

        Assert.True(store.TryAddIncoming(Incoming(id) with { PeerId = other }));

        Assert.Single(store.GetConversation(Peer));
        Assert.Single(store.GetConversation(other));
    }

    [Fact]
    public void IncomingMessageWithTheIdOfAnOutgoingOne_IsADifferentMessage()
    {
        var store = Open();
        var id = Guid.NewGuid();
        store.AddOutgoing(Outgoing(id));

        Assert.True(store.TryAddIncoming(Incoming(id)));
        Assert.Equal(2, store.GetConversation(Peer).Count);
        Assert.Equal(DeliveryState.Pending, store.GetUndelivered(Peer)[0].State);
    }

    [Fact]
    public void Conversation_IsInTimestampOrder_WhateverTheArrivalOrder()
    {
        var store = Open();
        store.TryAddIncoming(Incoming(seconds: 30, text: "third"));
        store.TryAddIncoming(Incoming(seconds: 10, text: "first"));
        store.TryAddIncoming(Incoming(seconds: 20, text: "second"));

        Assert.Equal(["first", "second", "third"], store.GetConversation(Peer).Select(m => m.Content));
    }

    [Fact]
    public void MessagesWithTheSameTimestamp_KeepTheirArrivalOrder_AlsoAfterARestart()
    {
        var store = Open();
        var texts = Enumerable.Range(0, 50).Select(i => $"m{i}").ToList();
        foreach (var text in texts)
        {
            // Random IDs, as within one millisecond: only the order of arrival can keep these in sequence.
            store.TryAddIncoming(Incoming(Guid.NewGuid(), text: text));
        }

        Assert.Equal(texts, store.GetConversation(Peer).Select(m => m.Content));
        Assert.Equal(texts, Reopen(store).GetConversation(Peer).Select(m => m.Content));
    }

    [Fact]
    public void Conversation_CanBeLimitedToTheLatestMessages_StillInDisplayOrder()
    {
        var store = Open();
        for (var i = 0; i < 5; i++)
        {
            store.TryAddIncoming(Incoming(seconds: i, text: $"m{i}"));
        }

        Assert.Equal(["m3", "m4"], store.GetConversation(Peer, 2).Select(m => m.Content));
    }

    [Fact]
    public void EveryField_SurvivesARestart()
    {
        var store = Open();
        var text = "नमस्ते 😀\r\nline two \"quoted\" '); DROP TABLE messages; --";
        var incoming = Incoming(text: text, seconds: 1);
        var sent = Outgoing(seconds: 2);
        var failed = Outgoing(seconds: 3);
        var waiting = Outgoing(seconds: 4);
        store.TryAddIncoming(incoming);
        store.AddOutgoing(sent);
        store.AddOutgoing(failed);
        store.AddOutgoing(waiting);
        store.Advance(Peer, sent.MessageId, DeliveryState.Sent);
        store.Advance(Peer, failed.MessageId, DeliveryState.Failed, "refused");
        store.MarkIncomingRead(Peer);

        var reopened = Reopen(store).GetConversation(Peer);

        Assert.Equal(
            [
                incoming with { State = DeliveryState.Read, ReadReceiptPending = true },
                sent with { State = DeliveryState.Sent },
                failed with { State = DeliveryState.Failed, Failure = "refused" },
                waiting,
            ],
            reopened);
    }

    [Fact]
    public void WaitingWork_SurvivesARestart()
    {
        var store = Open();
        var read = Guid.NewGuid();
        var waiting = Outgoing();
        var sent = Outgoing(seconds: 1);
        store.TryAddIncoming(Incoming(read));
        store.MarkIncomingRead(Peer);
        store.TryAddIncoming(Incoming(seconds: 2));
        store.AddOutgoing(waiting);
        store.AddOutgoing(sent);
        store.Advance(Peer, sent.MessageId, DeliveryState.Sent);

        store = Reopen(store);

        Assert.Equal([waiting.MessageId, sent.MessageId], store.GetUndelivered(Peer).Select(m => m.MessageId));
        Assert.Equal([read], store.GetPendingReadReceipts(Peer));
        var summary = Assert.Single(store.GetActiveConversations());
        Assert.Equal(1, summary.UnreadCount);
        Assert.True(summary.HasWaitingWork);
    }

    [Fact]
    public void Message_IsOnDiskWhenTheCallReturns()
    {
        var store = Open();
        var message = Incoming();
        store.TryAddIncoming(message);

        // A second connection sees only what was committed, and the first one is still open.
        Assert.Equal(message.MessageId, Assert.Single(Open().GetConversation(Peer)).MessageId);
    }

    [Fact]
    public void Advance_OnlyMovesForward()
    {
        var store = Open();
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
        var store = Open();
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
        var store = Open();
        var id = Guid.NewGuid();
        store.AddOutgoing(Outgoing(id));
        store.TryAddIncoming(Incoming(Guid.NewGuid()));

        Assert.False(store.Advance(Guid.NewGuid(), id, DeliveryState.Delivered));
        Assert.False(store.Advance(Peer, store.GetConversation(Peer).Single(m => m.Direction == MessageDirection.Incoming).MessageId, DeliveryState.Read));
    }

    [Fact]
    public void ReadTracking_CountsUnreadAndOwesReceiptsUntilCleared()
    {
        var store = Open();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        store.TryAddIncoming(Incoming(first));
        store.TryAddIncoming(Incoming(second, seconds: 1));
        Assert.Equal(2, store.GetActiveConversations().Single().UnreadCount);

        Assert.True(store.MarkIncomingRead(Peer));
        Assert.False(store.MarkIncomingRead(Peer));

        var summary = store.GetActiveConversations().Single();
        Assert.Equal(0, summary.UnreadCount);
        Assert.True(summary.HasWaitingWork);
        Assert.Equal([first, second], store.GetPendingReadReceipts(Peer));

        store.ClearPendingReadReceipts(Peer, [first]);
        Assert.Equal([second], store.GetPendingReadReceipts(Peer));

        store.ClearPendingReadReceipts(Peer, [second]);
        Assert.Empty(store.GetActiveConversations());
        Assert.Equal(2, store.GetConversation(Peer).Count);
    }

    [Fact]
    public void ActiveConversations_AreKeptApart_AndFinishedOnesAreLeftOut()
    {
        var store = Open();
        var other = Guid.NewGuid();
        var finished = Guid.NewGuid();
        var delivered = Outgoing() with { PeerId = finished };
        store.TryAddIncoming(Incoming());
        store.TryAddIncoming(Incoming(seconds: 1));
        store.AddOutgoing(Outgoing() with { PeerId = other });
        store.AddOutgoing(delivered);
        store.Advance(finished, delivered.MessageId, DeliveryState.Delivered);

        var active = store.GetActiveConversations().ToDictionary(c => c.PeerId);

        Assert.Equal(2, active.Count);
        Assert.Equal(2, active[Peer].UnreadCount);
        Assert.False(active[Peer].HasWaitingWork);
        Assert.Equal(0, active[other].UnreadCount);
        Assert.True(active[other].HasWaitingWork);
    }

    [Fact]
    public void FailedMessage_IsNotWaitingToBeSent()
    {
        var store = Open();
        var id = Guid.NewGuid();
        store.AddOutgoing(Outgoing(id));

        store.Advance(Peer, id, DeliveryState.Failed, "refused");

        Assert.Empty(store.GetUndelivered(Peer));
        Assert.Empty(store.GetActiveConversations());
    }

    [Fact]
    public void Database_ReachesTheLatestVersion_AndOpensAgainWithoutRepeatingMigrations()
    {
        var store = Open();
        store.TryAddIncoming(Incoming());
        store.Dispose();

        Assert.Equal(3, ReadUserVersion());
        Assert.Single(Open().GetConversation(Peer));
        Assert.Equal(3, ReadUserVersion());
    }

    /// <summary>The exact schema 0.7.0 shipped, with a message in each state: real databases look like this.</summary>
    [Fact]
    public void DatabaseFromVersion7_IsUpgraded_AndKeepsEverything()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        var waiting = Guid.NewGuid();
        var read = Guid.NewGuid();
        using (var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                CREATE TABLE messages (
                    seq INTEGER PRIMARY KEY AUTOINCREMENT,
                    message_id TEXT NOT NULL,
                    peer_id TEXT NOT NULL,
                    direction INTEGER NOT NULL CHECK (direction IN (0, 1)),
                    content TEXT NOT NULL,
                    timestamp_ms INTEGER NOT NULL,
                    state INTEGER NOT NULL CHECK (state BETWEEN 0 AND 4),
                    failure TEXT,
                    read_receipt_pending INTEGER NOT NULL DEFAULT 0 CHECK (read_receipt_pending IN (0, 1)),
                    UNIQUE (peer_id, direction, message_id)
                ) STRICT;
                CREATE INDEX messages_by_time ON messages (peer_id, timestamp_ms, seq);
                CREATE INDEX messages_undelivered ON messages (peer_id, timestamp_ms, seq) WHERE direction = 0 AND state < 2;
                CREATE INDEX messages_unread ON messages (peer_id) WHERE direction = 1 AND state < 4;
                CREATE INDEX messages_receipts_owed ON messages (peer_id, seq) WHERE read_receipt_pending = 1;
                INSERT INTO messages (message_id, peer_id, direction, content, timestamp_ms, state) VALUES ('{waiting:D}', '{Peer:D}', 0, 'waiting', 1000, 0);
                INSERT INTO messages (message_id, peer_id, direction, content, timestamp_ms, state, read_receipt_pending) VALUES ('{read:D}', '{Peer:D}', 1, 'read', 2000, 4, 1);
                PRAGMA user_version = 1;
                """;
            command.ExecuteNonQuery();
        }

        var store = Open();

        Assert.Equal(["waiting", "read"], store.GetConversation(Peer).Select(m => m.Content));
        Assert.Equal(waiting, Assert.Single(store.GetUndelivered(Peer)).MessageId);
        Assert.Equal([read], store.GetPendingReadReceipts(Peer));
        Assert.Null(store.GetConversation(Peer)[0].GroupId);
        Assert.False(store.TryAddIncoming(Incoming(read)));
        store.Dispose();
        Assert.Equal(3, ReadUserVersion());
    }

    private const string GroupA = "group-a";

    private static StoredMessage OutgoingToGroup(int seconds = 0) => new(
        Guid.NewGuid(), Guid.Empty, MessageDirection.Outgoing, "to all", Start.AddSeconds(seconds), DeliveryState.Delivered, GroupId: GroupA, MembershipVersion: 1);

    [Fact]
    public void GroupCopies_AreTrackedForEachRecipient()
    {
        var store = Open();
        var other = Guid.NewGuid();
        var message = OutgoingToGroup();
        store.AddOutgoingGroup(message, [Peer, other]);

        var copy = Assert.Single(store.GetUndelivered(Peer));
        Assert.Equal(message.MessageId, copy.MessageId);
        Assert.Equal(Peer, copy.PeerId);
        Assert.Equal(GroupA, copy.GroupId);
        Assert.Equal(1, copy.MembershipVersion);

        Assert.True(store.Advance(Peer, message.MessageId, DeliveryState.Delivered));
        Assert.Empty(store.GetUndelivered(Peer));
        Assert.Single(store.GetUndelivered(other));

        var recipients = Assert.Single(store.GetGroupConversation(GroupA)).Recipients!.ToDictionary(r => r.DeviceId);
        Assert.Equal(DeliveryState.Delivered, recipients[Peer].State);
        Assert.Equal(DeliveryState.Pending, recipients[other].State);
    }

    [Fact]
    public void GroupCopy_CanOnlyBeConfirmedByItsOwnRecipient_AndOnlyForward()
    {
        var store = Open();
        var message = OutgoingToGroup();
        store.AddOutgoingGroup(message, [Peer]);

        Assert.False(store.Advance(Guid.NewGuid(), message.MessageId, DeliveryState.Delivered));
        Assert.True(store.Advance(Peer, message.MessageId, DeliveryState.Read));
        Assert.False(store.Advance(Peer, message.MessageId, DeliveryState.Delivered));
    }

    [Fact]
    public void GroupMessages_StayOutOfOneToOneConversations()
    {
        var store = Open();
        var fromGroup = Incoming() with { GroupId = GroupA, MembershipVersion = 1 };
        store.TryAddIncoming(fromGroup);
        store.TryAddIncoming(Incoming(seconds: 1, text: "direct"));

        Assert.Equal("direct", Assert.Single(store.GetConversation(Peer)).Content);
        Assert.Equal(fromGroup.MessageId, Assert.Single(store.GetGroupConversation(GroupA)).MessageId);
        Assert.Equal(1, Assert.Single(store.GetActiveConversations()).UnreadCount);
        Assert.Equal(1, store.GetGroupUnreadCounts()[GroupA]);

        store.MarkIncomingRead(Peer);
        Assert.Equal(1, store.GetGroupUnreadCounts()[GroupA]);

        Assert.Equal([Peer], store.MarkGroupRead(GroupA));
        Assert.Empty(store.GetGroupUnreadCounts());
        Assert.Contains(fromGroup.MessageId, store.GetPendingReadReceipts(Peer));
        Assert.Empty(store.MarkGroupRead(GroupA));
    }

    [Fact]
    public void WaitingGroupCopy_CountsAsWorkForThatDevice_AndSurvivesARestart()
    {
        var store = Open();
        var message = OutgoingToGroup();
        store.AddOutgoingGroup(message, [Peer]);

        store = Reopen(store);

        Assert.True(Assert.Single(store.GetActiveConversations()).HasWaitingWork);
        Assert.Equal(Peer, store.GetActiveConversations()[0].PeerId);
        Assert.Equal(message.MessageId, Assert.Single(store.GetUndelivered(Peer)).MessageId);
    }

    [Fact]
    public void GroupDuplicate_FromTheSameSender_IsNotStoredTwice()
    {
        var store = Open();
        var message = Incoming() with { GroupId = GroupA, MembershipVersion = 1 };

        Assert.True(store.TryAddIncoming(message));
        Assert.False(store.TryAddIncoming(message));
        Assert.True(store.TryAddIncoming(message with { PeerId = Guid.NewGuid() }));
    }

    [Fact]
    public void DatabaseFromANewerVersion_IsRefusedAndLeftAlone()
    {
        Open().Dispose();
        using (var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 999";
            command.ExecuteNonQuery();
        }

        var error = Assert.Throws<MessageStoreException>(() => new SqliteMessageStore(DatabasePath));

        Assert.Contains("newer version", error.Message, StringComparison.Ordinal);
        Assert.Equal(999, ReadUserVersion());
    }

    [Fact]
    public void DamagedDatabase_IsRefusedAndNotOverwritten()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        var garbage = Enumerable.Range(0, 8192).Select(i => (byte)(i * 7)).ToArray();
        File.WriteAllBytes(DatabasePath, garbage);

        Assert.Throws<MessageStoreException>(() => new SqliteMessageStore(DatabasePath));

        Assert.Equal(garbage, File.ReadAllBytes(DatabasePath));
    }

    [Fact]
    public void ClosedStore_FailsWithTheStoreException()
    {
        var store = Open();
        store.Dispose();
        store.Dispose();

        Assert.Throws<MessageStoreException>(() => store.GetConversation(Peer));
        Assert.Throws<MessageStoreException>(() => store.AddOutgoing(Outgoing()));
    }

    [Fact]
    public void DirectConversationId_IsTheSameFromBothSides_AndFitsTheIdentifierLimit()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        Assert.Equal(DirectConversation.IdFor(a, b), DirectConversation.IdFor(b, a));
        Assert.Equal(64, DirectConversation.IdFor(a, b).Length);
    }

    [Fact]
    public void Transfer_OnlyMovesFromTheStatesAllowed_AndAnEndedOneStaysEnded()
    {
        var store = Open();
        var id = Guid.NewGuid();
        store.AddTransfer(new Roomie.Core.Files.StoredTransfer(id, Peer, MessageDirection.Outgoing, "a.txt", 1, new string('a', 64), "text/plain", Roomie.Core.Files.FileTransferState.Waiting, Start, "C:\\a.txt"));

        Assert.False(store.TryMoveTransfer(Peer, MessageDirection.Outgoing, id, Roomie.Core.Files.FileTransferState.Verifying, null, null, Roomie.Core.Files.FileTransferState.Transferring));
        Assert.Equal(Roomie.Core.Files.FileTransferState.Waiting, store.GetTransfer(Peer, MessageDirection.Outgoing, id)!.State);
        Assert.Equal("C:\\a.txt", store.GetTransfer(Peer, MessageDirection.Outgoing, id)!.LocalPath);

        Assert.True(store.TryMoveTransfer(Peer, MessageDirection.Outgoing, id, Roomie.Core.Files.FileTransferState.Canceled, "C:\\a.txt", null, Roomie.Core.Files.FileTransferState.Waiting));
        Assert.False(store.TryMoveTransfer(Peer, MessageDirection.Outgoing, id, Roomie.Core.Files.FileTransferState.Offered, null, null, Roomie.Core.Files.FileTransferState.Waiting));
        Assert.Equal(Roomie.Core.Files.FileTransferState.Canceled, store.GetTransfer(Peer, MessageDirection.Outgoing, id)!.State);

        // Another device, and the other direction, are different transfers.
        Assert.False(store.TryMoveTransfer(Guid.NewGuid(), MessageDirection.Outgoing, id, Roomie.Core.Files.FileTransferState.Failed, null, null, Roomie.Core.Files.FileTransferState.Canceled));
        Assert.False(store.TryMoveTransfer(Peer, MessageDirection.Incoming, id, Roomie.Core.Files.FileTransferState.Failed, null, null, Roomie.Core.Files.FileTransferState.Canceled));
    }

    private long ReadUserVersion()
    {
        using var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return (long)command.ExecuteScalar()!;
    }
}
