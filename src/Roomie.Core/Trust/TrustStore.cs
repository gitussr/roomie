namespace Roomie.Core.Trust;

/// <summary>A device the user has paired with. Its pinned fingerprint is the only proof of its identity.</summary>
public sealed record TrustedPeer(Guid DeviceId, string Username, string CertificateFingerprint, DateTimeOffset TrustedAt);

/// <summary>Trusted and blocked devices (architecture.md §1.2, §3). Implementations must be thread-safe.</summary>
public interface ITrustStore
{
    event EventHandler? Changed;

    TrustedPeer? FindTrusted(Guid deviceId);

    bool IsBlocked(Guid deviceId);
}

/// <summary>
/// Keeps trust in memory. Used until pairing (phase 4) and the database (phase 7) persist it, and in tests.
/// </summary>
public sealed class InMemoryTrustStore : ITrustStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, TrustedPeer> _trusted = [];
    private readonly HashSet<Guid> _blocked = [];

    public event EventHandler? Changed;

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
        lock (_gate)
        {
            _trusted[peer.DeviceId] = peer;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Block(Guid deviceId)
    {
        lock (_gate)
        {
            _blocked.Add(deviceId);
            _trusted.Remove(deviceId);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
