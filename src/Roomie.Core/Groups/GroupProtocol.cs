using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Roomie.Core.Identity;
using Roomie.Core.Pairing;
using Roomie.Core.Protocol;

namespace Roomie.Core.Groups;

public sealed record GroupMember(Guid DeviceId, string Username);

/// <summary>
/// A group's membership at one version (architecture.md §7). Always signed by the owner's device key, so any
/// member can relay it and none can forge it.
/// </summary>
public sealed record GroupState(
    string GroupId,
    string Name,
    Guid OwnerId,
    string OwnerPublicKey,
    int MembershipVersion,
    Guid EventId,
    IReadOnlyList<GroupMember> Members)
{
    public bool HasMember(Guid deviceId) => Members.Any(m => m.DeviceId == deviceId);
}

/// <summary>
/// Tells one device it was removed, instead of sending it the new membership, which it has no right to see.
/// </summary>
public sealed record GroupRemoval(string GroupId, Guid OwnerId, Guid RemovedDeviceId, int MembershipVersion, Guid EventId);

/// <summary>Ends a group for everyone. Members relay it, so it reaches them while the owner is offline.</summary>
public sealed record GroupDeletion(string GroupId, Guid OwnerId, int MembershipVersion, Guid EventId);

/// <summary>A <see cref="GroupState"/>, <see cref="GroupRemoval"/> or <see cref="GroupDeletion"/> as JSON text, with the owner's signature over exactly that text.</summary>
public sealed record SignedGroupDocument(string Document, string Signature);

public sealed record GroupInvitePayload(Guid InviteId, string Document, string Signature);

/// <summary><c>GROUP_ACCEPT</c>: the answer to an invitation, either way.</summary>
public sealed record GroupAcceptPayload(string GroupId, Guid InviteId, bool Accepted);

public sealed record GroupStateRequestPayload(string GroupId);

/// <summary><c>GROUP_LEAVE</c>: a member asking its owner to remove it. The session proves who is asking.</summary>
public sealed record GroupLeavePayload(string GroupId);

/// <summary>Creating, signing and strictly checking group documents.</summary>
public static class GroupProtocol
{
    public const int MaxMembers = 32;
    public const int MaxNameLength = 64;

    // The two kinds of document can't be mistaken for each other, or for a pairing signature.
    private const string StateDomain = "roomie-group-state-v1\0";
    private const string RemovalDomain = "roomie-group-removal-v1\0";
    private const string DeletionDomain = "roomie-group-deletion-v1\0";

    public static SignedGroupDocument Sign(GroupState state, IDeviceSigner signer) => Sign(state, StateDomain, signer);

    public static SignedGroupDocument Sign(GroupRemoval removal, IDeviceSigner signer) => Sign(removal, RemovalDomain, signer);

    public static SignedGroupDocument Sign(GroupDeletion deletion, IDeviceSigner signer) => Sign(deletion, DeletionDomain, signer);

    /// <summary>The name to use, or null if it isn't acceptable.</summary>
    public static string? NormalizeName(string? name)
    {
        var trimmed = name?.Trim();
        return !string.IsNullOrEmpty(trimmed) && trimmed.Length <= MaxNameLength && !trimmed.Any(char.IsControl) ? trimmed : null;
    }

    /// <summary>Reads a document's content. This proves nothing about who wrote it: see <see cref="IsSignedBy"/>.</summary>
    public static bool TryReadState(SignedGroupDocument signedDocument, [NotNullWhen(true)] out GroupState? state)
    {
        ArgumentNullException.ThrowIfNull(signedDocument);
        state = Parse<GroupState>(signedDocument.Document);
        if (state is null || !IsValid(state))
        {
            state = null;
            return false;
        }

        return true;
    }

    public static bool TryReadRemoval(SignedGroupDocument signedDocument, [NotNullWhen(true)] out GroupRemoval? removal)
    {
        ArgumentNullException.ThrowIfNull(signedDocument);
        removal = Parse<GroupRemoval>(signedDocument.Document);
        if (removal is null || !ContentPayloads.IsIdentifier(removal.GroupId) || removal.OwnerId == Guid.Empty
            || removal.RemovedDeviceId == Guid.Empty || removal.MembershipVersion < 1 || removal.EventId == Guid.Empty)
        {
            removal = null;
            return false;
        }

        return true;
    }

    public static bool TryReadDeletion(SignedGroupDocument signedDocument, [NotNullWhen(true)] out GroupDeletion? deletion)
    {
        ArgumentNullException.ThrowIfNull(signedDocument);
        deletion = Parse<GroupDeletion>(signedDocument.Document);
        if (deletion is null || !ContentPayloads.IsIdentifier(deletion.GroupId) || deletion.OwnerId == Guid.Empty
            || deletion.MembershipVersion < 1 || deletion.EventId == Guid.Empty)
        {
            deletion = null;
            return false;
        }

        return true;
    }

    /// <summary>Whether the owner holding this key signed this membership.</summary>
    public static bool IsStateSignedBy(SignedGroupDocument signedDocument, string ownerPublicKey) => IsSignedBy(signedDocument, StateDomain, ownerPublicKey);

