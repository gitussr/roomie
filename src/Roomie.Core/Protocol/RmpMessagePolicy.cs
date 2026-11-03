using System.Runtime.InteropServices;
using System.Text.Json;
using Roomie.Core.Files;
using Roomie.Core.Groups;

namespace Roomie.Core.Protocol;

/// <summary>Optional features negotiated through <c>HELLO</c> (architecture.md §9).</summary>
public static class RmpCapabilities
{
    public const string Text = "text";
    public const string Groups = "groups";
    public const string FileTransfer = "file-transfer";
}

/// <summary>What was agreed for one session: the intersection of what both sides advertised in <c>HELLO</c>.</summary>
public sealed record RmpNegotiatedSession(int ProtocolVersion, IReadOnlySet<string> Capabilities)
{
    /// <summary>
    /// The highest protocol version both sides support, and the capabilities both offer. Capabilities only
    /// one side knows are dropped, not errors: that is how a newer peer stays compatible.
    /// </summary>
    /// <returns>Null when the two version ranges don't overlap.</returns>
    public static RmpNegotiatedSession? Negotiate(HelloPayload local, HelloPayload remote)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);

        var version = Math.Min(local.ProtocolVersion, remote.ProtocolVersion);
        if (version < Math.Max(local.MinimumSupportedVersion, remote.MinimumSupportedVersion))
        {
            return null;
        }

        var shared = new HashSet<string>(local.Capabilities, StringComparer.Ordinal);
        shared.IntersectWith(remote.Capabilities);
        return new RmpNegotiatedSession(version, shared);
    }
}

/// <summary>Why an incoming message was turned away, as reported in an <c>ERROR</c>. The session stays open.</summary>
public readonly record struct RmpRejection(string Code, string Message);

/// <summary>
/// The rules for each message type (architecture.md §5.1, §5.3): whether it needs a trusted session, which
/// capability it belongs to, how large its payload may be and what shape it must have.
/// </summary>
public sealed record RmpMessageSpec(
    string Type,
    int MaxPayloadBytes,
    bool AllowedBeforeTrust = false,
    string? RequiredCapability = null,
    Func<JsonElement, RmpPayloadCheck>? Validate = null);

/// <summary>
/// The single decision on whether a message may be accepted or sent. A message that fails here was still a
/// complete, well-formed frame, so it is refused without closing the session.
/// </summary>
public static class RmpMessagePolicy
{
    private const int Small = 1024;
    private const int Metadata = RmpContentLimits.MaxFileMetadataBytes;
    private const int Frame = RmpFraming.MaxControlPayload;

