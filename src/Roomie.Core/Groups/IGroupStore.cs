namespace Roomie.Core.Groups;

/// <param name="SignedDocument">The latest membership this device may see.</param>
/// <param name="Version">The highest version known, which passes <see cref="State"/>'s after a removal.</param>
/// <param name="IsMember">False once this device was removed, left, or the group was deleted. Its history stays, but it can't send.</param>
/// <param name="Deletion">The owner's signed deletion, kept to pass on to members who haven't heard. Null while the group exists.</param>
/// <param name="HasLeft">This device left. Until the owner confirms, it keeps telling the owner.</param>
/// <param name="IsHidden">The user removed it from this computer. What's kept is only what others still need to be told.</param>
public sealed record StoredGroup(
    SignedGroupDocument SignedDocument,
    GroupState State,
    int Version,
    bool IsMember,
    SignedGroupDocument? Deletion = null,
    bool HasLeft = false,
    bool IsHidden = false)
{
    public bool IsDeleted => Deletion is not null;
}

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

    /// <param name="includeHidden">Also those the user removed from this computer. Only for telling others.</param>
    IReadOnlyList<StoredGroup> GetGroups(bool includeHidden = false);

    /// <summary>
    /// Saves a group, and in the same transaction gives up on undelivered copies of its messages to devices
    /// no longer in it, and on all of them if this device isn't.
    /// </summary>
    void SaveGroup(StoredGroup group);

    /// <summary>Removes a group's messages from this computer and takes it off the list, in one transaction.</summary>
    void HideGroup(string groupId);

    StoredInvite? GetInvite(string groupId, Guid peerId, InviteDirection direction);

    IReadOnlyList<StoredInvite> GetInvites();

    void SaveInvite(StoredInvite invite);

    void DeleteInvite(string groupId, Guid peerId, InviteDirection direction);

    IReadOnlyList<StoredRemovalNotice> GetRemovalNotices(Guid peerId);

    StoredRemovalNotice? GetRemovalNotice(string groupId, Guid peerId);

    void SaveRemovalNotice(StoredRemovalNotice notice);

    void DeleteRemovalNotice(string groupId, Guid peerId);
}
