using Roomie.Core.Configuration;

namespace Roomie.Network.Connections;

public sealed class ConnectionOptions
{
    /// <summary>TCP port to listen on. 0 picks a free port per interface (tests only).</summary>
    public int Port { get; init; } = NetworkDefaults.MessagingTcpPort;

    public string AppVersion { get; init; } = "0.0.0";

    /// <summary>Optional features advertised in HELLO. List one only when the service behind it is running.</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Covers TLS plus the HELLO exchange.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan HeartbeatInterval { get; init; } = NetworkDefaults.HeartbeatInterval;

    public int MissedHeartbeatThreshold { get; init; } = NetworkDefaults.MissedHeartbeatThreshold;

    /// <summary>Delays between automatic reconnection attempts to a trusted peer (architecture.md §4.3).</summary>
    public IReadOnlyList<TimeSpan> ReconnectDelays { get; init; } =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(16),
        TimeSpan.FromSeconds(30),
    ];

    /// <summary>Each reconnect delay is stretched by a random 0 to this fraction, so peers don't retry in lockstep.</summary>
    public double ReconnectJitter { get; init; } = 0.2;

    /// <summary>How long the side with the larger device ID waits before dialling itself (architecture.md §4.2).</summary>
    public TimeSpan LargerIdDialDelay { get; init; } = TimeSpan.FromSeconds(10);

    public int MaxSessions { get; init; } = 64;

    /// <summary>Sessions with unpaired devices are only for pairing, so few are needed at once.</summary>
    public int MaxUntrustedSessions { get; init; } = 8;

    public int MaxPendingHandshakes { get; init; } = 16;

    public int MaxInboundPerAddressPerMinute { get; init; } = 20;

    /// <summary>A session is closed when more messages than this are refused within a minute.</summary>
    public int MaxRejectedMessagesPerMinute { get; init; } = 30;

    public TimeSpan MaintenanceInterval { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan InterfaceRefreshInterval { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan NetworkChangeDebounce { get; init; } = TimeSpan.FromSeconds(1);
}
