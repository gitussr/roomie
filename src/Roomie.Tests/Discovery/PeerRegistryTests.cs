using System.Net;
using Roomie.Core.Discovery;

namespace Roomie.Tests.Discovery;

public sealed class PeerRegistryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    private static readonly IPAddress Wifi = IPAddress.Parse("192.168.1.105");
    private static readonly IPAddress Ethernet = IPAddress.Parse("10.0.0.105");

    private readonly PeerRegistry _registry = new(TimeSpan.FromSeconds(20));
    private int _changes;

    public PeerRegistryTests()
    {
        _registry.Changed += (_, _) => _changes++;
    }

    private static DiscoveryPacket Packet(Guid id, string name = "Office PC", int port = 45821) =>
        new(DiscoveryPacketType.Announce, id, name, port, "1.0.0");

    [Fact]
    public void SameDeviceOnTwoInterfaces_IsOnePeer()
    {
        var id = Guid.NewGuid();

        Assert.Equal(PeerObservation.Added, _registry.Observe(Packet(id), Wifi, T0));
        Assert.Equal(PeerObservation.Updated, _registry.Observe(Packet(id), Ethernet, T0.AddSeconds(1)));
        Assert.Equal(PeerObservation.Refreshed, _registry.Observe(Packet(id), Wifi, T0.AddSeconds(2)));

        var peer = Assert.Single(_registry.Snapshot());
        Assert.Equal([Wifi, Ethernet], peer.Addresses);
        Assert.Equal(T0, peer.FirstSeenAt);
        Assert.Equal(T0.AddSeconds(2), peer.LastSeenAt);
        Assert.Equal(2, _changes);
    }

    [Fact]
    public void RenameAndPortChange_UpdateThePeer()
    {
        var id = Guid.NewGuid();
        _registry.Observe(Packet(id), Wifi, T0);

        Assert.Equal(PeerObservation.Updated, _registry.Observe(Packet(id, "Laptop", 50000), Wifi, T0.AddSeconds(5)));

        Assert.True(_registry.TryGet(id, out var peer));
        Assert.Equal("Laptop", peer.Username);
        Assert.Equal(50000, peer.TcpPort);
    }

    [Fact]
    public void Prune_ExpiresAddressesThenPeers()
    {
        var id = Guid.NewGuid();
        _registry.Observe(Packet(id), Wifi, T0);
        _registry.Observe(Packet(id), Ethernet, T0.AddSeconds(15));

        Assert.Equal(0, _registry.Prune(T0.AddSeconds(20)));
        Assert.Equal(2, _registry.Snapshot()[0].Addresses.Count);

        Assert.Equal(0, _registry.Prune(T0.AddSeconds(21)));
        Assert.Equal([Ethernet], _registry.Snapshot()[0].Addresses);

        _changes = 0;
        Assert.Equal(1, _registry.Prune(T0.AddSeconds(36)));
        Assert.Empty(_registry.Snapshot());
        Assert.Equal(1, _changes);
    }

    [Fact]
    public void Prune_WithNothingExpired_DoesNotRaiseChanged()
    {
        _registry.Observe(Packet(Guid.NewGuid()), Wifi, T0);
        _changes = 0;

        _registry.Prune(T0.AddSeconds(1));

        Assert.Equal(0, _changes);
    }

    [Fact]
    public void Full_RejectsNewDevicesButKeepsUpdatingKnownOnes()
    {
        var registry = new PeerRegistry(TimeSpan.FromSeconds(20), capacity: 2);
        var known = Guid.NewGuid();
        registry.Observe(Packet(known), Wifi, T0);
        registry.Observe(Packet(Guid.NewGuid()), Wifi, T0);

        Assert.Equal(PeerObservation.Rejected, registry.Observe(Packet(Guid.NewGuid()), Wifi, T0));
        Assert.Equal(PeerObservation.Refreshed, registry.Observe(Packet(known), Wifi, T0.AddSeconds(1)));
        Assert.Equal(2, registry.Count);
    }

    [Fact]
    public void Snapshot_SortsByUsername()
    {
        _registry.Observe(Packet(Guid.NewGuid(), "zed"), Wifi, T0);
        _registry.Observe(Packet(Guid.NewGuid(), "Alice"), Wifi, T0);

        Assert.Equal(["Alice", "zed"], _registry.Snapshot().Select(p => p.Username));
    }
}
