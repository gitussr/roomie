using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Roomie.Core.Messaging;

namespace Roomie.Infrastructure.Messaging;

/// <summary>
/// Message history in <c>database\messenger.db</c> (architecture.md §6). Each call commits before it returns,
/// with a full sync to disk, because an acknowledgement promises the recipient has the message.
/// </summary>
public sealed partial class SqliteMessageStore : IMessageStore, IDisposable
{
    private const int Outgoing = 0;
    private const int Incoming = 1;

    private const string MessageColumns = "message_id, peer_id, direction, content, timestamp_ms, state, failure, read_receipt_pending";

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
        Run(() => Execute($"INSERT INTO messages ({MessageColumns}) VALUES ($id, $peer, $direction, $content, $time, $state, $failure, $receipt)", Values(message)));
    }

    public bool TryAddIncoming(StoredMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Run(() => Execute(
            $"INSERT INTO messages ({MessageColumns}) VALUES ($id, $peer, $direction, $content, $time, $state, $failure, $receipt) "
            + "ON CONFLICT (peer_id, direction, message_id) DO NOTHING",
            Values(message)) > 0);
    }

    public IReadOnlyList<StoredMessage> GetConversation(Guid peerId, int maxMessages = int.MaxValue)
    {
        return Run(() =>
        {
            var latestFirst = Query(
                $"SELECT {MessageColumns} FROM messages WHERE peer_id = $peer ORDER BY timestamp_ms DESC, seq DESC LIMIT $limit",
                ReadMessage,
                ("$peer", Text(peerId)),
                ("$limit", maxMessages));
            latestFirst.Reverse();
            return latestFirst;
        });
    }

    public IReadOnlyList<StoredMessage> GetUndelivered(Guid peerId) => Run(() => Query(
        $"SELECT {MessageColumns} FROM messages WHERE peer_id = $peer AND direction = 0 AND state < 2 ORDER BY timestamp_ms, seq",
        ReadMessage,
        ("$peer", Text(peerId))));

    public bool Advance(Guid peerId, Guid messageId, DeliveryState state, string? failure = null) => Run(() => Execute(
        "UPDATE messages SET state = $state, failure = $failure "
        + "WHERE peer_id = $peer AND direction = 0 AND message_id = $id AND state < $state",
        ("$state", StateCode(state)),
        ("$failure", state == DeliveryState.Failed ? failure : null),
        ("$peer", Text(peerId)),
        ("$id", Text(messageId))) > 0);

    public bool MarkIncomingRead(Guid peerId) => Run(() => Execute(
        "UPDATE messages SET state = 4, read_receipt_pending = 1 WHERE peer_id = $peer AND direction = 1 AND state < 4",
        ("$peer", Text(peerId))) > 0);

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
            SELECT peer_id, 1 AS unread, 0 AS waiting FROM messages WHERE direction = 1 AND state < 4
            UNION ALL
            SELECT peer_id, 0, 1 FROM messages WHERE direction = 0 AND state < 2
            UNION ALL
            SELECT peer_id, 0, 1 FROM messages WHERE read_receipt_pending = 1
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
    ];

    private static StoredMessage ReadMessage(SqliteDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetGuid(1),
        reader.GetInt32(2) == Incoming ? MessageDirection.Incoming : MessageDirection.Outgoing,
        reader.GetString(3),
        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
        StateFromCode(reader.GetInt32(5)),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.GetInt32(7) != 0);

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
