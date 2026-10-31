using System.Net;
using Roomie.Network.Discovery;

namespace Roomie.Tests.Discovery;

public sealed class ResponseRateLimiterTests
{
    private readonly ManualTimeProvider _time = new();

    [Fact]
    public void AllowsOneResponsePerIntervalPerAddress()
    {
        var limiter = new ResponseRateLimiter(_time, TimeSpan.FromSeconds(2));
        var a = IPAddress.Parse("192.168.1.10");
        var b = IPAddress.Parse("192.168.1.11");

        Assert.True(limiter.TryAcquire(a));
        Assert.False(limiter.TryAcquire(a));
        Assert.True(limiter.TryAcquire(b));

        _time.Advance(TimeSpan.FromMilliseconds(1999));
        Assert.False(limiter.TryAcquire(a));

        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(limiter.TryAcquire(a));
    }

    [Fact]
    public void WhenFull_RefusesNewAddressesUntilEntriesExpire()
    {
        var limiter = new ResponseRateLimiter(_time, TimeSpan.FromSeconds(2), maxTracked: 2);

        Assert.True(limiter.TryAcquire(IPAddress.Parse("10.0.0.1")));
        Assert.True(limiter.TryAcquire(IPAddress.Parse("10.0.0.2")));
        Assert.False(limiter.TryAcquire(IPAddress.Parse("10.0.0.3")));

        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.True(limiter.TryAcquire(IPAddress.Parse("10.0.0.3")));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
