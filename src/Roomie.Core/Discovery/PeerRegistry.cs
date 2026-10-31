using System.Net;
using Roomie.Core.Identity;

namespace Roomie.Core.Discovery;

/// <summary>
/// A device that has announced itself (architecture.md §2). Discovered only: it is neither connected
/// nor trusted, and its username is an unverified claim.
/// </summary>
/// <param name="Addresses">Addresses the device was heard from recently, most recent first. Connection hints only.</param>
public sealed record DiscoveredPeer(
    Guid DeviceId,
    string Username,
    int TcpPort,
    string AppVersion,
    IReadOnlyList<IPAddress> Addresses,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt)
{
    public string DisplayId => DeviceIdFormat.ToDisplayId(DeviceId);
}

public enum PeerObservation
{
    /// <summary>A device not in the registry was added.</summary>
    Added,

    /// <summary>A known device changed its username, port, version or gained an address.</summary>
    Updated,

    /// <summary>A known device was seen again with nothing new.</summary>
    Refreshed,

    /// <summary>The registry is full, so a new device was ignored.</summary>
    Rejected,
}

/// <summary>
/// Discovered devices keyed by device ID, so a peer heard on Wi-Fi and Ethernet is one entry. Each
/// address expires on its own after the offline timeout; a device with no live address is removed.
/// Thread-safe. <see cref="Changed"/> is raised outside the lock, on the thread that made the change.
/// </summary>
public sealed class PeerRegistry
{
    /// <summary>Bounds memory when someone floods the LAN with forged device IDs.</summary>
    public const int DefaultCapacity = 256;

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Entry> _peers = [];
    private readonly TimeSpan _offlineTimeout;
    private readonly int _capacity;

    public PeerRegistry(TimeSpan offlineTimeout, int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(offlineTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        _offlineTimeout = offlineTimeout;
        _capacity = capacity;
    }

    public event EventHandler? Changed;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _peers.Count;
            }
        }
    }

    public PeerObservation Observe(DiscoveryPacket packet, IPAddress source, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(source);

        PeerObservation result;
        lock (_gate)
        {
            if (!_peers.TryGetValue(packet.DeviceId, out var entry))
            {
                if (_peers.Count >= _capacity)
                {
                    return PeerObservation.Rejected;
                }

                entry = new Entry(packet, now);
                entry.Addresses[source] = now;
                _peers.Add(packet.DeviceId, entry);
                result = PeerObservation.Added;
            }
            else
            {
                var changed = entry.Username != packet.Username
                    || entry.TcpPort != packet.TcpPort
                    || entry.AppVersion != packet.AppVersion
                    || !entry.Addresses.ContainsKey(source);

                entry.Username = packet.Username;
                entry.TcpPort = packet.TcpPort;
                entry.AppVersion = packet.AppVersion;
                entry.Addresses[source] = Max(entry.Addresses.GetValueOrDefault(source), now);
                entry.LastSeenAt = Max(entry.LastSeenAt, now);
                result = changed ? PeerObservation.Updated : PeerObservation.Refreshed;
            }
        }

        if (result is PeerObservation.Added or PeerObservation.Updated)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return result;
    }

    /// <summary>Drops addresses not heard from within the offline timeout, and peers left with none.</summary>
    /// <returns>The number of peers removed.</returns>
    public int Prune(DateTimeOffset now)
    {
        var removedPeers = 0;
        var changed = false;

        lock (_gate)
        {
            foreach (var (deviceId, entry) in _peers.ToList())
            {
                foreach (var (address, lastSeen) in entry.Addresses.ToList())
                {
                    if (now - lastSeen > _offlineTimeout)
                    {
                        entry.Addresses.Remove(address);
                        changed = true;
                    }
                }

                if (entry.Addresses.Count == 0)
                {
                    _peers.Remove(deviceId);
                    removedPeers++;
                }
            }
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return removedPeers;
    }

    public IReadOnlyList<DiscoveredPeer> Snapshot()
    {
        lock (_gate)
        {
            return _peers.Values
                .Select(entry => entry.ToPeer())
                .OrderBy(peer => peer.Username, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(peer => peer.DeviceId)
                .ToList();
        }
    }

    public bool TryGet(Guid deviceId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out DiscoveredPeer? peer)
    {
        lock (_gate)
        {
            peer = _peers.TryGetValue(deviceId, out var entry) ? entry.ToPeer() : null;
            return peer is not null;
        }
    }

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left > right ? left : right;

    private sealed class Entry(DiscoveryPacket packet, DateTimeOffset firstSeenAt)
    {
        public Guid DeviceId { get; } = packet.DeviceId;
        public string Username { get; set; } = packet.Username;
        public int TcpPort { get; set; } = packet.TcpPort;
        public string AppVersion { get; set; } = packet.AppVersion;
        public DateTimeOffset FirstSeenAt { get; } = firstSeenAt;
        public DateTimeOffset LastSeenAt { get; set; } = firstSeenAt;
        public Dictionary<IPAddress, DateTimeOffset> Addresses { get; } = [];

        public DiscoveredPeer ToPeer() => new(
            DeviceId,
            Username,
            TcpPort,
            AppVersion,
            Addresses.OrderByDescending(pair => pair.Value).Select(pair => pair.Key).ToList(),
            FirstSeenAt,
            LastSeenAt);
    }
}
