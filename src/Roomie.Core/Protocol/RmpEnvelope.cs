using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;

namespace Roomie.Core.Protocol;

/// <summary>
/// The common RMP control message envelope (architecture.md §5.2). <see cref="SenderId"/> is a claim:
/// receivers check it against the device authenticated by TLS and never trust it on its own.
/// </summary>
public sealed record RmpEnvelope(
    string Type,
    Guid MessageId,
    Guid SenderId,
    Guid RecipientId,
    DateTimeOffset Timestamp,
    JsonElement Payload)
{
    public static RmpEnvelope Create(string type, Guid senderId, Guid recipientId, DateTimeOffset timestamp, JsonElement payload) =>
        new(type, Guid.CreateVersion7(timestamp), senderId, recipientId, timestamp, payload);
}

/// <summary>Strict encoding and validation of <see cref="RmpEnvelope"/> as UTF-8 JSON.</summary>
public static class RmpEnvelopeCodec
{
    public const string ProtocolName = "RMP";
    public const int ProtocolVersion = 1;
    public const int MaxTypeLength = 40;

    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK";

    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 16 };

    private static readonly HashSet<string> EnvelopeFields =
        ["protocol", "version", "type", "messageId", "senderId", "recipientId", "timestamp", "payload"];

    public static byte[] Encode(RmpEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (!IsValidType(envelope.Type))
        {
            throw new ArgumentException($"'{envelope.Type}' is not a valid message type.", nameof(envelope));
        }

        if (envelope.Payload.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("The payload must be a JSON object.", nameof(envelope));
        }

        using var buffer = new MemoryStream(256);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = SessionPayloads.WireEncoder }))
        {
            writer.WriteStartObject();
            writer.WriteString("protocol", ProtocolName);
            writer.WriteNumber("version", ProtocolVersion);
            writer.WriteString("type", envelope.Type);
            writer.WriteString("messageId", envelope.MessageId.ToString("D"));
            writer.WriteString("senderId", envelope.SenderId.ToString("D"));
            writer.WriteString("recipientId", envelope.RecipientId.ToString("D"));
            writer.WriteString("timestamp", envelope.Timestamp.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            writer.WritePropertyName("payload");
            envelope.Payload.WriteTo(writer);
            writer.WriteEndObject();
        }

        var bytes = buffer.ToArray();
        if (bytes.Length > RmpFraming.MaxControlPayload)
        {
            throw new ArgumentException("The encoded message exceeds the control frame limit.", nameof(envelope));
        }

        return bytes;
    }

    /// <summary>Parses and validates a control frame payload. Never throws for bad input.</summary>
    public static bool TryDecode(ReadOnlyMemory<byte> data, [NotNullWhen(true)] out RmpEnvelope? envelope, [NotNullWhen(false)] out string? error)
    {
        envelope = null;
        try
        {
            using var document = JsonDocument.Parse(data, DocumentOptions);
            return TryRead(document.RootElement, out envelope, out error);
        }
        catch (JsonException)
        {
            error = "The message is not valid JSON or is nested too deeply.";
            return false;
        }
        catch (InvalidOperationException)
        {
            // JsonElement only checks a string's encoding when it is read.
            error = "The message contains invalid text.";
            return false;
        }
    }

    private static bool TryRead(JsonElement root, [NotNullWhen(true)] out RmpEnvelope? envelope, [NotNullWhen(false)] out string? error)
    {
        envelope = null;
        if (root.ValueKind != JsonValueKind.Object)
        {
            error = "The message is not a JSON object.";
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            // All envelope fields are critical: an unknown one means a message this version can't understand.
            if (!EnvelopeFields.Contains(property.Name))
            {
                error = $"Unknown envelope field '{Truncate(property.Name)}'.";
                return false;
            }

            if (!seen.Add(property.Name))
            {
                error = $"Duplicate envelope field '{property.Name}'.";
                return false;
            }
        }

        if (seen.Count != EnvelopeFields.Count)
        {
            error = "The envelope is missing required fields.";
            return false;
        }

        if (GetString(root, "protocol") != ProtocolName)
        {
            error = "Wrong protocol name.";
            return false;
        }

        var version = root.GetProperty("version");
        if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != ProtocolVersion)
        {
            error = "Unsupported envelope version.";
            return false;
        }

        var type = GetString(root, "type");
        if (!IsValidType(type))
        {
            error = "Invalid message type.";
            return false;
        }

        if (!TryGetGuid(root, "messageId", out var messageId)
            || !TryGetGuid(root, "senderId", out var senderId)
            || !TryGetGuid(root, "recipientId", out var recipientId))
        {
            error = "Invalid message, sender or recipient ID.";
            return false;
        }

        if (!DateTimeOffset.TryParseExact(GetString(root, "timestamp"), TimestampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal, out var timestamp))
        {
            error = "Invalid timestamp.";
            return false;
        }

        var payload = root.GetProperty("payload");
        if (payload.ValueKind != JsonValueKind.Object)
        {
            error = "The payload must be a JSON object.";
            return false;
        }

        envelope = new RmpEnvelope(type, messageId, senderId, recipientId, timestamp, payload.Clone());
        error = null;
        return true;
    }

    private static bool IsValidType([NotNullWhen(true)] string? type) =>
        !string.IsNullOrEmpty(type) && type.Length <= MaxTypeLength && type.All(ch => ch is (>= 'A' and <= 'Z') or '_');

    private static string? GetString(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static bool TryGetGuid(JsonElement root, string name, out Guid value)
    {
        value = Guid.Empty;
        var text = GetString(root, name);
        return text is not null
            && Guid.TryParseExact(text, "D", out value)
            && value != Guid.Empty
            && string.Equals(text, value.ToString("D"), StringComparison.Ordinal);
    }

    private static string Truncate(string value) => value.Length <= 40 ? value : value[..40] + "…";
}
