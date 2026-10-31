using System.Net;

namespace Roomie.Network.Discovery;

/// <summary>
/// Allows at most one discovery response per source address per interval (architecture.md §2.1).
/// Keyed by address rather than the claimed device ID, which a sender can vary at will. The table is
/// bounded; when it is full of live entries, new requesters are refused until entries expire.
/// </summary>
public sealed class ResponseRateLimiter
{
    public const int DefaultMaxTracked = 1024;

    private readonly Lock _gate = new();
    private readonly Dictionary<IPAddress, DateTimeOffset> _lastResponse = [];
    private readonly TimeProvider _time;
    private readonly TimeSpan _minInterval;
    private readonly int _maxTracked;

    public ResponseRateLimiter(TimeProvider time, TimeSpan minInterval, int maxTracked = DefaultMaxTracked)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTracked, 1);

        _time = time;
        _minInterval = minInterval;
        _maxTracked = maxTracked;
    }

    public bool TryAcquire(IPAddress requester)
    {
        ArgumentNullException.ThrowIfNull(requester);

        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_lastResponse.TryGetValue(requester, out var last) && now - last < _minInterval)
            {
                return false;
            }

            if (!_lastResponse.ContainsKey(requester) && _lastResponse.Count >= _maxTracked)
            {
                foreach (var (address, time) in _lastResponse.ToList())
                {
                    if (now - time >= _minInterval)
                    {
                        _lastResponse.Remove(address);
                    }
                }

                if (_lastResponse.Count >= _maxTracked)
                {
                    return false;
                }
            }

            _lastResponse[requester] = now;
            return true;
        }
    }
}
