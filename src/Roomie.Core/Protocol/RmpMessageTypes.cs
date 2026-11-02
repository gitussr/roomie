namespace Roomie.Core.Protocol;

/// <summary>The central registry of RMP control message types (architecture.md §5.3).</summary>
public static class RmpMessageTypes
{
    public const string Hello = "HELLO";
    public const string Heartbeat = "HEARTBEAT";
    public const string HeartbeatAck = "HEARTBEAT_ACK";

    public const string PairRequest = "PAIR_REQUEST";
    public const string PairResponse = "PAIR_RESPONSE";
    public const string PairReveal = "PAIR_REVEAL";
    public const string PairApprove = "PAIR_APPROVE";
    public const string PairReject = "PAIR_REJECT";

    public const string ChatMessage = "CHAT_MESSAGE";
    public const string MessageAck = "MESSAGE_ACK";
    public const string MessageRead = "MESSAGE_READ";

    public const string GroupCreate = "GROUP_CREATE";
    public const string GroupInvite = "GROUP_INVITE";
    public const string GroupAccept = "GROUP_ACCEPT";
    public const string GroupRemove = "GROUP_REMOVE";
    public const string GroupState = "GROUP_STATE";
    public const string GroupStateRequest = "GROUP_STATE_REQUEST";

    public const string FileOffer = "FILE_OFFER";
    public const string FileAccept = "FILE_ACCEPT";
    public const string FileReject = "FILE_REJECT";
    public const string FileChunk = "FILE_CHUNK";
    public const string FileComplete = "FILE_COMPLETE";
    public const string FileAck = "FILE_ACK";

    public const string Error = "ERROR";
    public const string Goodbye = "GOODBYE";

    /// <summary>
    /// Whether a peer that hasn't been paired may send this type. Everything else (chat, groups, files)
    /// requires a trusted session (architecture.md §10, rule 6).
    /// </summary>
    public static bool IsAllowedBeforeTrust(string type) => RmpMessagePolicy.Find(type) is { AllowedBeforeTrust: true };
}

/// <summary>Error codes for <c>ERROR</c> messages (architecture.md §9.1).</summary>
public static class RmpErrorCodes
{
    public const string InvalidFrame = "INVALID_FRAME";
    public const string UnsupportedVersion = "UNSUPPORTED_VERSION";
    public const string UnsupportedMessage = "UNSUPPORTED_MESSAGE";
    public const string PeerNotTrusted = "PEER_NOT_TRUSTED";
    public const string IdentityMismatch = "IDENTITY_MISMATCH";
    public const string PairingRejected = "PAIRING_REJECTED";
    public const string MessageTooLarge = "MESSAGE_TOO_LARGE";
    public const string TransferRejected = "TRANSFER_REJECTED";
    public const string RateLimited = "RATE_LIMITED";
    public const string NotAMember = "NOT_A_MEMBER";
    public const string InternalError = "INTERNAL_ERROR";
}

/// <summary>Reasons carried by <c>GOODBYE</c>.</summary>
public static class GoodbyeReasons
{
    public const string Shutdown = "SHUTDOWN";
    public const string DuplicateSession = "DUPLICATE_SESSION";
    public const string PeerBlocked = "PEER_BLOCKED";
}
