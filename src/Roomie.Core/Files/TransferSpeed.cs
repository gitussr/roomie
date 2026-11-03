namespace Roomie.Core.Files;

/// <summary>Estimates speed and time left from progress readings, smoothed so the display doesn't jump about.</summary>
public sealed class TransferSpeed
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);
    private const double Smoothing = 0.3;

    private long _lastBytes = -1;
    private DateTimeOffset _lastTime;

    /// <summary>Bytes a second, or null until there is a reading to go on.</summary>
    public double? BytesPerSecond { get; private set; }

    public void Update(long bytes, DateTimeOffset now)
    {
        if (_lastBytes < 0 || bytes < _lastBytes)
        {
            // The first reading, or the transfer stepped back to continue from its last saved point.
            _lastBytes = bytes;
            _lastTime = now;
            BytesPerSecond = null;
            return;
        }

        var elapsed = now - _lastTime;
        if (elapsed < MinInterval)
        {
            return;
        }

        var rate = (bytes - _lastBytes) / elapsed.TotalSeconds;
        BytesPerSecond = BytesPerSecond is { } previous ? (previous * (1 - Smoothing)) + (rate * Smoothing) : rate;
        _lastBytes = bytes;
        _lastTime = now;
    }

    /// <summary>The time left at the current speed, or null if it can't be told.</summary>
    public TimeSpan? TimeLeft(long bytes, long total)
    {
        if (BytesPerSecond is not > 0 || bytes >= total)
        {
            return null;
        }

        var seconds = (total - bytes) / BytesPerSecond.Value;
        return seconds < TimeSpan.MaxValue.TotalSeconds ? TimeSpan.FromSeconds(seconds) : null;
    }
}
