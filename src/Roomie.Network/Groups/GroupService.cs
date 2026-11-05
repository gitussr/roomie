using Microsoft.Extensions.Logging;
using Roomie.Core.Groups;
using Roomie.Core.Identity;
using Roomie.Core.Messaging;
using Roomie.Core.Protocol;
using Roomie.Core.Trust;
using Roomie.Network.Connections;

namespace Roomie.Network.Groups;

/// <summary>Null <see cref="Error"/> means it worked.</summary>
public sealed record GroupActionResult(string? GroupId, string? Error);

/// <summary>
/// Group membership (architecture.md §7). The owner alone changes membership, by signing a new numbered state.
/// Delivery works by reconciliation: on each session, and after each change, the worker in MessagingService
/// calls <see cref="FlushAsync"/> to send a device whatever it may have missed, so nothing here needs its own
/// retries or acknowledgements, and repeats are harmless.
/// </summary>
public sealed partial class GroupService : IDisposable
{
    private const int MaxPendingInvitesPerDevice = 20;

    private static readonly TimeSpan StateRequestInterval = TimeSpan.FromSeconds(5);

    private readonly ConnectionManager _connections;
    private readonly IGroupStore _store;
    private readonly ITrustStore _trust;
    private readonly ILocalIdentityProvider _identity;
    private readonly IDeviceSigner _signer;
    private readonly TimeProvider _time;
    private readonly ILogger<GroupService> _logger;
    private readonly IDisposable _registration;

    private readonly Lock _gate = new();

    // Held while the owner works out the next version, so two changes can't both claim it.
    private readonly Lock _ownerGate = new();
    private readonly Dictionary<Guid, SentThisSession> _sent = [];
    private readonly Dictionary<(string Group, Guid Peer), DateTimeOffset> _lastStateTraffic = [];

    public GroupService(
        ConnectionManager connections,
        IGroupStore store,
        ITrustStore trust,
        ILocalIdentityProvider identity,
        IDeviceSigner signer,
        TimeProvider time,
        ILogger<GroupService> logger)
    {
        _connections = connections;
        _store = store;
        _trust = trust;
        _identity = identity;
        _signer = signer;
        _time = time;
        _logger = logger;

        _registration = connections.Dispatcher.Register(
            [RmpMessageTypes.GroupInvite, RmpMessageTypes.GroupAccept, RmpMessageTypes.GroupRemove, RmpMessageTypes.GroupState, RmpMessageTypes.GroupStateRequest, RmpMessageTypes.GroupDelete, RmpMessageTypes.GroupLeave],
            HandleMessageAsync);
        _trust.Changed += OnTrustChanged;
    }

    /// <summary>Raised on any thread when groups or invitations change.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when a device has something to catch up on. The argument is the device.</summary>
    public event EventHandler<Guid>? WorkQueued;

    /// <summary>Raised when another device invites this one.</summary>
    public event EventHandler? IncomingInvite;

    private Guid Self => _identity.Identity.DeviceId;

    public GroupActionResult CreateGroup(string name)
    {
        if (GroupProtocol.NormalizeName(name) is not { } normalized)
        {
            return new GroupActionResult(null, $"Enter a group name of up to {GroupProtocol.MaxNameLength} characters.");
        }

        var identity = _identity.Identity;
        var state = new GroupState(
            Guid.CreateVersion7(_time.GetUtcNow()).ToString("N"),
            normalized,
            identity.DeviceId,
            identity.PublicKey,
            1,
            Guid.CreateVersion7(_time.GetUtcNow()),
            [new GroupMember(identity.DeviceId, identity.Username)]);

        return Attempt(state.GroupId, "create the group", () =>
        {
            _store.SaveGroup(new StoredGroup(GroupProtocol.Sign(state, _signer), state, state.MembershipVersion, true));
            RaiseChanged();
        });
    }

