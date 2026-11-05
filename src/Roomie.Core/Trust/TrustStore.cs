namespace Roomie.Core.Trust;

/// <summary>A device the user has paired with. Its pinned fingerprint is the only proof of its identity.</summary>
/// <param name="Username">The name the device last gave. Display only, and never proof of who it is.</param>
public sealed record TrustedPeer(Guid DeviceId, string Username, string CertificateFingerprint, DateTimeOffset TrustedAt);

/// <summary>Trusted and blocked devices (architecture.md §1.2, §3). Implementations must be thread-safe.</summary>
public interface ITrustStore
{
    /// <summary>Raised after any change, on the thread that made it.</summary>
    event EventHandler? Changed;

    IReadOnlyList<TrustedPeer> TrustedPeers { get; }

    IReadOnlyCollection<Guid> BlockedDevices { get; }

    TrustedPeer? FindTrusted(Guid deviceId);

    bool IsBlocked(Guid deviceId);

    /// <summary>Pins the device's fingerprint, replacing any earlier pairing with it, and unblocks it.</summary>
    void Trust(TrustedPeer peer);

    /// <summary>Updates the name of a paired device. Its pin and the rest stay as they are.</summary>
    /// <returns>Whether the name changed. False if it was the same, or the device isn't paired.</returns>
    bool UpdateUsername(Guid deviceId, string username);

    /// <summary>Revokes trust. The device becomes an ordinary discovered device again.</summary>
    void Remove(Guid deviceId);

    /// <summary>Revokes trust and refuses every future connection from the device.</summary>
    void Block(Guid deviceId);

    void Unblock(Guid deviceId);
}

/// <summary>Trust kept in memory only. Used in tests.</summary>
public class InMemoryTrustStore : ITrustStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, TrustedPeer> _trusted = [];
    private readonly HashSet<Guid> _blocked = [];

    public event EventHandler? Changed;

    public IReadOnlyList<TrustedPeer> TrustedPeers
    {
        get
        {
            lock (_gate)
            {
                return [.. _trusted.Values.OrderBy(p => p.TrustedAt)];
            }
        }
    }

    public IReadOnlyCollection<Guid> BlockedDevices
    {
        get
        {
            lock (_gate)
            {
                return [.. _blocked];
            }
        }
    }

    public TrustedPeer? FindTrusted(Guid deviceId)
    {
        lock (_gate)
        {
            return _trusted.GetValueOrDefault(deviceId);
        }
    }

    public bool IsBlocked(Guid deviceId)
    {
        lock (_gate)
        {
            return _blocked.Contains(deviceId);
        }
    }

    public void Trust(TrustedPeer peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        Mutate(() =>
        {
            _trusted[peer.DeviceId] = peer;
            _blocked.Remove(peer.DeviceId);
        });
    }

    public bool UpdateUsername(Guid deviceId, string username)
    {
        lock (_gate)
        {
            if (!_trusted.TryGetValue(deviceId, out var peer) || peer.Username == username)
            {
                return false;
            }
        }

        var changed = false;
        Mutate(() =>
        {
            // Looked up again: it may have been forgotten since, and must not come back.
            if (_trusted.TryGetValue(deviceId, out var peer))
            {
                _trusted[deviceId] = peer with { Username = username };
                changed = true;
            }
        });
        return changed;
    }

    public void Remove(Guid deviceId) => Mutate(() => _trusted.Remove(deviceId));

    public void Block(Guid deviceId) => Mutate(() =>
    {
        _trusted.Remove(deviceId);
        _blocked.Add(deviceId);
    });

    public void Unblock(Guid deviceId) => Mutate(() => _blocked.Remove(deviceId));

    /// <summary>Replaces the whole contents, e.g. after loading from disk. Doesn't raise <see cref="Changed"/>.</summary>
    protected void Load(IEnumerable<TrustedPeer> trusted, IEnumerable<Guid> blocked)
    {
        lock (_gate)
        {
            _trusted.Clear();
            foreach (var peer in trusted)
            {
                _trusted[peer.DeviceId] = peer;
            }

            _blocked.Clear();
            _blocked.UnionWith(blocked);
        }
    }

    /// <summary>Called inside the lock after each change, so a subclass can persist a consistent snapshot.</summary>
    protected virtual void OnChanged(IReadOnlyList<TrustedPeer> trusted, IReadOnlyCollection<Guid> blocked)
    {
    }

    private void Mutate(Action change)
    {
        lock (_gate)
        {
            change();
            OnChanged([.. _trusted.Values.OrderBy(p => p.TrustedAt)], [.. _blocked]);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
