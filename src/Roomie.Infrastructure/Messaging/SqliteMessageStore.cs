using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Roomie.Core.Groups;
using Roomie.Core.Messaging;

namespace Roomie.Infrastructure.Messaging;

/// <summary>
/// Message history in <c>database\messenger.db</c> (architecture.md §6). Each call commits before it returns,
/// with a full sync to disk, because an acknowledgement promises the recipient has the message.
/// </summary>
public sealed partial class SqliteMessageStore : IMessageStore, IGroupStore, IDisposable
{
    private const int Outgoing = 0;
    private const int Incoming = 1;

    private const string MessageColumns = "message_id, peer_id, direction, content, timestamp_ms, state, failure, read_receipt_pending, group_id, membership_version";

    private const string InsertMessage =
        $"INSERT INTO messages ({MessageColumns}) VALUES ($id, $peer, $direction, $content, $time, $state, $failure, $receipt, $group, $version)";

    // Run in order, each in its own transaction, tracked by user_version. Never edit one that has shipped: add another.
    //
    // state holds the codes from StateCode, which are also the order states may advance in.
    // UNIQUE is the (message_id, sender_id) rule of architecture.md §6: the sender of an incoming message is
    // its peer, and of an outgoing message this device.
    // seq keeps the arrival order of messages with the same timestamp.
    private static readonly string[] Migrations =
    [
        """
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

        PRAGMA user_version = 1;
        """,

        // An outgoing group message has one row here and a message_recipients row per member. Its peer_id is
        // the empty ID, and its own state stays Delivered so the one-to-one queries never treat it as waiting.
        // An incoming group message keeps its sender in peer_id, so (message_id, sender_id) still holds.
        """
        ALTER TABLE messages ADD COLUMN group_id TEXT;
        ALTER TABLE messages ADD COLUMN membership_version INTEGER;
        CREATE INDEX messages_by_group ON messages (group_id, timestamp_ms, seq) WHERE group_id IS NOT NULL;

        CREATE TABLE message_recipients (
            message_seq INTEGER NOT NULL REFERENCES messages (seq) ON DELETE CASCADE,
            recipient_id TEXT NOT NULL,
            state INTEGER NOT NULL CHECK (state BETWEEN 0 AND 4),
            failure TEXT,
            PRIMARY KEY (message_seq, recipient_id)
        ) STRICT, WITHOUT ROWID;
        CREATE INDEX message_recipients_undelivered ON message_recipients (recipient_id, message_seq) WHERE state < 2;

        CREATE TABLE groups (
            group_id TEXT PRIMARY KEY,
            document TEXT NOT NULL,
            signature TEXT NOT NULL,
            version INTEGER NOT NULL,
            is_member INTEGER NOT NULL CHECK (is_member IN (0, 1))
        ) STRICT, WITHOUT ROWID;

        CREATE TABLE group_invites (
            group_id TEXT NOT NULL,
            peer_id TEXT NOT NULL,
            direction INTEGER NOT NULL CHECK (direction IN (0, 1)),
            invite_id TEXT NOT NULL,
            document TEXT,
            signature TEXT,
            response INTEGER NOT NULL CHECK (response IN (0, 1, 2)),
            PRIMARY KEY (group_id, peer_id, direction)
        ) STRICT, WITHOUT ROWID;

        CREATE TABLE group_removal_notices (
            group_id TEXT NOT NULL,
            peer_id TEXT NOT NULL,
            document TEXT NOT NULL,
            signature TEXT NOT NULL,
            PRIMARY KEY (group_id, peer_id)
        ) STRICT, WITHOUT ROWID;

        PRAGMA user_version = 2;
        """,
    ];

    private readonly Lock _gate = new();
    private readonly SqliteConnection _connection;
    private readonly ILogger _logger;
    private bool _disposed;