    /// <summary>Invites a paired device. It joins only if its user accepts.</summary>
    public GroupActionResult Invite(string groupId, Guid peerId)
    {
        return Attempt(groupId, "invite this device", () =>
        {
            var group = RequireOwnedGroup(groupId);
            if (_trust.FindTrusted(peerId) is null || _trust.IsBlocked(peerId))
            {
                throw new GroupActionException("Pair with this device before inviting it.");
            }

            if (group.State.HasMember(peerId))
            {
                throw new GroupActionException("This device is already in the group.");
            }

            if (group.State.Members.Count >= GroupProtocol.MaxMembers)
            {
                throw new GroupActionException($"A group can have at most {GroupProtocol.MaxMembers} members.");
            }

            _store.SaveInvite(new StoredInvite(groupId, peerId, InviteDirection.Outgoing, Guid.CreateVersion7(_time.GetUtcNow()), null, InviteResponse.None));
            RaiseChanged();
            RaiseWork(peerId);
        });
    }

    /// <summary>Removes a member, or withdraws an invitation it hasn't answered.</summary>
    public GroupActionResult Remove(string groupId, Guid peerId)
    {
        return Attempt(groupId, "remove this device", () =>
        {
            lock (_ownerGate)
            {
                var group = RequireOwnedGroup(groupId);
                if (peerId == Self)
                {
                    throw new GroupActionException("The owner can't be removed from the group.");
                }

                RemoveDevice(group, peerId);
            }
        });
    }

    /// <summary>Ends the group for everyone. Each device keeps its history until its user removes it.</summary>
    public GroupActionResult Delete(string groupId)
    {
        return Attempt(groupId, "delete this group", () =>
        {
            lock (_ownerGate)
            {
                var group = RequireOwnedGroup(groupId);
                var version = group.Version + 1;
                var deletion = new GroupDeletion(groupId, Self, version, Guid.CreateVersion7(_time.GetUtcNow()));

                // Unanswered invitations stay, so those devices are told too.
                _store.SaveGroup(group with { Version = version, IsMember = false, Deletion = GroupProtocol.Sign(deletion, _signer) });
                RaiseChanged();
                RaiseWork(group.State.Members.Select(m => m.DeviceId));
                RaiseWork(_store.GetInvites().Where(i => i.GroupId == groupId && i.Direction == InviteDirection.Outgoing).Select(i => i.PeerId));
            }
        });
    }

    /// <summary>Leaves a group this device doesn't own. The owner is told, now or when it's next connected.</summary>
    public GroupActionResult Leave(string groupId)
    {
        return Attempt(groupId, "leave this group", () =>
        {
            if (_store.GetGroup(groupId) is not { IsMember: true } group)
            {
                throw new GroupActionException("You're no longer in this group.");
            }

            if (group.State.OwnerId == Self)
            {
                throw new GroupActionException("The owner can't leave a group, only delete it.");
            }

            _store.SaveGroup(group with { IsMember = false, HasLeft = true });
            RaiseChanged();
            RaiseWork(group.State.OwnerId);
        });
    }

    /// <summary>Removes a group that has ended for this device, and its messages, from this computer.</summary>
    public GroupActionResult RemoveFromThisComputer(string groupId)
    {
        return Attempt(groupId, "remove this group", () =>
        {
            if (_store.GetGroup(groupId) is not { } group)
            {
                return;
            }

            if (group.IsMember)
            {
                throw new GroupActionException("Leave or delete this group first.");
            }

            _store.HideGroup(groupId);
            RaiseChanged();
        });
    }

    /// <summary>
    /// Puts current names into the groups this device owns, so members who aren't paired with a renamed device
    /// see the new name too. Call after this device's own name changes. A paired device's change arrives by itself.
    /// </summary>
    public void RefreshOwnedNames()
    {
        try
        {
            lock (_ownerGate)
            {
                foreach (var group in _store.GetGroups().Where(g => g.IsMember && g.State.OwnerId == Self))
                {
                    var members = group.State.Members.Select(m => m with { Username = CurrentName(m) }).ToList();
                    if (members.SequenceEqual(group.State.Members))
                    {
                        continue;
                    }

                    var state = group.State with { MembershipVersion = group.Version + 1, EventId = Guid.CreateVersion7(_time.GetUtcNow()), Members = members };
                    _store.SaveGroup(new StoredGroup(GroupProtocol.Sign(state, _signer), state, state.MembershipVersion, true));
                    RaiseChanged();
                    RaiseWork(members.Select(m => m.DeviceId));
                }
            }
        }
        catch (MessageStoreException ex)
        {
            // The next change of any name tries again, and the names shown locally are already current.
            LogNamesNotSaved(_logger, ex.Message);
        }
    }

