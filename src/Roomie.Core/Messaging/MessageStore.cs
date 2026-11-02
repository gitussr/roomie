namespace Roomie.Core.Messaging;

public enum MessageDirection
{
    Outgoing,
    Incoming,
}

/// <summary>The delivery states of architecture.md §6. An incoming message is Delivered until the user reads it.</summary>
public enum DeliveryState
{
    /// <summary>Saved locally, awaiting transmission.</summary>
    Pending,

    /// <summary>Written to the connection. This says nothing about whether the recipient stored it.</summary>
    Sent,

    /// <summary>The recipient stored it and acknowledged it.</summary>
    Delivered,

    /// <summary>The recipient's user read it.</summary>
    Read,

    /// <summary>The recipient refused it, and sending it again wouldn't help.</summary>
    Failed,
}

/// <param name="Timestamp">Creation time on the sender. For display order only, never for security decisions.</param>
/// <param name="Failure">Why the message wasn't delivered, when <see cref="State"/> is Failed.</param>
/// <param name="ReadReceiptPending">An incoming message was read, and the sender hasn't been told yet.</param>
public sealed record StoredMessage(
    Guid MessageId,
    Guid PeerId,
    MessageDirection Direction,
    string Content,
    DateTimeOffset Timestamp,
    DeliveryState State,
    string? Failure = null,
    bool ReadReceiptPending = false);

public sealed record ConversationSummary(Guid PeerId, int UnreadCount, bool HasWaitingWork);

/// <summary>
/// Local message history. A message is identified by its ID and sender (architecture.md §6), so one a peer
/// sends with the ID of one of ours is a different message and can't affect ours.
/// </summary>
public interface IMessageStore
{
    void AddOutgoing(StoredMessage message);

    /// <summary>Saves a received message. When this returns, the message must be safe to acknowledge.</summary>
    /// <returns>False if this message was already stored; the existing copy is kept unchanged.</returns>
    bool TryAddIncoming(StoredMessage message);

    /// <summary>A device's messages in display order.</summary>
    IReadOnlyList<StoredMessage> GetConversation(Guid peerId);

    /// <summary>Outgoing messages that haven't been acknowledged, oldest first.</summary>
    IReadOnlyList<StoredMessage> GetUndelivered(Guid peerId);

    /// <summary>
    /// Moves an outgoing message forward: Pending, Sent, Delivered, Read. It never moves back, so a late or
    /// repeated acknowledgement can't undo a read receipt. Failed applies only before delivery; a later
    /// acknowledgement overrides it, because the message did arrive.
    /// </summary>
    /// <returns>False if the message is unknown or the change would be a step backwards.</returns>
    bool Advance(Guid peerId, Guid messageId, DeliveryState state, string? failure = null);

    /// <summary>Marks every unread incoming message as read and owing a read receipt.</summary>
    /// <returns>Whether anything changed.</returns>
    bool MarkIncomingRead(Guid peerId);

    IReadOnlyList<Guid> GetPendingReadReceipts(Guid peerId);

    void ClearPendingReadReceipts(Guid peerId, IReadOnlyCollection<Guid> messageIds);

    IReadOnlyList<ConversationSummary> GetConversations();
}

/// <summary>
/// Keeps messages only until Roomie closes; phase 7 replaces it with SQLite. Each conversation is bounded, so
/// a peer can't fill memory: the oldest finished messages go first, and duplicate detection forgets them.
/// </summary>
public sealed class InMemoryMessageStore(int maxPerConversation = InMemoryMessageStore.DefaultMaxPerConversation) : IMessageStore
{
    public const int DefaultMaxPerConversation = 2000;

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, List<StoredMessage>> _conversations = [];

