using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Roomie.Core.Configuration;

namespace Roomie.Core.Discovery;

public enum DiscoveryPacketError
{
    None,
    TooLarge,
    Malformed,
    WrongProtocol,
    UnsupportedVersion,
    UnknownType,
    InvalidDeviceId,
    InvalidUsername,
    InvalidTcpPort,
    InvalidAppVersion,
}

/// <summary>
/// Encodes and strictly validates discovery packets. Anything that arrives over UDP is hostile until
/// proven otherwise, so decoding never throws and rejects anything outside the specification.
/// </summary>
public static class DiscoveryPacketCodec
{
    public const string ProtocolName = "roomie";
    public const int ProtocolVersion = 1;

    private const int MaxProperties = 32;

    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 4 };

    /// <exception cref="ArgumentException">The packet would fail validation on the receiving side.</exception>
    public static byte[] Encode(DiscoveryPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        var error = Validate(packet.DeviceId, packet.Username, packet.TcpPort, packet.AppVersion);
        if (error != DiscoveryPacketError.None)
        {
            throw new ArgumentException($"The discovery packet is invalid: {error}.", nameof(packet));
        }

        using var buffer = new MemoryStream(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("protocol", ProtocolName);
            writer.WriteNumber("protocolVersion", ProtocolVersion);
            writer.WriteString("type", ToWireType(packet.Type));
            writer.WriteString("deviceId", packet.DeviceId.ToString("D"));
            writer.WriteString("username", packet.Username);
            writer.WriteNumber("tcpPort", packet.TcpPort);
            writer.WriteString("appVersion", packet.AppVersion);
            writer.WriteEndObject();
        }

        // Can't happen with the length limits above, but the limit is part of the protocol.
        var bytes = buffer.ToArray();
        if (bytes.Length > NetworkDefaults.MaxDiscoveryPacketSize)
        {
            throw new ArgumentException("The encoded discovery packet is too large.", nameof(packet));
        }

        return bytes;
    }

    public static bool TryDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out DiscoveryPacket? packet, out DiscoveryPacketError error)
    {
        packet = null;

        if (data.Length > NetworkDefaults.MaxDiscoveryPacketSize)
        {
            error = DiscoveryPacketError.TooLarge;
            return false;
        }

        if (data.IsEmpty)
        {
            error = DiscoveryPacketError.Malformed;
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(data.ToArray(), DocumentOptions);
            return TryRead(document.RootElement, out packet, out error);
        }
        catch (JsonException)
        {
            error = DiscoveryPacketError.Malformed;
            return false;
        }
    }

    private static bool TryRead(JsonElement root, [NotNullWhen(true)] out DiscoveryPacket? packet, out DiscoveryPacketError error)
    {
        packet = null;

        if (root.ValueKind != JsonValueKind.Object)
        {
            error = DiscoveryPacketError.Malformed;
            return false;
        }

        string? protocol = null, type = null, deviceId = null, username = null, appVersion = null;
        int? protocolVersion = null, tcpPort = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var property in root.EnumerateObject())
        {
            // Unknown properties are tolerated for forward compatibility, but a repeated property is
            // ambiguous (which value wins?) and is rejected outright.
            if (seen.Count >= MaxProperties || !seen.Add(property.Name))
            {
                error = DiscoveryPacketError.Malformed;
                return false;
            }

            var ok = property.Name switch
            {
                "protocol" => TryGetString(property.Value, out protocol),
                "protocolVersion" => TryGetInt(property.Value, out protocolVersion),
                "type" => TryGetString(property.Value, out type),
                "deviceId" => TryGetString(property.Value, out deviceId),
                "username" => TryGetString(property.Value, out username),
                "tcpPort" => TryGetInt(property.Value, out tcpPort),
                "appVersion" => TryGetString(property.Value, out appVersion),
                _ => true,
            };

            if (!ok)
            {
                error = DiscoveryPacketError.Malformed;
                return false;
            }
        }

        if (protocol != ProtocolName)
        {
            error = DiscoveryPacketError.WrongProtocol;
            return false;
        }

        if (protocolVersion != ProtocolVersion)
        {
            error = DiscoveryPacketError.UnsupportedVersion;
            return false;
        }

        if (!TryParseWireType(type, out var packetType))
        {
            error = DiscoveryPacketError.UnknownType;
            return false;
        }

        if (!TryParseDeviceId(deviceId, out var id))
        {
            error = DiscoveryPacketError.InvalidDeviceId;
            return false;
        }

        error = Validate(id, username, tcpPort ?? 0, appVersion);
        if (error != DiscoveryPacketError.None)
        {
            return false;
        }

        packet = new DiscoveryPacket(packetType, id, username!, tcpPort!.Value, appVersion!);
        return true;
    }

    private static DiscoveryPacketError Validate(Guid deviceId, string? username, int tcpPort, string? appVersion)
    {
        if (deviceId == Guid.Empty)
        {
            return DiscoveryPacketError.InvalidDeviceId;
        }

        // The name must already be in normalized form: a sender has no reason to pad or disguise it.
        if (!Identity.Username.TryNormalize(username, out var normalized, out _) || normalized != username)
        {
            return DiscoveryPacketError.InvalidUsername;
        }

        if (tcpPort is < 1 or > 65535)
        {
            return DiscoveryPacketError.InvalidTcpPort;
        }

        if (!AppVersionFormat.IsValid(appVersion))
        {
            return DiscoveryPacketError.InvalidAppVersion;
        }

        return DiscoveryPacketError.None;
    }

    // Canonical lowercase "D" form only, so one device can't appear under several spellings.
    private static bool TryParseDeviceId(string? value, out Guid id)
    {
        id = Guid.Empty;
        return value is not null
            && Guid.TryParseExact(value, "D", out id)
            && string.Equals(value, id.ToString("D"), StringComparison.Ordinal);
    }

    private static string ToWireType(DiscoveryPacketType type) => type switch
    {
        DiscoveryPacketType.Announce => "DISCOVERY_ANNOUNCE",
        DiscoveryPacketType.Request => "DISCOVERY_REQUEST",
        DiscoveryPacketType.Response => "DISCOVERY_RESPONSE",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    private static bool TryParseWireType(string? value, out DiscoveryPacketType type)
    {
        switch (value)
        {
            case "DISCOVERY_ANNOUNCE":
                type = DiscoveryPacketType.Announce;
                return true;
            case "DISCOVERY_REQUEST":
                type = DiscoveryPacketType.Request;
                return true;
            case "DISCOVERY_RESPONSE":
                type = DiscoveryPacketType.Response;
                return true;
            default:
                type = default;
                return false;
        }
    }

    private static bool TryGetString(JsonElement element, out string? value)
    {
        value = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        return value is not null;
    }

    private static bool TryGetInt(JsonElement element, out int? value)
    {
        value = null;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var number))
        {
            return false;
        }

        value = number;
        return true;
    }
}