    public GroupActionResult RespondToInvite(string groupId, Guid ownerId, bool accept)
    {
        return Attempt(groupId, "answer this invitation", () =>
        {
            if (_store.GetInvite(groupId, ownerId, InviteDirection.Incoming) is not { Response: InviteResponse.None } invite)
            {
                throw new GroupActionException("This invitation is no longer open.");
            }

            // Kept even after declining, so the owner's repeats of this invitation don't ask again.
            _store.SaveInvite(invite with { Response = accept ? InviteResponse.Accepted : InviteResponse.Declined });
            RaiseChanged();
            RaiseWork(ownerId);
        });
    }

    /// <summary>
    /// Applies the rules of architecture.md §7 to a group message, before it is stored.
    /// </summary>
    /// <exception cref="RmpProtocolException">The message must not be stored.</exception>
    internal async ValueTask CheckIncomingMessageAsync(PeerSession session, string groupId, int version, CancellationToken cancellationToken)
    {
        var sender = session.RemoteDeviceId;
        var group = _store.GetGroup(groupId);

        if (group is not { IsMember: true })
        {
            // Accepted but not yet told about the membership: another member got there before the owner did.
            if (_store.GetInvites().Any(i => i.GroupId == groupId && i is { Direction: InviteDirection.Incoming, Response: InviteResponse.Accepted }))
            {
                throw new RmpProtocolException(RmpErrorCodes.MembershipUpdating, "This device is still joining the group.");
            }

            throw new RmpProtocolException(RmpErrorCodes.NotAMember, "This device isn't in that group.");
        }

        var senderIsMember = group.State.HasMember(sender);
        if (version > group.Version)
        {
            await RequestStateAsync(session, group, cancellationToken).ConfigureAwait(false);
            if (!senderIsMember)
            {
                // Stored as "membership updating", this would let a removed member keep posting just by
                // claiming a higher version. It is sent again and accepted once the signed state proves it.
                throw new RmpProtocolException(RmpErrorCodes.MembershipUpdating, "This device hasn't seen that membership yet.");
            }

            return;
        }

        if (!senderIsMember)
        {
            throw new RmpProtocolException(RmpErrorCodes.NotAMember, "The sender isn't in that group.");
        }

        if (version < group.Version && ShouldSendState(groupId, sender))
        {
            await session.SendAsync(RmpMessageTypes.GroupState, SessionPayloads.ToElement(group.SignedDocument), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Sends a device whatever about groups it may have missed. Only runs on the messaging worker.</summary>
    internal async Task FlushAsync(PeerSession session, CancellationToken cancellationToken)
    {
        if (!session.Supports(RmpCapabilities.Groups))
        {
            return;
        }

        var peer = session.RemoteDeviceId;
        var groups = _store.GetGroups(includeHidden: true).ToDictionary(g => g.State.GroupId);
        var canEnd = session.Supports(RmpCapabilities.GroupEnd);

        foreach (var notice in _store.GetRemovalNotices(peer))
        {
            await SendOnceAsync(session, $"remove:{notice.GroupId}:{notice.SignedDocument.Signature}", RmpMessageTypes.GroupRemove, notice.SignedDocument, cancellationToken).ConfigureAwait(false);
        }

        foreach (var invite in _store.GetInvites().Where(i => i.PeerId == peer))
        {
            if (invite.Direction == InviteDirection.Outgoing && groups.TryGetValue(invite.GroupId, out var ended) && ended.Deletion is { } withdrawn)
            {
                if (canEnd)
                {
                    await SendOnceAsync(session, $"delete:{invite.GroupId}", RmpMessageTypes.GroupDelete, withdrawn, cancellationToken).ConfigureAwait(false);
                }
            }
            else if (invite.Direction == InviteDirection.Outgoing && invite.Response == InviteResponse.None && groups.TryGetValue(invite.GroupId, out var owned))
            {
                var payload = new GroupInvitePayload(invite.InviteId, owned.SignedDocument.Document, owned.SignedDocument.Signature);
                await SendOnceAsync(session, $"invite:{invite.InviteId}:{owned.Version}", RmpMessageTypes.GroupInvite, payload, cancellationToken).ConfigureAwait(false);
            }
            else if (invite.Direction == InviteDirection.Incoming && invite.Response != InviteResponse.None)
            {
                var answer = new GroupAcceptPayload(invite.GroupId, invite.InviteId, invite.Response == InviteResponse.Accepted);
                await SendOnceAsync(session, $"answer:{invite.InviteId}", RmpMessageTypes.GroupAccept, answer, cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var group in groups.Values.Where(g => g.IsMember && g.State.HasMember(peer)))
        {
            await SendOnceAsync(session, $"state:{group.State.GroupId}:{group.Version}", RmpMessageTypes.GroupState, group.SignedDocument, cancellationToken).ConfigureAwait(false);
        }

        // A device on an older version hears about these once it updates, because nothing here is thrown away.
        if (!canEnd)
        {
            return;
        }

        foreach (var group in groups.Values.Where(g => g.State.HasMember(peer)))
        {
            if (group.Deletion is { } deletion)
            {
                await SendOnceAsync(session, $"delete:{group.State.GroupId}", RmpMessageTypes.GroupDelete, deletion, cancellationToken).ConfigureAwait(false);
            }
            else if (group.HasLeft && group.State.OwnerId == peer && group.Version == group.State.MembershipVersion)
            {
                // A higher version means the owner's removal arrived, so it knows.
                await SendOnceAsync(session, $"leave:{group.State.GroupId}:{group.Version}", RmpMessageTypes.GroupLeave, new GroupLeavePayload(group.State.GroupId), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public IReadOnlyList<StoredGroup> GetGroups() => _store.GetGroups();

    public StoredGroup? GetGroup(string groupId) => _store.GetGroup(groupId);

    public IReadOnlyList<StoredInvite> GetInvites() => _store.GetInvites();

    public void Dispose()
    {
        _trust.Changed -= OnTrustChanged;
        _registration.Dispose();
    }

    private async ValueTask HandleMessageAsync(PeerSession session, RmpEnvelope message, CancellationToken cancellationToken)
    {
        switch (message.Type)
        {
            case RmpMessageTypes.GroupInvite:
                OnInvite(session, SessionPayloads.TryDeserialize<GroupInvitePayload>(message.Payload)!);
                break;

            case RmpMessageTypes.GroupAccept:
                OnAnswer(session, SessionPayloads.TryDeserialize<GroupAcceptPayload>(message.Payload)!);
                break;

            case RmpMessageTypes.GroupRemove:
                OnRemoval(session, SessionPayloads.TryDeserialize<SignedGroupDocument>(message.Payload)!);
                break;

            case RmpMessageTypes.GroupState:
                OnState(session, SessionPayloads.TryDeserialize<SignedGroupDocument>(message.Payload)!);
                break;

            case RmpMessageTypes.GroupDelete:
                OnDeletion(session, SessionPayloads.TryDeserialize<SignedGroupDocument>(message.Payload)!);
                break;

            case RmpMessageTypes.GroupLeave:
                OnLeave(session, SessionPayloads.TryDeserialize<GroupLeavePayload>(message.Payload)!);
                break;

            case RmpMessageTypes.GroupStateRequest:
                await OnStateRequestAsync(session, SessionPayloads.TryDeserialize<GroupStateRequestPayload>(message.Payload)!, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private void OnInvite(PeerSession session, GroupInvitePayload payload)
    {
        var signedDocument = new SignedGroupDocument(payload.Document, payload.Signature);
        var sender = session.RemoteDeviceId;
        if (!GroupProtocol.TryReadState(signedDocument, out var state)
            || state.OwnerId != sender
            || !GroupProtocol.OwnerKeyMatches(state, session.RemoteFingerprint)
            || !GroupProtocol.IsStateSignedBy(signedDocument, state.OwnerPublicKey))
        {
            throw new RmpProtocolException(RmpErrorCodes.InvalidFrame, "That invitation isn't signed by its group's owner.");
        }

        var known = _store.GetGroup(state.GroupId);
        if (known is not null && known.State.OwnerId != sender)
        {
            throw new RmpProtocolException(RmpErrorCodes.InvalidFrame, "That group belongs to another device.");
        }

        if (known is { IsMember: true } or { IsDeleted: true })
        {
            return;
        }

        // The same invitation sent again changes nothing, so an answer already given isn't asked for twice.
        var existing = _store.GetInvite(state.GroupId, sender, InviteDirection.Incoming);
        if (existing?.InviteId == payload.InviteId)
        {
            if (existing.Response == InviteResponse.None)
            {
                _store.SaveInvite(existing with { SignedDocument = signedDocument });
            }

            return;
        }

        var open = _store.GetInvites().Count(i => i.PeerId == sender && i is { Direction: InviteDirection.Incoming, Response: InviteResponse.None });
        if (existing is null && open >= MaxPendingInvitesPerDevice)
        {
            throw new RmpProtocolException(RmpErrorCodes.RateLimited, "This device has too many open invitations from you.");
        }

        _store.SaveInvite(new StoredInvite(state.GroupId, sender, InviteDirection.Incoming, payload.InviteId, signedDocument, InviteResponse.None));
        RaiseChanged();
        IncomingInvite?.Invoke(this, EventArgs.Empty);
    }

    private void OnAnswer(PeerSession session, GroupAcceptPayload answer)
    {
        var sender = session.RemoteDeviceId;
        lock (_ownerGate)
        {
            // A deleted group keeps its invitation, because that device still has to be told.
            if (_store.GetInvite(answer.GroupId, sender, InviteDirection.Outgoing) is not { } invite
                || invite.InviteId != answer.InviteId
                || _store.GetGroup(answer.GroupId) is not { IsDeleted: false } group
                || group.State.OwnerId != Self)
            {
                return;
            }

            if (answer.Accepted && !group.State.HasMember(sender) && group.State.Members.Count < GroupProtocol.MaxMembers)
            {
                var username = _trust.FindTrusted(sender)?.Username ?? DeviceIdFormat.ToDisplayId(sender);
                var state = group.State with
                {
                    MembershipVersion = group.Version + 1,
                    EventId = Guid.CreateVersion7(_time.GetUtcNow()),
                    Members = [.. group.State.Members, new GroupMember(sender, username)],
                };
                _store.SaveGroup(new StoredGroup(GroupProtocol.Sign(state, _signer), state, state.MembershipVersion, true));
                _store.DeleteRemovalNotice(answer.GroupId, sender);
                RaiseWork(state.Members.Select(m => m.DeviceId));
            }

            _store.DeleteInvite(answer.GroupId, sender, InviteDirection.Outgoing);
            RaiseChanged();
        }
    }

    private void OnState(PeerSession session, SignedGroupDocument signedDocument)
    {
        // Any device may relay a state, so nothing below depends on who sent it.
        if (!GroupProtocol.TryReadState(signedDocument, out var state))
        {
            return;
        }

        var known = _store.GetGroup(state.GroupId);
        string ownerKey;
        if (known is not null)
        {
            if (known.IsDeleted || state.OwnerId != known.State.OwnerId || state.MembershipVersion <= known.Version)
            {
                return;
            }

            ownerKey = known.State.OwnerPublicKey;
        }
        else
        {
            ownerKey = state.OwnerPublicKey;
        }

        var invite = _store.GetInvite(state.GroupId, state.OwnerId, InviteDirection.Incoming);
        var joining = invite is { Response: InviteResponse.Accepted, SignedDocument: not null };
        if (known is not { IsMember: true })
        {
            // Not in the group: only an invitation this user accepted can bring this device in, and the key
            // must be the one that invitation was verified against.
            if (!joining || !GroupProtocol.TryReadState(invite!.SignedDocument!, out var invited) || invited.OwnerPublicKey != state.OwnerPublicKey || !state.HasMember(Self))
            {
                return;
            }
        }

        if (!GroupProtocol.IsStateSignedBy(signedDocument, ownerKey))
        {
            LogBadSignature(_logger, session.RemoteDeviceId, state.GroupId);
            return;
        }

        _store.SaveGroup(new StoredGroup(signedDocument, state, state.MembershipVersion, state.HasMember(Self)));
        if (state.HasMember(Self) && joining)
        {
            _store.DeleteInvite(state.GroupId, state.OwnerId, InviteDirection.Incoming);
        }

        RaiseChanged();
        RaiseWork(state.Members.Select(m => m.DeviceId));
    }

    private void OnRemoval(PeerSession session, SignedGroupDocument signedDocument)
    {
        if (!GroupProtocol.TryReadRemoval(signedDocument, out var removal)
            || removal.RemovedDeviceId != Self
            || _store.GetGroup(removal.GroupId) is not { } group
            || removal.OwnerId != group.State.OwnerId
            || removal.MembershipVersion <= group.Version)
        {
            return;
        }

        if (!GroupProtocol.IsRemovalSignedBy(signedDocument, group.State.OwnerPublicKey))
        {
            LogBadSignature(_logger, session.RemoteDeviceId, removal.GroupId);
            return;
        }

        _store.SaveGroup(group with { Version = removal.MembershipVersion, IsMember = false });
        RaiseChanged();
    }

    /// <summary>
    /// Any device may relay a deletion, so only the signature counts: the key saved with the group, or for a device
    /// that was only invited, the one its invitation was checked against.
    /// </summary>
    private void OnDeletion(PeerSession session, SignedGroupDocument signedDocument)
    {
        if (!GroupProtocol.TryReadDeletion(signedDocument, out var deletion))
        {
            return;
        }

        var group = _store.GetGroup(deletion.GroupId);
        var invite = _store.GetInvite(deletion.GroupId, deletion.OwnerId, InviteDirection.Incoming);
        string? ownerKey = null;
        if (group is not null)
        {
            ownerKey = group.State.OwnerId == deletion.OwnerId ? group.State.OwnerPublicKey : null;
        }
        else if (invite?.SignedDocument is { } invited && GroupProtocol.TryReadState(invited, out var state) && state.OwnerId == deletion.OwnerId)
        {
            ownerKey = state.OwnerPublicKey;
        }

        if (ownerKey is null)
        {
            return;
        }

        if (!GroupProtocol.IsDeletionSignedBy(signedDocument, ownerKey))
        {
            LogBadSignature(_logger, session.RemoteDeviceId, deletion.GroupId);
            return;
        }

        if (invite is not null)
        {
            _store.DeleteInvite(deletion.GroupId, deletion.OwnerId, InviteDirection.Incoming);
        }

        if (group is { IsDeleted: false })
        {
            _store.SaveGroup(group with { Version = Math.Max(group.Version, deletion.MembershipVersion), IsMember = false, Deletion = signedDocument });
            RaiseWork(group.State.Members.Select(m => m.DeviceId));
        }

        RaiseChanged();
    }

    /// <summary>The session proves who is asking, and a device can only ever take itself out.</summary>
    private void OnLeave(PeerSession session, GroupLeavePayload request)
    {
        var sender = session.RemoteDeviceId;
        lock (_ownerGate)
        {
            if (sender != Self
                && _store.GetGroup(request.GroupId) is { IsMember: true, IsDeleted: false } group
                && group.State.OwnerId == Self
                && group.State.HasMember(sender))
            {
                RemoveDevice(group, sender);
            }
        }
    }

    /// <summary>Takes a device out of a group this device owns, or withdraws its invitation. Call holding the owner gate.</summary>
    private void RemoveDevice(StoredGroup group, Guid peerId)
    {
        var groupId = group.State.GroupId;
        _store.DeleteInvite(groupId, peerId, InviteDirection.Outgoing);
        if (group.State.HasMember(peerId))
        {
            var version = group.Version + 1;
            var eventId = Guid.CreateVersion7(_time.GetUtcNow());
            var state = group.State with
            {
                MembershipVersion = version,
                EventId = eventId,
                Members = group.State.Members.Where(m => m.DeviceId != peerId).ToList(),
            };
            var removal = new GroupRemoval(groupId, Self, peerId, version, eventId);

            // The notice first: if Roomie stops between the two, sending it again is harmless.
            _store.SaveRemovalNotice(new StoredRemovalNotice(groupId, peerId, GroupProtocol.Sign(removal, _signer)));
            _store.SaveGroup(new StoredGroup(GroupProtocol.Sign(state, _signer), state, version, true));
            RaiseWork(state.Members.Select(m => m.DeviceId));
        }

        RaiseChanged();
        RaiseWork(peerId);
    }

    private void OnTrustChanged(object? sender, EventArgs e) => RefreshOwnedNames();

    private string CurrentName(GroupMember member) =>
        member.DeviceId == Self ? _identity.Identity.Username : _trust.FindTrusted(member.DeviceId)?.Username ?? member.Username;

    private async ValueTask OnStateRequestAsync(PeerSession session, GroupStateRequestPayload request, CancellationToken cancellationToken)
    {
        var peer = session.RemoteDeviceId;
        var known = _store.GetGroup(request.GroupId);
        if (known is { Deletion: { } deletion } && known.State.HasMember(peer) && session.Supports(RmpCapabilities.GroupEnd))
        {
            await session.SendAsync(RmpMessageTypes.GroupDelete, SessionPayloads.ToElement(deletion), cancellationToken).ConfigureAwait(false);
        }
        else if (known is { IsMember: true } group && group.State.HasMember(peer))
        {
            await session.SendAsync(RmpMessageTypes.GroupState, SessionPayloads.ToElement(group.SignedDocument), cancellationToken).ConfigureAwait(false);
        }
        else if (_store.GetRemovalNotice(request.GroupId, peer) is { } notice)
        {
            await session.SendAsync(RmpMessageTypes.GroupRemove, SessionPayloads.ToElement(notice.SignedDocument), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // The same answer whether or not the group exists, and never the member list.
            throw new RmpProtocolException(RmpErrorCodes.NotAMember, "That device isn't in that group.");
        }
    }

    /// <summary>Asks the owner if it's connected, otherwise the device that showed the newer version exists.</summary>
    private async ValueTask RequestStateAsync(PeerSession sender, StoredGroup group, CancellationToken cancellationToken)
    {
        if (!ShouldSendState(group.State.GroupId, Guid.Empty))
        {
            return;
        }

        var target = _connections.TryGetSession(group.State.OwnerId, out var owner) && owner.IsTrusted && owner.Supports(RmpCapabilities.Groups) ? owner : sender;
        var request = SessionPayloads.ToElement(new GroupStateRequestPayload(group.State.GroupId));
        await target.SendAsync(RmpMessageTypes.GroupStateRequest, request, cancellationToken).ConfigureAwait(false);
    }

    private bool ShouldSendState(string groupId, Guid peer)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_lastStateTraffic.TryGetValue((groupId, peer), out var last) && now - last < StateRequestInterval)
            {
                return false;
            }

            _lastStateTraffic[(groupId, peer)] = now;
            return true;
        }
    }

    private async Task SendOnceAsync<T>(PeerSession session, string key, string type, T payload, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var sent = _sent.GetValueOrDefault(session.RemoteDeviceId);
            if (sent?.SessionId == session.SessionId && sent.Keys.Contains(key))
            {
                return;
            }
        }

        await session.SendAsync(type, SessionPayloads.ToElement(payload), cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            if (!_sent.TryGetValue(session.RemoteDeviceId, out var sent) || sent.SessionId != session.SessionId)
            {
                sent = new SentThisSession(session.SessionId, []);
                _sent[session.RemoteDeviceId] = sent;
            }

            sent.Keys.Add(key);
        }
    }

    private StoredGroup RequireOwnedGroup(string groupId)
    {
        if (_store.GetGroup(groupId) is not { } group || group.State.OwnerId != Self)
        {
            throw new GroupActionException("Only the group's owner can change who is in it.");
        }

        if (group.IsDeleted)
        {
            throw new GroupActionException("This group was deleted.");
        }

        return group;
    }

    private static GroupActionResult Attempt(string groupId, string action, Action change)
    {
        try
        {
            change();
            return new GroupActionResult(groupId, null);
        }
        catch (GroupActionException ex)
        {
            return new GroupActionResult(groupId, ex.Message);
        }
        catch (MessageStoreException)
        {
            return new GroupActionResult(groupId, $"Roomie couldn't {action}, because it couldn't save the change.");
        }
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private void RaiseWork(Guid peer) => WorkQueued?.Invoke(this, peer);

    private void RaiseWork(IEnumerable<Guid> peers)
    {
        foreach (var peer in peers.Where(p => p != Self))
        {
            RaiseWork(peer);
        }
    }

    private sealed record SentThisSession(Guid SessionId, HashSet<string> Keys);

    private sealed class GroupActionException(string message) : Exception(message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't save new names in a group: {Reason}")]
    private static partial void LogNamesNotSaved(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{DeviceId} sent a document for group {GroupId} that its owner didn't sign")]
    private static partial void LogBadSignature(ILogger logger, Guid deviceId, string groupId);
}
