namespace Roomie.Core.Groups;

/// <param name="SignedDocument">The latest membership this device may see.</param>
/// <param name="Version">The highest version known, which passes <see cref="State"/>'s after a removal.</param>
/// <param name="IsMember">False once this device was removed. Its history stays, but it can't send.</param>
public sealed record StoredGroup(SignedGroupDocument SignedDocument, GroupState State, int Version, bool IsMember);

public enum InviteDirection
{
    Outgoing,
    Incoming,
}

public enum InviteResponse
{
    None,
    Accepted,
    Declined,
}

/// <param name="PeerId">The invitee of an outgoing invitation; the owner who sent an incoming one.</param>
/// <param name="SignedDocument">The membership sent with an incoming invitation. Null for an outgoing one.</param>
public sealed record StoredInvite(
    string GroupId,
    Guid PeerId,
    InviteDirection Direction,
    Guid InviteId,
    SignedGroupDocument? SignedDocument,
    InviteResponse Response);

/// <summary>A signed removal the owner keeps sending to the removed device, which may be offline.</summary>
public sealed record StoredRemovalNotice(string GroupId, Guid PeerId, SignedGroupDocument SignedDocument);

/// <summary>Local group membership. Methods throw <see cref="Messaging.MessageStoreException"/> if the store fails.</summary>
public interface IGroupStore
{
    StoredGroup? GetGroup(string groupId);

    IReadOnlyList<StoredGroup> GetGroups();

    /// <summary>
    /// Saves a group, and in the same transaction gives up on undelivered copies of its messages to devices
    /// no longer in it, and on all of them if this device isn't.
    /// </summary>
    void SaveGroup(StoredGroup group);

    StoredInvite? GetInvite(string groupId, Guid peerId, InviteDirection direction);

    IReadOnlyList<StoredInvite> GetInvites();

    void SaveInvite(StoredInvite invite);

    void DeleteInvite(string groupId, Guid peerId, InviteDirection direction);

    IReadOnlyList<StoredRemovalNotice> GetRemovalNotices(Guid peerId);

    StoredRemovalNotice? GetRemovalNotice(string groupId, Guid peerId);

    void SaveRemovalNotice(StoredRemovalNotice notice);

    void DeleteRemovalNotice(string groupId, Guid peerId);
}
