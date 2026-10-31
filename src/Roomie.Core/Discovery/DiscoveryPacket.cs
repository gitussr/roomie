using Roomie.Core.Identity;

namespace Roomie.Core.Discovery;

public enum DiscoveryPacketType
{
    /// <summary>Periodic broadcast presence announcement.</summary>
    Announce,

    /// <summary>Broadcast at startup and on network change, asking peers to answer right away.</summary>
    Request,

    /// <summary>Unicast answer to a <see cref="Request"/>.</summary>
    Response,
}

/// <summary>
/// A UDP discovery packet (architecture.md §2.1). Unauthenticated: everything in it, the username
/// especially, is a claim by whoever sent it and must never be used for trust decisions.
/// </summary>
public sealed record DiscoveryPacket(
    DiscoveryPacketType Type,
    Guid DeviceId,
    string Username,
    int TcpPort,
    string AppVersion)
{
    public string DisplayId => DeviceIdFormat.ToDisplayId(DeviceId);
}
