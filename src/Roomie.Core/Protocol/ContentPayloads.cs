using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;

namespace Roomie.Core.Protocol;

/// <summary>Content limits checked after parsing (architecture.md §5.1).</summary>
public static class RmpContentLimits
{
    public const int MaxTextBodyBytes = 16 * 1024;
    public const int MaxFileMetadataBytes = 16 * 1024;
    public const long MaxFileSizeBytes = 100L * 1024 * 1024;
    public const int MaxIdentifierLength = 64;
    public const int MaxFilenameLength = 255;
    public const int MaxContentTypeLength = 127;
    public const int Sha256HexLength = 64;
}

public static class RmpContentTypes
{
    public const string Text = "text";
}

public enum RmpPayloadCheck
{
    Valid,
    Malformed,
    TooLarge,
}

/// <summary>The payload of a <c>CHAT_MESSAGE</c> (architecture.md §6). Group copies also carry the group fields.</summary>
public sealed record ChatMessagePayload(
    string ConversationId,
    string ContentType,
    string Content,
    string? GroupId = null,
    int? MembershipVersion = null);

/// <summary>The payload of a <c>FILE_OFFER</c> (architecture.md §8).</summary>
public sealed record FileOfferPayload(
    Guid TransferId,
    string Filename,
    long FileSize,
    string Sha256,
    string ContentType);

/// <summary><c>MESSAGE_ACK</c>: the recipient has stored this message.</summary>
public sealed record MessageAckPayload(Guid MessageId);

/// <summary><c>MESSAGE_READ</c>: the recipient's user read these messages.</summary>
public sealed record MessageReadPayload(IReadOnlyList<Guid> MessageIds);

/// <summary>Strict parsing and limits for message content.</summary>
public static class ContentPayloads
{
    /// <summary>How many messages one <c>MESSAGE_READ</c> may cover; more take several.</summary>
    public const int MaxReadReceiptsPerMessage = 20;

    /// <summary>Removes what a chat body may not contain, and surrounding whitespace.</summary>
    public static string NormalizeText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return string.Concat(text.Where(ch => !IsForbiddenInText(ch))).Trim();
    }

    public static RmpPayloadCheck CheckMessageAck(JsonElement payload) => Read<MessageAckPayload>(payload, Check, out _);

    public static RmpPayloadCheck CheckMessageRead(JsonElement payload) => Read<MessageReadPayload>(payload, Check, out _);

    public static bool TryReadMessageAck(JsonElement payload, [NotNullWhen(true)] out MessageAckPayload? ack) =>
        Read(payload, Check, out ack) == RmpPayloadCheck.Valid;

    public static bool TryReadMessageRead(JsonElement payload, [NotNullWhen(true)] out MessageReadPayload? read) =>
        Read(payload, Check, out read) == RmpPayloadCheck.Valid;

    private static RmpPayloadCheck Check(MessageAckPayload ack) =>
        ack.MessageId != Guid.Empty ? RmpPayloadCheck.Valid : RmpPayloadCheck.Malformed;

    private static RmpPayloadCheck Check(MessageReadPayload read) =>
        read.MessageIds.Count is > 0 and <= MaxReadReceiptsPerMessage && !read.MessageIds.Contains(Guid.Empty)
            ? RmpPayloadCheck.Valid
            : RmpPayloadCheck.Malformed;

    private static bool IsForbiddenInText(char ch) => char.IsControl(ch) && ch is not ('\t' or '\r' or '\n');

    public static bool TryReadChatMessage(JsonElement payload, [NotNullWhen(true)] out ChatMessagePayload? message) =>
        Read(payload, Check, out message) == RmpPayloadCheck.Valid;

    public static bool TryReadFileOffer(JsonElement payload, [NotNullWhen(true)] out FileOfferPayload? offer) =>
        Read(payload, Check, out offer) == RmpPayloadCheck.Valid;

    public static RmpPayloadCheck CheckChatMessage(JsonElement payload) => Read<ChatMessagePayload>(payload, Check, out _);

    public static RmpPayloadCheck CheckFileOffer(JsonElement payload) => Read<FileOfferPayload>(payload, Check, out _);

    private static RmpPayloadCheck Read<T>(JsonElement payload, Func<T, RmpPayloadCheck> check, out T? value)
        where T : class
    {
        value = SessionPayloads.TryDeserialize<T>(payload);
        if (value is null)
        {
            return RmpPayloadCheck.Malformed;
        }

        var result = check(value);
        if (result != RmpPayloadCheck.Valid)
        {
            value = null;
        }

        return result;
    }

    private static RmpPayloadCheck Check(ChatMessagePayload message)
    {
        if (Encoding.UTF8.GetByteCount(message.Content) > RmpContentLimits.MaxTextBodyBytes)
        {
            return RmpPayloadCheck.TooLarge;
        }

        // Limiting control characters also guarantees a full-size body fits a control frame: everything
        // left costs at most about twice its size once escaped.
        var valid = IsIdentifier(message.ConversationId)
            && message.ContentType == RmpContentTypes.Text
            && message.Content.Length > 0
            && !message.Content.Any(IsForbiddenInText)
            && (message.GroupId is null) == (message.MembershipVersion is null)
            && (message.GroupId is null || IsIdentifier(message.GroupId))
            && message.MembershipVersion is null or >= 1;

        return valid ? RmpPayloadCheck.Valid : RmpPayloadCheck.Malformed;
    }

    private static RmpPayloadCheck Check(FileOfferPayload offer)
    {
        if (offer.FileSize > RmpContentLimits.MaxFileSizeBytes)
        {
            return RmpPayloadCheck.TooLarge;
        }

        var valid = offer.TransferId != Guid.Empty
            && offer.FileSize >= 0
            && IsPlainFilename(offer.Filename)
            && offer.Sha256.Length == RmpContentLimits.Sha256HexLength
            && offer.Sha256.All(ch => ch is (>= '0' and <= '9') or (>= 'a' and <= 'f'))
            && offer.ContentType.Length is > 0 and <= RmpContentLimits.MaxContentTypeLength
            && offer.ContentType.All(ch => ch is > ' ' and < '\x7f');

        return valid ? RmpPayloadCheck.Valid : RmpPayloadCheck.Malformed;
    }

    public static bool IsIdentifier(string value) =>
        value.Length is > 0 and <= RmpContentLimits.MaxIdentifierLength
        && value.All(ch => ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_');

    // A first line of defense against a peer choosing a local path (architecture.md §10, rule 5). The file
    // transfer service must still build the final path itself and handle names Windows reserves.
    private static bool IsPlainFilename(string value) =>
        value.Length is > 0 and <= RmpContentLimits.MaxFilenameLength
        && value is not ("." or "..")
        && !value.Any(ch => char.IsControl(ch) || ch is '/' or '\\' or ':');
}
