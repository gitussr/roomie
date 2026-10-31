using System.Net;
using System.Net.Sockets;
using Roomie.Core.Discovery;
using Roomie.Network.Discovery;

namespace Roomie.Tests.Discovery;

/// <summary>Exercises real sockets over loopback; broadcast itself needs two machines (see README).</summary>
public sealed class UdpDiscoveryChannelTests
{
    private static readonly LanInterface Loopback = new("lo", "Loopback", new Ipv4Subnet(IPAddress.Loopback, 8));

    [Fact]
    public async Task SendsAndReceivesDatagrams()
    {
        using var a = new UdpDiscoveryChannel(Loopback, 0);
        using var b = new UdpDiscoveryChannel(Loopback, 0);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await a.SendAsync("hello"u8.ToArray(), b.LocalEndPoint, timeout.Token);
        var datagram = await b.ReceiveAsync(timeout.Token);

        Assert.Equal("hello"u8.ToArray(), datagram.Data.ToArray());
        Assert.Equal(a.LocalEndPoint, datagram.RemoteEndPoint);
    }

    [Fact]
    public async Task OversizedDatagramsArriveWholeSoTheyCanBeRejected()
    {
        using var a = new UdpDiscoveryChannel(Loopback, 0);
        using var b = new UdpDiscoveryChannel(Loopback, 0);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await a.SendAsync(new byte[1500], b.LocalEndPoint, timeout.Token);
        var datagram = await b.ReceiveAsync(timeout.Token);

        Assert.Equal(1500, datagram.Data.Length);
        Assert.False(DiscoveryPacketCodec.TryDecode(datagram.Data.Span, out _, out var error));
        Assert.Equal(DiscoveryPacketError.TooLarge, error);
    }

    [Fact]
    public void BindingAPortInUse_Throws()
    {
        using var a = new UdpDiscoveryChannel(Loopback, 0);

        var ex = Assert.Throws<SocketException>(() => new UdpDiscoveryChannel(Loopback, a.LocalEndPoint.Port));
        Assert.Contains(ex.SocketErrorCode, new[] { SocketError.AddressAlreadyInUse, SocketError.AccessDenied });
    }
}
