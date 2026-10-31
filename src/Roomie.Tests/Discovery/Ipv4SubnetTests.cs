using System.Net;
using Roomie.Core.Discovery;

namespace Roomie.Tests.Discovery;

public sealed class Ipv4SubnetTests
{
    [Theory]
    [InlineData("192.168.1.20", 24, "192.168.1.0", "192.168.1.255")]
    [InlineData("10.4.7.9", 8, "10.0.0.0", "10.255.255.255")]
    [InlineData("172.20.5.1", 20, "172.20.0.0", "172.20.15.255")]
    [InlineData("169.254.12.34", 16, "169.254.0.0", "169.254.255.255")]
    public void ComputesNetworkAndBroadcast(string address, int prefix, string network, string broadcast)
    {
        var subnet = new Ipv4Subnet(IPAddress.Parse(address), prefix);

        Assert.Equal(IPAddress.Parse(network), subnet.NetworkAddress);
        Assert.Equal(IPAddress.Parse(broadcast), subnet.BroadcastAddress);
        Assert.Equal($"{address}/{prefix}", subnet.ToString());
    }

    [Fact]
    public void Contains_ChecksPrefix()
    {
        var subnet = new Ipv4Subnet(IPAddress.Parse("192.168.1.20"), 24);

        Assert.True(subnet.Contains(IPAddress.Parse("192.168.1.105")));
        Assert.True(subnet.Contains(IPAddress.Parse("192.168.1.105").MapToIPv6()));
        Assert.False(subnet.Contains(IPAddress.Parse("192.168.2.105")));
        Assert.False(subnet.Contains(IPAddress.IPv6Loopback));
    }

    [Theory]
    [InlineData("10.0.0.1", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.254", true)]
    [InlineData("192.168.0.1", true)]
    [InlineData("169.254.1.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("::1", false)]
    public void IsLanAddress(string address, bool expected)
    {
        Assert.Equal(expected, Ipv4Subnet.IsLanAddress(IPAddress.Parse(address)));
    }

    [Fact]
    public void RejectsIpv6AndBadPrefixes()
    {
        Assert.Throws<ArgumentException>(() => new Ipv4Subnet(IPAddress.IPv6Loopback, 24));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Ipv4Subnet(IPAddress.Loopback, 33));
    }
}