    /// <summary>Whether the owner holding this key signed this removal.</summary>
    public static bool IsRemovalSignedBy(SignedGroupDocument signedDocument, string ownerPublicKey) => IsSignedBy(signedDocument, RemovalDomain, ownerPublicKey);

    /// <summary>Whether the owner holding this key deleted this group.</summary>
    public static bool IsDeletionSignedBy(SignedGroupDocument signedDocument, string ownerPublicKey) => IsSignedBy(signedDocument, DeletionDomain, ownerPublicKey);

    /// <summary>Whether the key a state names for its owner is the one pinned for that device when pairing.</summary>
    public static bool OwnerKeyMatches(GroupState state, string pinnedFingerprint)
    {
        ArgumentNullException.ThrowIfNull(state);
        var key = Decode(state.OwnerPublicKey);
        return key is { Length: > 0 } && CertificateFingerprint.AreEqual(CertificateFingerprint.FromSubjectPublicKeyInfo(key), pinnedFingerprint);
    }

    public static RmpPayloadCheck CheckInvite(JsonElement payload) =>
        SessionPayloads.TryDeserialize<GroupInvitePayload>(payload) is { } invite && invite.InviteId != Guid.Empty
        && TryReadState(new SignedGroupDocument(invite.Document, invite.Signature), out _)
            ? RmpPayloadCheck.Valid
            : RmpPayloadCheck.Malformed;

    public static RmpPayloadCheck CheckState(JsonElement payload) =>
        SessionPayloads.TryDeserialize<SignedGroupDocument>(payload) is { } signedDocument && TryReadState(signedDocument, out _)
            ? RmpPayloadCheck.Valid
            : RmpPayloadCheck.Malformed;

    public static RmpPayloadCheck CheckRemoval(JsonElement payload) =>
        SessionPayloads.TryDeserialize<SignedGroupDocument>(payload) is { } signedDocument && TryReadRemoval(signedDocument, out _)
            ? RmpPayloadCheck.Valid
            : RmpPayloadCheck.Malformed;

    public static RmpPayloadCheck CheckDeletion(JsonElement payload) =>
        SessionPayloads.TryDeserialize<SignedGroupDocument>(payload) is { } signedDocument && TryReadDeletion(signedDocument, out _)
            ? RmpPayloadCheck.Valid
            : RmpPayloadCheck.Malformed;

    public static RmpPayloadCheck CheckLeave(JsonElement payload) =>
        SessionPayloads.TryDeserialize<GroupLeavePayload>(payload) is { } leave && ContentPayloads.IsIdentifier(leave.GroupId)
            ? RmpPayloadCheck.Valid
            : RmpPayloadCheck.Malformed;

    public static RmpPayloadCheck CheckAccept(JsonElement payload) =>
        SessionPayloads.TryDeserialize<GroupAcceptPayload>(payload) is { } accept
        && ContentPayloads.IsIdentifier(accept.GroupId) && accept.InviteId != Guid.Empty
            ? RmpPayloadCheck.Valid
            : RmpPayloadCheck.Malformed;

    public static RmpPayloadCheck CheckStateRequest(JsonElement payload) =>
        SessionPayloads.TryDeserialize<GroupStateRequestPayload>(payload) is { } request && ContentPayloads.IsIdentifier(request.GroupId)
            ? RmpPayloadCheck.Valid
            : RmpPayloadCheck.Malformed;

    private static SignedGroupDocument Sign<T>(T content, string domain, IDeviceSigner signer)
    {
        ArgumentNullException.ThrowIfNull(signer);
        var document = SessionPayloads.ToElement(content).GetRawText();
        return new SignedGroupDocument(document, Convert.ToBase64String(signer.SignData(SignedBytes(domain, document))));
    }

    private static bool IsSignedBy(SignedGroupDocument signedDocument, string domain, string ownerPublicKey)
    {
        ArgumentNullException.ThrowIfNull(signedDocument);
        return Decode(ownerPublicKey) is { Length: > 0 } key && Decode(signedDocument.Signature) is { } signature
            && PairingProtocol.VerifySignature(key, SignedBytes(domain, signedDocument.Document), signature);
    }

    private static byte[] SignedBytes(string domain, string document) => Encoding.UTF8.GetBytes(domain + document);

    private static byte[]? Decode(string? base64)
    {
        try
        {
            return base64 is null ? null : Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static T? Parse<T>(string? document)
        where T : class
    {
        if (document is null)
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(document);
            return SessionPayloads.TryDeserialize<T>(json.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsValid(GroupState state) =>
        ContentPayloads.IsIdentifier(state.GroupId)
        && NormalizeName(state.Name) == state.Name
        && state.OwnerId != Guid.Empty
        && state.MembershipVersion >= 1
        && state.EventId != Guid.Empty
        && state.Members.Count is > 0 and <= MaxMembers
        && state.Members.Select(m => m.DeviceId).Distinct().Count() == state.Members.Count
        && state.Members.All(m => m.DeviceId != Guid.Empty && Username.TryNormalize(m.Username, out var name, out _) && name == m.Username)
        && state.HasMember(state.OwnerId);
}
