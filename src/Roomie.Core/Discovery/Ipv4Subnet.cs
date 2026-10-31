using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Roomie.Core.Discovery;

/// <summary>An IPv4 interface address with its prefix length, e.g. <c>192.168.1.20/24</c>.</summary>
public readonly record struct Ipv4Subnet
{
    private readonly uint _address;

    public Ipv4Subnet(IPAddress address, int prefixLength)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentOutOfRangeException.ThrowIfNegative(prefixLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(prefixLength, 32);

        if (!TryToUInt32(address, out _address))
        {
            throw new ArgumentException("Only IPv4 addresses are supported.", nameof(address));
        }

        PrefixLength = prefixLength;
    }

    public int PrefixLength { get; }

    public IPAddress Address => FromUInt32(_address);

    public IPAddress NetworkAddress => FromUInt32(_address & Mask);

    /// <summary>The subnet-directed broadcast address, e.g. <c>192.168.1.255</c> for a /24.</summary>
    public IPAddress BroadcastAddress => FromUInt32(_address | ~Mask);

    private uint Mask => PrefixLength == 0 ? 0u : uint.MaxValue << (32 - PrefixLength);

    public bool Contains(IPAddress address) =>
        TryToUInt32(address, out var value) && (value & Mask) == (_address & Mask);

    /// <summary>
    /// True for addresses that can only be on a local network: RFC 1918 private ranges and
    /// 169.254.0.0/16 link-local (two PCs cabled together without DHCP). Roomie never listens elsewhere.
    /// </summary>
    public static bool IsLanAddress(IPAddress address)
    {
        if (!TryToUInt32(address, out var value))
        {
            return false;
        }

        return (value & 0xFF000000) == 0x0A000000      // 10.0.0.0/8
            || (value & 0xFFF00000) == 0xAC100000      // 172.16.0.0/12
            || (value & 0xFFFF0000) == 0xC0A80000      // 192.168.0.0/16
            || (value & 0xFFFF0000) == 0xA9FE0000;     // 169.254.0.0/16
    }

    /// <summary>Returns the plain IPv4 form of an IPv4 or IPv4-mapped IPv6 address, or null otherwise.</summary>
    public static IPAddress? Normalize(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return address;
        }

        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : null;
    }

    public override string ToString() => $"{Address}/{PrefixLength}";

    private static bool TryToUInt32(IPAddress address, out uint value)
    {
        value = 0;
        var ipv4 = Normalize(address);
        if (ipv4 is null)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[4];
        if (!ipv4.TryWriteBytes(bytes, out _))
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        return true;
    }

    private static IPAddress FromUInt32(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return new IPAddress(bytes);
    }
}