    public void AddOutgoing(StoredMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_gate)
        {
            Insert(message);
        }
    }

    public bool TryAddIncoming(StoredMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_gate)
        {
            if (FindIndex(message.PeerId, message.MessageId, MessageDirection.Incoming) >= 0)
            {
                return false;
            }

            Insert(message);
            return true;
        }
    }

    public IReadOnlyList<StoredMessage> GetConversation(Guid peerId)
    {
        lock (_gate)
        {
            return _conversations.TryGetValue(peerId, out var list) ? [.. list] : [];
        }
    }

    public IReadOnlyList<StoredMessage> GetUndelivered(Guid peerId)
    {
        lock (_gate)
        {
            return _conversations.TryGetValue(peerId, out var list) ? list.Where(IsUndelivered).ToList() : [];
        }
    }

    public bool Advance(Guid peerId, Guid messageId, DeliveryState state, string? failure = null)
    {
        lock (_gate)
        {
            var index = FindIndex(peerId, messageId, MessageDirection.Outgoing);
            if (index < 0)
            {
                return false;
            }

            var list = _conversations[peerId];
            if (Rank(state) <= Rank(list[index].State))
            {
                return false;
            }

            list[index] = list[index] with { State = state, Failure = state == DeliveryState.Failed ? failure : null };
            return true;
        }
    }

    public bool MarkIncomingRead(Guid peerId)
    {
        lock (_gate)
        {
            if (!_conversations.TryGetValue(peerId, out var list))
            {
                return false;
            }

            var changed = false;
            for (var i = 0; i < list.Count; i++)
            {
                if (IsUnread(list[i]))
                {
                    list[i] = list[i] with { State = DeliveryState.Read, ReadReceiptPending = true };
                    changed = true;
                }
            }

            return changed;
        }
    }

    public IReadOnlyList<Guid> GetPendingReadReceipts(Guid peerId)
    {
        lock (_gate)
        {
            return _conversations.TryGetValue(peerId, out var list)
                ? list.Where(m => m.ReadReceiptPending).Select(m => m.MessageId).ToList()
                : [];
        }
    }

    public void ClearPendingReadReceipts(Guid peerId, IReadOnlyCollection<Guid> messageIds)
    {
        ArgumentNullException.ThrowIfNull(messageIds);
        lock (_gate)
        {
            if (!_conversations.TryGetValue(peerId, out var list))
            {
                return;
            }

            for (var i = 0; i < list.Count; i++)
            {
                if (list[i].ReadReceiptPending && messageIds.Contains(list[i].MessageId))
                {
                    list[i] = list[i] with { ReadReceiptPending = false };
                }
            }
        }
    }

    public IReadOnlyList<ConversationSummary> GetConversations()
    {
        lock (_gate)
        {
            return _conversations
                .Select(pair => new ConversationSummary(
                    pair.Key,
                    pair.Value.Count(IsUnread),
                    pair.Value.Any(m => IsUndelivered(m) || m.ReadReceiptPending)))
                .ToList();
        }
    }

    private static bool IsUndelivered(StoredMessage message) =>
        message.Direction == MessageDirection.Outgoing && message.State is DeliveryState.Pending or DeliveryState.Sent;

    private static bool IsUnread(StoredMessage message) =>
        message.Direction == MessageDirection.Incoming && message.State != DeliveryState.Read;

    private static int Rank(DeliveryState state) => state switch
    {
        DeliveryState.Pending => 0,
        DeliveryState.Sent => 1,
        DeliveryState.Failed => 2,
        DeliveryState.Delivered => 3,
        DeliveryState.Read => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    private int FindIndex(Guid peerId, Guid messageId, MessageDirection direction) =>
        _conversations.TryGetValue(peerId, out var list)
            ? list.FindIndex(m => m.MessageId == messageId && m.Direction == direction)
            : -1;

    private void Insert(StoredMessage message)
    {
        if (!_conversations.TryGetValue(message.PeerId, out var list))
        {
            list = [];
            _conversations.Add(message.PeerId, list);
        }

        // Equal timestamps keep their arrival order. The wire carries only milliseconds, and message IDs
        // are random within a millisecond, so neither can order a quick burst; the connection already has.
        var index = list.Count;
        while (index > 0 && list[index - 1].Timestamp > message.Timestamp)
        {
            index--;
        }

        list.Insert(index, message);

        if (list.Count > maxPerConversation)
        {
            var oldest = list.FindIndex(m => !IsUndelivered(m) && !m.ReadReceiptPending && !IsUnread(m));
            list.RemoveAt(oldest >= 0 ? oldest : 0);
        }
    }
}

/// <summary>The conversation ID of a one-to-one chat, which both devices work out independently.</summary>
public static class DirectConversation
{
    public static string IdFor(Guid first, Guid second)
    {
        var a = first.ToString("N");
        var b = second.ToString("N");
        return string.CompareOrdinal(a, b) <= 0 ? a + b : b + a;
    }
}
