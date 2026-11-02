using System.Diagnostics.CodeAnalysis;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Roomie.Core.Configuration;

namespace Roomie.Core.Protocol;

/// <summary>Capabilities and versions exchanged first on every session (architecture.md §9).</summary>
public sealed record HelloPayload(
    int ProtocolVersion,
    int MinimumSupportedVersion,
    string ApplicationVersion,
    IReadOnlyList<string> Capabilities);

public sealed record GoodbyePayload(string Reason);

/// <summary>An error report. <see cref="Message"/> is for humans and never carries internal details.</summary>
public sealed record ErrorPayload(string Code, string Message, Guid? RequestId = null);

/// <summary>Strict (de)serialization of session payloads: unknown members, missing members and nulls are rejected.</summary>
public static class SessionPayloads
{
    public const int MaxCapabilities = 32;
    public const int MaxCapabilityLength = 32;
    public const int MaxErrorMessageLength = 512;
    public const int MaxReasonLength = 40;

    /// <summary>
    /// The default encoder writes every non-ASCII character and common punctuation as <c>\uXXXX</c> for safety
    /// inside HTML. RMP is never embedded in a page, and that escaping could push a 16 KiB text body past the
    /// 64 KiB control frame limit.
    /// </summary>
    internal static JavaScriptEncoder WireEncoder { get; } = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = WireEncoder,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static JsonElement ToElement<T>(T payload) => JsonSerializer.SerializeToElement(payload, Options);

    public static JsonElement Empty { get; } = JsonDocument.Parse("{}").RootElement.Clone();

    public static bool TryReadHello(JsonElement payload, [NotNullWhen(true)] out HelloPayload? hello)
    {
        hello = TryDeserialize<HelloPayload>(payload);
        if (hello is null)
        {
            return false;
        }

        var valid = hello.ProtocolVersion >= 1
            && hello.MinimumSupportedVersion >= 1
            && hello.MinimumSupportedVersion <= hello.ProtocolVersion
            && AppVersionFormat.IsValid(hello.ApplicationVersion)
            && hello.Capabilities.Count <= MaxCapabilities
            && hello.Capabilities.All(IsValidCapability)
            && hello.Capabilities.Distinct(StringComparer.Ordinal).Count() == hello.Capabilities.Count;

        if (!valid)
        {
            hello = null;
        }

        return hello is not null;
    }

    public static bool TryReadGoodbye(JsonElement payload, [NotNullWhen(true)] out GoodbyePayload? goodbye)
    {
        goodbye = TryDeserialize<GoodbyePayload>(payload);
        if (goodbye is not null && !IsCode(goodbye.Reason))
        {
            goodbye = null;
        }

        return goodbye is not null;
    }

    public static bool TryReadError(JsonElement payload, [NotNullWhen(true)] out ErrorPayload? error)
    {
        error = TryDeserialize<ErrorPayload>(payload);
        if (error is not null && (!IsCode(error.Code) || error.Message.Length > MaxErrorMessageLength))
        {
            error = null;
        }

        return error is not null;
    }

    /// <summary>True when the payload is an empty object, as for <c>HEARTBEAT</c> and <c>HEARTBEAT_ACK</c>.</summary>
    public static bool IsEmpty(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Object && !payload.EnumerateObject().Any();

    /// <summary>Strictly deserializes a payload, returning null when it doesn't match <typeparamref name="T"/> exactly.</summary>
    public static T? TryDeserialize<T>(JsonElement payload)
        where T : class
    {
        try
        {
            return payload.Deserialize<T>(Options);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // InvalidOperationException: a string with invalid encoding, only detected when it is read.
            return null;
        }
    }

    private static bool IsValidCapability(string capability) =>
        capability.Length is > 0 and <= MaxCapabilityLength
        && capability.All(ch => ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');

    private static bool IsCode(string value) =>
        value.Length is > 0 and <= MaxReasonLength && value.All(ch => ch is (>= 'A' and <= 'Z') or '_');
}
