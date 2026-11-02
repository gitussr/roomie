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

/// <param name="Timestamp">Creation time on the sender, kept to the millisecond. For display order only, never for security decisions.</param>
/// <param name="Failure">Why the message wasn't delivered, when <see cref="State"/> is Failed.</param>
/// <param name="ReadReceiptPending">An incoming message was read, and the sender hasn't been told yet.</param>
/// <param name="GroupId">The group it belongs to, or null in a one-to-one conversation.</param>
/// <param name="MembershipVersion">The group membership it was sent under.</param>
/// <param name="Recipients">How far each member got, on an outgoing group message read from a group conversation.</param>
public sealed record StoredMessage(
    Guid MessageId,
    Guid PeerId,
    MessageDirection Direction,
    string Content,
    DateTimeOffset Timestamp,
    DeliveryState State,
    string? Failure = null,
    bool ReadReceiptPending = false,
    string? GroupId = null,
    int? MembershipVersion = null,
    IReadOnlyList<MessageRecipient>? Recipients = null);

/// <summary>One member's copy of an outgoing group message, which is delivered independently (architecture.md §7).</summary>
public sealed record MessageRecipient(Guid DeviceId, DeliveryState State, string? Failure);

/// <summary>Unread counts one-to-one messages only. Waiting work includes group copies for this device.</summary>
public sealed record ConversationSummary(Guid PeerId, int UnreadCount, bool HasWaitingWork);

/// <summary>
/// Local message history. A message is identified by its ID and sender (architecture.md §6), so one a peer
/// sends with the ID of one of ours is a different message and can't affect ours.
/// </summary>
/// <remarks>Every method throws <see cref="MessageStoreException"/> if the store can't be read or written.</remarks>
public interface IMessageStore
{
    /// <summary>Saves a message to send. When this returns, it survives a restart or power loss.</summary>
    void AddOutgoing(StoredMessage message);

    /// <summary>Saves a received message. When this returns, the message must be safe to acknowledge.</summary>
    /// <returns>False if this message was already stored; the existing copy is kept unchanged.</returns>
    bool TryAddIncoming(StoredMessage message);

    /// <summary>Saves a group message to send, with one copy to deliver per recipient.</summary>
    void AddOutgoingGroup(StoredMessage message, IReadOnlyCollection<Guid> recipients);

    /// <summary>The latest one-to-one messages with a device, in display order.</summary>
    IReadOnlyList<StoredMessage> GetConversation(Guid peerId, int maxMessages = int.MaxValue);

    /// <summary>The latest messages in a group, in display order.</summary>
    IReadOnlyList<StoredMessage> GetGroupConversation(string groupId, int maxMessages = int.MaxValue);

    /// <summary>
    /// What still has to be delivered to a device, oldest first: one-to-one messages and group copies. For a
    /// group copy, <see cref="StoredMessage.PeerId"/> and <see cref="StoredMessage.State"/> are this device's.
    /// </summary>
    IReadOnlyList<StoredMessage> GetUndelivered(Guid peerId);

    /// <summary>
    /// Moves an outgoing message, or this device's copy of a group message, forward: Pending, Sent, Delivered,
    /// Read. It never moves back, so a late or repeated acknowledgement can't undo a read receipt. Failed
    /// applies only before delivery; a later acknowledgement overrides it, because the message did arrive.
    /// </summary>
    /// <returns>False if the message is unknown or the change would be a step backwards.</returns>
    bool Advance(Guid peerId, Guid messageId, DeliveryState state, string? failure = null);

    /// <summary>Marks every unread one-to-one message from a device as read and owing a read receipt.</summary>
    /// <returns>Whether anything changed.</returns>
    bool MarkIncomingRead(Guid peerId);

    /// <summary>Marks every unread message in a group as read and owing a read receipt.</summary>
    /// <returns>The senders now owed a receipt.</returns>
    IReadOnlyList<Guid> MarkGroupRead(string groupId);

    /// <summary>Unread messages per group, leaving out groups with none.</summary>
    IReadOnlyDictionary<string, int> GetGroupUnreadCounts();

    IReadOnlyList<Guid> GetPendingReadReceipts(Guid peerId);

    void ClearPendingReadReceipts(Guid peerId, IReadOnlyCollection<Guid> messageIds);

    /// <summary>The conversations with unread messages or work still to send. Finished ones aren't listed.</summary>
    IReadOnlyList<ConversationSummary> GetActiveConversations();
}

/// <summary>Message history couldn't be read or written. Nothing was changed by the failed call.</summary>
public sealed class MessageStoreException : Exception
{
    public MessageStoreException()
    {
    }

    public MessageStoreException(string message)
        : base(message)
    {
    }

    public MessageStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
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