    private static readonly Dictionary<string, RmpMessageSpec> Specs = new RmpMessageSpec[]
    {
        new(RmpMessageTypes.Hello, 4 * Small, AllowedBeforeTrust: true),
        new(RmpMessageTypes.Heartbeat, Small, AllowedBeforeTrust: true, Validate: MustBeEmpty),
        new(RmpMessageTypes.HeartbeatAck, Small, AllowedBeforeTrust: true, Validate: MustBeEmpty),
        new(RmpMessageTypes.Error, 4 * Small, AllowedBeforeTrust: true),
        new(RmpMessageTypes.Goodbye, Small, AllowedBeforeTrust: true),

        // Pairing payloads are parsed by the pairing service: a malformed one must fail the attempt it belongs to.
        new(RmpMessageTypes.PairRequest, 2 * Small, AllowedBeforeTrust: true),
        new(RmpMessageTypes.PairResponse, 2 * Small, AllowedBeforeTrust: true),
        new(RmpMessageTypes.PairReveal, 2 * Small, AllowedBeforeTrust: true),
        new(RmpMessageTypes.PairApprove, 2 * Small, AllowedBeforeTrust: true),
        new(RmpMessageTypes.PairReject, 2 * Small, AllowedBeforeTrust: true),

        // A chat body is limited after decoding, by ContentPayloads, not by its escaped size on the wire.
        new(RmpMessageTypes.ChatMessage, Frame, RequiredCapability: RmpCapabilities.Text, Validate: ContentPayloads.CheckChatMessage),
        new(RmpMessageTypes.MessageAck, Small, RequiredCapability: RmpCapabilities.Text, Validate: ContentPayloads.CheckMessageAck),
        new(RmpMessageTypes.MessageRead, Small, RequiredCapability: RmpCapabilities.Text, Validate: ContentPayloads.CheckMessageRead),

        // Creating a group is local, so nothing sends GROUP_CREATE and no service owns it.
        new(RmpMessageTypes.GroupCreate, Metadata, RequiredCapability: RmpCapabilities.Groups),
        new(RmpMessageTypes.GroupInvite, Frame, RequiredCapability: RmpCapabilities.Groups, Validate: GroupProtocol.CheckInvite),
        new(RmpMessageTypes.GroupAccept, Small, RequiredCapability: RmpCapabilities.Groups, Validate: GroupProtocol.CheckAccept),
        new(RmpMessageTypes.GroupRemove, 2 * Small, RequiredCapability: RmpCapabilities.Groups, Validate: GroupProtocol.CheckRemoval),
        new(RmpMessageTypes.GroupState, Frame, RequiredCapability: RmpCapabilities.Groups, Validate: GroupProtocol.CheckState),
        new(RmpMessageTypes.GroupStateRequest, Small, RequiredCapability: RmpCapabilities.Groups, Validate: GroupProtocol.CheckStateRequest),

        new(RmpMessageTypes.FileOffer, Metadata, RequiredCapability: RmpCapabilities.FileTransfer, Validate: ContentPayloads.CheckFileOffer),
        new(RmpMessageTypes.FileAccept, Small, RequiredCapability: RmpCapabilities.FileTransfer, Validate: FilePayloads.CheckAccept),
        new(RmpMessageTypes.FileReject, Small, RequiredCapability: RmpCapabilities.FileTransfer, Validate: FilePayloads.CheckReject),
        new(RmpMessageTypes.FileChunk, Small, RequiredCapability: RmpCapabilities.FileTransfer, Validate: NeverAControlMessage),
        new(RmpMessageTypes.FileComplete, Small, RequiredCapability: RmpCapabilities.FileTransfer, Validate: FilePayloads.CheckComplete),
        new(RmpMessageTypes.FileAck, Small, RequiredCapability: RmpCapabilities.FileTransfer, Validate: FilePayloads.CheckAck),
    }.ToDictionary(spec => spec.Type, StringComparer.Ordinal);

    public static IEnumerable<string> KnownTypes => Specs.Keys;

    public static RmpMessageSpec? Find(string type) => Specs.GetValueOrDefault(type);

    /// <summary>
    /// Decides whether a message is acceptable on a session in this state. The trust check comes first, so
    /// an unpaired peer gets the same answer for every type it may not send and learns nothing about what
    /// this version supports (architecture.md §10, rule 6).
    /// </summary>
    /// <returns>Null when the message is acceptable.</returns>
    public static RmpRejection? Check(string type, JsonElement payload, bool isTrusted, IReadOnlySet<string> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        var spec = Find(type);
        if (!isTrusted && spec is not { AllowedBeforeTrust: true })
        {
            return new RmpRejection(RmpErrorCodes.PeerNotTrusted, "This device has not been paired.");
        }

        // An unknown type is what a newer peer's optional feature looks like (architecture.md §9). A change
        // that older versions can't safely ignore needs a new protocol version instead, so refusing just this
        // message is enough.
        if (spec is null)
        {
            return new RmpRejection(RmpErrorCodes.UnsupportedMessage, "This version of Roomie doesn't understand that message.");
        }

        if (spec.RequiredCapability is { } capability && !capabilities.Contains(capability))
        {
            return new RmpRejection(RmpErrorCodes.UnsupportedMessage, $"The '{capability}' feature wasn't agreed for this session.");
        }

        if (JsonMarshal.GetRawUtf8Value(payload).Length > spec.MaxPayloadBytes)
        {
            return new RmpRejection(RmpErrorCodes.MessageTooLarge, "The message is larger than allowed for its type.");
        }

        return spec.Validate?.Invoke(payload) switch
        {
            RmpPayloadCheck.TooLarge => new RmpRejection(RmpErrorCodes.MessageTooLarge, "The message content exceeds its limit."),
            RmpPayloadCheck.Malformed => new RmpRejection(RmpErrorCodes.InvalidFrame, "The message content is not valid for its type."),
            _ => null,
        };
    }

    private static RmpPayloadCheck MustBeEmpty(JsonElement payload) =>
        SessionPayloads.IsEmpty(payload) ? RmpPayloadCheck.Valid : RmpPayloadCheck.Malformed;

    // File data travels in binary frames (architecture.md §8); FILE_CHUNK is only a name for them.
    private static RmpPayloadCheck NeverAControlMessage(JsonElement payload) => RmpPayloadCheck.Malformed;
}