    /// <exception cref="MessageStoreException">
    /// The database can't be opened or was made by a newer Roomie. It is left as it is, never recreated.
    /// </exception>
    public SqliteMessageStore(string path, ILogger<SqliteMessageStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _logger = logger ?? NullLogger<SqliteMessageStore>.Instance;

        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        // No pooling, so disposing really closes the file. The timeout is short because the UI thread reads too.
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 5,
        }.ToString());

        try
        {
            _connection.Open();
            Execute("PRAGMA journal_mode = WAL");

            // WAL's usual NORMAL setting can lose the latest commits in a power cut, after they were acknowledged.
            Execute("PRAGMA synchronous = FULL");
            Execute("PRAGMA foreign_keys = ON");
            Migrate();
        }
        catch (Exception ex) when (ex is SqliteException or MessageStoreException)
        {
            _connection.Dispose();
            throw ex as MessageStoreException
                ?? new MessageStoreException($"The message database can't be opened: {((SqliteException)ex).Message}", ex);
        }
    }

    public void AddOutgoing(StoredMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Run(() => Execute(InsertMessage, Values(message)));
    }

    public bool TryAddIncoming(StoredMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Run(() => Execute(InsertMessage + " ON CONFLICT (peer_id, direction, message_id) DO NOTHING", Values(message)) > 0);
    }

    public void AddOutgoingGroup(StoredMessage message, IReadOnlyCollection<Guid> recipients)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(recipients);
        Run(() =>
        {
            using var transaction = _connection.BeginTransaction();
            Execute(InsertMessage, Values(message with { PeerId = Guid.Empty, State = DeliveryState.Delivered }));
            foreach (var recipient in recipients)
            {
                Execute(
                    "INSERT INTO message_recipients (message_seq, recipient_id, state) VALUES (last_insert_rowid(), $recipient, 0)",
                    ("$recipient", Text(recipient)));
            }

            transaction.Commit();
            return 0;
        });
    }

    public IReadOnlyList<StoredMessage> GetConversation(Guid peerId, int maxMessages = int.MaxValue)
    {
        return Run(() =>
        {
            var latestFirst = Query(
                $"SELECT {MessageColumns} FROM messages WHERE peer_id = $peer AND group_id IS NULL ORDER BY timestamp_ms DESC, seq DESC LIMIT $limit",
                ReadMessage,
                ("$peer", Text(peerId)),
                ("$limit", maxMessages));
            latestFirst.Reverse();
            return latestFirst;
        });
    }

    public IReadOnlyList<StoredMessage> GetGroupConversation(string groupId, int maxMessages = int.MaxValue)
    {
        return Run(() =>
        {
            var latestFirst = Query(
                $"SELECT {MessageColumns} FROM messages WHERE group_id = $group ORDER BY timestamp_ms DESC, seq DESC LIMIT $limit",
                ReadMessage,
                ("$group", groupId),
                ("$limit", maxMessages));
            latestFirst.Reverse();

            var recipients = Query(
                """
                SELECT m.message_id, r.recipient_id, r.state, r.failure
                FROM message_recipients r JOIN messages m ON m.seq = r.message_seq
                WHERE m.group_id = $group
                """,
                reader => (MessageId: reader.GetGuid(0), Recipient: new MessageRecipient(reader.GetGuid(1), StateFromCode(reader.GetInt32(2)), reader.IsDBNull(3) ? null : reader.GetString(3))),
                ("$group", groupId)).ToLookup(r => r.MessageId, r => r.Recipient);

            return latestFirst
                .Select(m => m.Direction == MessageDirection.Outgoing ? m with { Recipients = recipients[m.MessageId].ToList() } : m)
                .ToList();
        });
    }

    public IReadOnlyList<StoredMessage> GetUndelivered(Guid peerId) => Run(() => Query(
        $"""
        SELECT {MessageColumns}, seq FROM messages WHERE peer_id = $peer AND direction = 0 AND state < 2
        UNION ALL
        SELECT m.message_id, r.recipient_id, m.direction, m.content, m.timestamp_ms, r.state, r.failure, m.read_receipt_pending, m.group_id, m.membership_version, m.seq
        FROM message_recipients r JOIN messages m ON m.seq = r.message_seq
        WHERE r.recipient_id = $peer AND r.state < 2
        ORDER BY timestamp_ms, seq
        """,
        ReadMessage,
        ("$peer", Text(peerId))));

    public bool Advance(Guid peerId, Guid messageId, DeliveryState state, string? failure = null) => Run(() =>
    {
        var parameters = new (string Name, object? Value)[]
        {
            ("$state", StateCode(state)),
            ("$failure", state == DeliveryState.Failed ? failure : null),
            ("$peer", Text(peerId)),
            ("$id", Text(messageId)),
            ("$none", Text(Guid.Empty)),
        };

        return Execute(
            "UPDATE messages SET state = $state, failure = $failure "
            + "WHERE peer_id = $peer AND direction = 0 AND message_id = $id AND state < $state AND group_id IS NULL",
            parameters) > 0
            || Execute(
                """
                UPDATE message_recipients SET state = $state, failure = $failure
                WHERE recipient_id = $peer AND state < $state
                AND message_seq = (SELECT seq FROM messages WHERE peer_id = $none AND direction = 0 AND message_id = $id)
                """,
                parameters) > 0;
    });

    public bool MarkIncomingRead(Guid peerId) => Run(() => Execute(
        "UPDATE messages SET state = 4, read_receipt_pending = 1 WHERE peer_id = $peer AND direction = 1 AND state < 4 AND group_id IS NULL",
        ("$peer", Text(peerId))) > 0);

    public IReadOnlyList<Guid> MarkGroupRead(string groupId) => Run(() =>
    {
        using var transaction = _connection.BeginTransaction();
        var senders = Query(
            "SELECT DISTINCT peer_id FROM messages WHERE group_id = $group AND direction = 1 AND state < 4",
            reader => reader.GetGuid(0),
            ("$group", groupId));
        Execute(
            "UPDATE messages SET state = 4, read_receipt_pending = 1 WHERE group_id = $group AND direction = 1 AND state < 4",
            ("$group", groupId));
        transaction.Commit();
        return senders;
    });

    public IReadOnlyDictionary<string, int> GetGroupUnreadCounts() => Run(() => Query(
        "SELECT group_id, COUNT(*) FROM messages WHERE direction = 1 AND state < 4 AND group_id IS NOT NULL GROUP BY group_id",
        reader => (Group: reader.GetString(0), Count: reader.GetInt32(1))).ToDictionary(r => r.Group, r => r.Count));

    public StoredGroup? GetGroup(string groupId) => Run(() => Query(
        "SELECT document, signature, version, is_member FROM groups WHERE group_id = $group",
        ReadGroup,
        ("$group", groupId)).SingleOrDefault());

    public IReadOnlyList<StoredGroup> GetGroups() => Run(() => Query(
        "SELECT document, signature, version, is_member FROM groups",
        ReadGroup));

    public void SaveGroup(StoredGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        Run(() =>
        {
            using var transaction = _connection.BeginTransaction();
            Execute(
                """
                INSERT INTO groups (group_id, document, signature, version, is_member) VALUES ($group, $document, $signature, $version, $member)
                ON CONFLICT (group_id) DO UPDATE SET document = $document, signature = $signature, version = $version, is_member = $member
                """,
                ("$group", group.State.GroupId),
                ("$document", group.SignedDocument.Document),
                ("$signature", group.SignedDocument.Signature),
                ("$version", group.Version),
                ("$member", group.IsMember ? 1 : 0));

            var waiting = Query(
                """
                SELECT r.message_seq, r.recipient_id FROM message_recipients r JOIN messages m ON m.seq = r.message_seq
                WHERE m.group_id = $group AND r.state < 2
                """,
                reader => (Seq: reader.GetInt64(0), Recipient: reader.GetGuid(1)),
                ("$group", group.State.GroupId));
            foreach (var (seq, recipient) in waiting.Where(w => !group.IsMember || !group.State.HasMember(w.Recipient)))
            {
                Execute(
                    "UPDATE message_recipients SET state = 2, failure = $failure WHERE message_seq = $seq AND recipient_id = $recipient",
                    ("$failure", group.IsMember ? "No longer in this group." : "You're no longer in this group."),
                    ("$seq", seq),
                    ("$recipient", Text(recipient)));
            }

            transaction.Commit();
            return 0;
        });
    }

    public StoredInvite? GetInvite(string groupId, Guid peerId, InviteDirection direction) => Run(() => Query(
        "SELECT group_id, peer_id, direction, invite_id, document, signature, response FROM group_invites "
        + "WHERE group_id = $group AND peer_id = $peer AND direction = $direction",
        ReadInvite,
        ("$group", groupId),
        ("$peer", Text(peerId)),
        ("$direction", (int)direction)).SingleOrDefault());

    public IReadOnlyList<StoredInvite> GetInvites() => Run(() => Query(
        "SELECT group_id, peer_id, direction, invite_id, document, signature, response FROM group_invites",
        ReadInvite));

    public void SaveInvite(StoredInvite invite)
    {
        ArgumentNullException.ThrowIfNull(invite);
        Run(() => Execute(
            """
            INSERT INTO group_invites (group_id, peer_id, direction, invite_id, document, signature, response)
            VALUES ($group, $peer, $direction, $invite, $document, $signature, $response)
            ON CONFLICT (group_id, peer_id, direction) DO UPDATE SET invite_id = $invite, document = $document, signature = $signature, response = $response
            """,
            ("$group", invite.GroupId),
            ("$peer", Text(invite.PeerId)),
            ("$direction", (int)invite.Direction),
            ("$invite", Text(invite.InviteId)),
            ("$document", invite.SignedDocument?.Document),
            ("$signature", invite.SignedDocument?.Signature),
            ("$response", (int)invite.Response)));
    }

    public void DeleteInvite(string groupId, Guid peerId, InviteDirection direction) => Run(() => Execute(
        "DELETE FROM group_invites WHERE group_id = $group AND peer_id = $peer AND direction = $direction",
        ("$group", groupId),
        ("$peer", Text(peerId)),
        ("$direction", (int)direction)));

    public IReadOnlyList<StoredRemovalNotice> GetRemovalNotices(Guid peerId) => Run(() => Query(
        "SELECT group_id, peer_id, document, signature FROM group_removal_notices WHERE peer_id = $peer",
        ReadRemovalNotice,
        ("$peer", Text(peerId))));

    public StoredRemovalNotice? GetRemovalNotice(string groupId, Guid peerId) => Run(() => Query(
        "SELECT group_id, peer_id, document, signature FROM group_removal_notices WHERE group_id = $group AND peer_id = $peer",
        ReadRemovalNotice,
        ("$group", groupId),
        ("$peer", Text(peerId))).SingleOrDefault());

    public void SaveRemovalNotice(StoredRemovalNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        Run(() => Execute(
            """
            INSERT INTO group_removal_notices (group_id, peer_id, document, signature) VALUES ($group, $peer, $document, $signature)
            ON CONFLICT (group_id, peer_id) DO UPDATE SET document = $document, signature = $signature
            """,
            ("$group", notice.GroupId),
            ("$peer", Text(notice.PeerId)),
            ("$document", notice.SignedDocument.Document),
            ("$signature", notice.SignedDocument.Signature)));
    }

    public void DeleteRemovalNotice(string groupId, Guid peerId) => Run(() => Execute(
        "DELETE FROM group_removal_notices WHERE group_id = $group AND peer_id = $peer",
        ("$group", groupId),
        ("$peer", Text(peerId))));

    public IReadOnlyList<Guid> GetPendingReadReceipts(Guid peerId) => Run(() => Query(
        "SELECT message_id FROM messages WHERE peer_id = $peer AND read_receipt_pending = 1 ORDER BY seq",
        reader => reader.GetGuid(0),
        ("$peer", Text(peerId))));

    public void ClearPendingReadReceipts(Guid peerId, IReadOnlyCollection<Guid> messageIds)
    {
        ArgumentNullException.ThrowIfNull(messageIds);
        Run(() =>
        {
            using var transaction = _connection.BeginTransaction();
            foreach (var id in messageIds)
            {
                Execute(
                    "UPDATE messages SET read_receipt_pending = 0 WHERE peer_id = $peer AND direction = 1 AND message_id = $id",
                    ("$peer", Text(peerId)),
                    ("$id", Text(id)));
            }

            transaction.Commit();
            return 0;
        });
    }

    // One branch per partial index, so this doesn't scan the whole history. It runs on every screen refresh.
    public IReadOnlyList<ConversationSummary> GetActiveConversations() => Run(() => Query(
        """
        SELECT peer_id, SUM(unread), MAX(waiting) FROM (
            SELECT peer_id, 1 AS unread, 0 AS waiting FROM messages WHERE direction = 1 AND state < 4 AND group_id IS NULL
            UNION ALL
            SELECT peer_id, 0, 1 FROM messages WHERE direction = 0 AND state < 2
            UNION ALL
            SELECT peer_id, 0, 1 FROM messages WHERE read_receipt_pending = 1
            UNION ALL
            SELECT recipient_id, 0, 1 FROM message_recipients WHERE state < 2
        ) GROUP BY peer_id
        """,
        reader => new ConversationSummary(reader.GetGuid(0), reader.GetInt32(1), reader.GetInt32(2) != 0)));

    public void Dispose()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _disposed = true;
                _connection.Dispose();
            }
        }
    }

    private void Migrate()
    {
        var version = Query("PRAGMA user_version", reader => reader.GetInt32(0))[0];
        if (version > Migrations.Length)
        {
            throw new MessageStoreException("The message database was made by a newer version of Roomie. Update Roomie to open it.");
        }

        for (; version < Migrations.Length; version++)
        {
            using var transaction = _connection.BeginTransaction();
            Execute(Migrations[version]);
            transaction.Commit();
        }
    }

    private T Run<T>(Func<T> action)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                throw new MessageStoreException("The message database is closed.");
            }

            try
            {
                return action();
            }
            catch (Exception ex) when (ex is SqliteException or FormatException)
            {
                LogFailed(_logger, ex);
                throw new MessageStoreException("The message database couldn't be read or written.", ex);
            }
        }
    }

    private int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = CreateCommand(sql, parameters);
        return command.ExecuteNonQuery();
    }

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> read, params (string Name, object? Value)[] parameters)
    {
        using var command = CreateCommand(sql, parameters);
        using var reader = command.ExecuteReader();
        var results = new List<T>();
        while (reader.Read())
        {
            results.Add(read(reader));
        }

        return results;
    }

    private SqliteCommand CreateCommand(string sql, (string Name, object? Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private static (string Name, object? Value)[] Values(StoredMessage message) =>
    [
        ("$id", Text(message.MessageId)),
        ("$peer", Text(message.PeerId)),
        ("$direction", message.Direction == MessageDirection.Incoming ? Incoming : Outgoing),
        ("$content", message.Content),
        ("$time", message.Timestamp.ToUnixTimeMilliseconds()),
        ("$state", StateCode(message.State)),
        ("$failure", message.Failure),
        ("$receipt", message.ReadReceiptPending ? 1 : 0),
        ("$group", message.GroupId),
        ("$version", message.MembershipVersion),
    ];

    private static StoredMessage ReadMessage(SqliteDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetGuid(1),
        reader.GetInt32(2) == Incoming ? MessageDirection.Incoming : MessageDirection.Outgoing,
        reader.GetString(3),
        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
        StateFromCode(reader.GetInt32(5)),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.GetInt32(7) != 0,
        reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetInt32(9));

    private static StoredGroup ReadGroup(SqliteDataReader reader)
    {
        var signedDocument = new SignedGroupDocument(reader.GetString(0), reader.GetString(1));
        return GroupProtocol.TryReadState(signedDocument, out var state)
            ? new StoredGroup(signedDocument, state, reader.GetInt32(2), reader.GetInt32(3) != 0)
            : throw new FormatException("A saved group can't be read.");
    }

    private static StoredInvite ReadInvite(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetGuid(1),
        (InviteDirection)reader.GetInt32(2),
        reader.GetGuid(3),
        reader.IsDBNull(4) ? null : new SignedGroupDocument(reader.GetString(4), reader.GetString(5)),
        (InviteResponse)reader.GetInt32(6));

    private static StoredRemovalNotice ReadRemovalNotice(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetGuid(1),
        new SignedGroupDocument(reader.GetString(2), reader.GetString(3)));

    private static string Text(Guid id) => id.ToString("D");

    /// <summary>Stored values, so they must not change. A message only ever moves to a higher code.</summary>
    private static int StateCode(DeliveryState state) => state switch
    {
        DeliveryState.Pending => 0,
        DeliveryState.Sent => 1,
        DeliveryState.Failed => 2,
        DeliveryState.Delivered => 3,
        DeliveryState.Read => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    private static DeliveryState StateFromCode(int code) => code switch
    {
        0 => DeliveryState.Pending,
        1 => DeliveryState.Sent,
        2 => DeliveryState.Failed,
        3 => DeliveryState.Delivered,
        4 => DeliveryState.Read,
        _ => throw new FormatException($"Unknown message state {code}."),
    };

    [LoggerMessage(Level = LogLevel.Error, Message = "A message database operation failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
