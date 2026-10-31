namespace Roomie.Core.Configuration;

/// <summary>
/// Application-defined network defaults from architecture.md §12. Ports are user-configurable later.
/// </summary>
public static class NetworkDefaults
{
    public const int DiscoveryUdpPort = 45820;
    public const int MessagingTcpPort = 45821;

    public static readonly TimeSpan AnnouncementInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan OfflineTimeout = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);
    public const int MissedHeartbeatThreshold = 3;
}
