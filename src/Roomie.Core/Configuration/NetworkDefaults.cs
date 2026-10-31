namespace Roomie.Core.Configuration;

/// <summary>
/// Application-defined network defaults from architecture.md §2.1 and §12. Ports are user-configurable later.
/// </summary>
public static class NetworkDefaults
{
    public const int DiscoveryUdpPort = 45820;
    public const int MessagingTcpPort = 45821;

    public static readonly TimeSpan AnnouncementInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan OfflineTimeout = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);
    public const int MissedHeartbeatThreshold = 3;

    /// <summary>Upper bound of the random delay before answering a <c>DISCOVERY_REQUEST</c>.</summary>
    public static readonly TimeSpan DiscoveryResponseMaxDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>At most one <c>DISCOVERY_RESPONSE</c> per requester within this interval.</summary>
    public static readonly TimeSpan DiscoveryResponseMinInterval = TimeSpan.FromSeconds(2);

    /// <summary>Discovery datagrams larger than this are dropped.</summary>
    public const int MaxDiscoveryPacketSize = 1024;
}
