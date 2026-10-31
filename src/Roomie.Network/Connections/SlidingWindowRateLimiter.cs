using System.Net;

namespace Roomie.Network.Connections;

/// <summary>Allows at most N events per address within a sliding window. Bounded; refuses new addresses when full.</summary>
public sealed class SlidingWindowRateLimiter
{
    private readonly Lock _gate = new();
    private readonly Dictionary<IPAddress, Queue<DateTimeOffset>> _events = [];
    private readonly TimeProvider _time;
    private readonly int _limit;
    private readonly TimeSpan _window;
    private readonly int _maxTracked;

    public SlidingWindowRateLimiter(TimeProvider time, int limit, TimeSpan window, int maxTracked = 1024)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        _time = time;
        _limit = limit;
        _window = window;
        _maxTracked = maxTracked;
    }

    public bool TryAcquire(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (!_events.TryGetValue(address, out var queue))
            {
                if (_events.Count >= _maxTracked)
                {
                    foreach (var (key, old) in _events.ToList())
                    {
                        Trim(old, now);
                        if (old.Count == 0)
                        {
                            _events.Remove(key);
                        }
                    }

                    if (_events.Count >= _maxTracked)
                    {
                        return false;
                    }
                }

                queue = new Queue<DateTimeOffset>();
                _events.Add(address, queue);
            }

            Trim(queue, now);
            if (queue.Count >= _limit)
            {
                return false;
            }

            queue.Enqueue(now);
            return true;
        }
    }

    private void Trim(Queue<DateTimeOffset> queue, DateTimeOffset now)
    {
        while (queue.Count > 0 && now - queue.Peek() >= _window)
        {
            queue.Dequeue();
        }
    }
}
