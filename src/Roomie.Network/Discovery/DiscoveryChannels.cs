using System.Net;
using System.Net.Sockets;
using Roomie.Core.Configuration;

namespace Roomie.Network.Discovery;

public readonly record struct DiscoveryDatagram(ReadOnlyMemory<byte> Data, IPEndPoint RemoteEndPoint);

/// <summary>A UDP endpoint bound to one LAN interface.</summary>
public interface IDiscoveryChannel : IDisposable
{
    LanInterface LanInterface { get; }

    ValueTask SendAsync(ReadOnlyMemory<byte> data, IPEndPoint destination, CancellationToken cancellationToken);

    /// <summary>Waits for the next datagram. Oversized datagrams are skipped.</summary>
    ValueTask<DiscoveryDatagram> ReceiveAsync(CancellationToken cancellationToken);
}

public interface IDiscoveryChannelFactory
{
    /// <exception cref="SocketException">The port couldn't be bound, for example because it is in use.</exception>
    IDiscoveryChannel Open(LanInterface lanInterface, int port);
}

public sealed class UdpDiscoveryChannelFactory : IDiscoveryChannelFactory
{
    public IDiscoveryChannel Open(LanInterface lanInterface, int port) => new UdpDiscoveryChannel(lanInterface, port);
}

/// <summary>
/// A broadcast-enabled UDP socket bound to one interface address, so sends leave through that
/// adapter and the receiving interface of every datagram is known.
/// </summary>
public sealed class UdpDiscoveryChannel : IDiscoveryChannel
{
    // Windows reports ICMP "port unreachable" from an earlier send as WSAECONNRESET on the next receive.
    // Discovery doesn't care, so turn that behaviour off.
    private const int SioUdpConnReset = unchecked((int)0x9800000C);

    private static readonly IPEndPoint AnyEndPoint = new(IPAddress.Any, 0);

    private readonly Socket _socket;

    // Larger than the protocol limit so an oversized datagram is seen whole and rejected, not truncated.
    private readonly byte[] _buffer = new byte[NetworkDefaults.MaxDiscoveryPacketSize * 2];

    public UdpDiscoveryChannel(LanInterface lanInterface, int port)
    {
        ArgumentNullException.ThrowIfNull(lanInterface);

        LanInterface = lanInterface;
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            // Exclusive: another program must not be able to bind the same port and steal our packets.
            _socket.ExclusiveAddressUse = true;
            _socket.EnableBroadcast = true;
            if (OperatingSystem.IsWindows())
            {
                _socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
            }

            _socket.Bind(new IPEndPoint(lanInterface.Address, port));
        }
        catch
        {
            _socket.Dispose();
            throw;
        }
    }

    public LanInterface LanInterface { get; }

    public IPEndPoint LocalEndPoint => (IPEndPoint)_socket.LocalEndPoint!;

    public async ValueTask SendAsync(ReadOnlyMemory<byte> data, IPEndPoint destination, CancellationToken cancellationToken)
    {
        await _socket.SendToAsync(data, SocketFlags.None, destination, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<DiscoveryDatagram> ReceiveAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await _socket.ReceiveFromAsync(_buffer, SocketFlags.None, AnyEndPoint, cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.MessageSize or SocketError.ConnectionReset)
            {
                continue;
            }

            return new DiscoveryDatagram(_buffer.AsSpan(0, result.ReceivedBytes).ToArray(), (IPEndPoint)result.RemoteEndPoint);
        }
    }

    public void Dispose() => _socket.Dispose();
}
