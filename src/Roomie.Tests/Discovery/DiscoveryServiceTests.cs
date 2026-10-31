using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Roomie.Core.Discovery;
using Roomie.Core.Identity;
using Roomie.Network.Discovery;

namespace Roomie.Tests.Discovery;

/// <summary>Runs real <see cref="DiscoveryService"/> instances against an in-memory LAN.</summary>
public sealed class DiscoveryServiceTests : IAsyncLifetime
{
    private const int Port = 45820;

    private static readonly Ipv4Subnet HomeNetwork = new(IPAddress.Parse("192.168.1.0"), 24);

    private readonly FakeLan _lan = new();
    private readonly List<DiscoveryService> _services = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var service in _services)
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
    }

    [Fact]
    public async Task TwoComputers_DiscoverEachOtherWithoutWaitingForAnnouncements()
    {
        var alice = await StartAsync("Alice", "192.168.1.10");
        var bob = await StartAsync("Bob", "192.168.1.20");

        // Alice hears Bob's startup announcement; Bob learns about Alice from her response to his request.
        await WaitUntil(() => alice.Registry.Count == 1 && bob.Registry.Count == 1);

        var seenByBob = Assert.Single(bob.Registry.Snapshot());
        Assert.Equal(alice.Identity.DeviceId, seenByBob.DeviceId);
        Assert.Equal("Alice", seenByBob.Username);
        Assert.Equal([IPAddress.Parse("192.168.1.10")], seenByBob.Addresses);
        Assert.Equal(DiscoveryState.Running, alice.Service.Status.State);
    }

    [Fact]
    public async Task OwnBroadcasts_AreIgnored()
    {
        var alice = await StartAsync("Alice", "192.168.1.10", announcement: TimeSpan.FromMilliseconds(50));

        await Task.Delay(300);

        Assert.True(_lan.Delivered > 0);
        Assert.Equal(0, alice.Registry.Count);
    }

    [Fact]
    public async Task PacketsFromOutsideTheInterfaceSubnet_AreIgnored()
    {
        var alice = await StartAsync("Alice", "192.168.1.10");
        var stranger = _lan.Raw(IPAddress.Parse("10.9.9.9"));

        await stranger.SendAsync(Encode(DiscoveryPacketType.Announce, Guid.NewGuid(), "Stranger"), new IPEndPoint(IPAddress.Parse("192.168.1.10"), Port), default);
        await Task.Delay(200);

        Assert.Equal(0, alice.Registry.Count);
    }

    [Fact]
    public async Task MalformedPackets_AreIgnored()
    {
        var alice = await StartAsync("Alice", "192.168.1.10");
        var peer = _lan.Raw(IPAddress.Parse("192.168.1.50"));

        await peer.SendAsync("{\"protocol\":\"roomie\"}"u8.ToArray(), new IPEndPoint(IPAddress.Parse("192.168.1.10"), Port), default);
        await Task.Delay(200);

        Assert.Equal(0, alice.Registry.Count);
        Assert.Equal(DiscoveryState.Running, alice.Service.Status.State);
    }

    [Fact]
    public async Task Requests_AreAnsweredAtMostOncePerTwoSecondsPerRequester()
    {
        await StartAsync("Alice", "192.168.1.10");
        var requester = _lan.Raw(IPAddress.Parse("192.168.1.50"));
        var request = Encode(DiscoveryPacketType.Request, Guid.NewGuid(), "Requester");
        var alice = new IPEndPoint(IPAddress.Parse("192.168.1.10"), Port);

        for (var i = 0; i < 5; i++)
        {
            await requester.SendAsync(request, alice, default);
        }

        var responses = await requester.CollectAsync(TimeSpan.FromSeconds(1), DiscoveryPacketType.Response);

        var response = Assert.Single(responses);
        Assert.Equal("Alice", response.Username);
    }

    [Fact]
    public async Task SilentPeers_ExpireAfterTheOfflineTimeout()
    {
        var alice = await StartAsync("Alice", "192.168.1.10", offlineTimeout: TimeSpan.FromMilliseconds(400));
        var peer = _lan.Raw(IPAddress.Parse("192.168.1.50"));

        await peer.SendAsync(Encode(DiscoveryPacketType.Announce, Guid.NewGuid(), "Bob"), new IPEndPoint(IPAddress.Parse("192.168.1.10"), Port), default);
        await WaitUntil(() => alice.Registry.Count == 1);
        await WaitUntil(() => alice.Registry.Count == 0);
    }

    [Fact]
    public async Task AnotherComputerUsingOurDeviceId_IsNotAddedAsAPeer()
    {
        var alice = await StartAsync("Alice", "192.168.1.10");
        var clone = _lan.Raw(IPAddress.Parse("192.168.1.66"));

        await clone.SendAsync(Encode(DiscoveryPacketType.Announce, alice.Identity.DeviceId, "Alice"), new IPEndPoint(IPAddress.Parse("192.168.1.10"), Port), default);
        await Task.Delay(200);

        Assert.Equal(0, alice.Registry.Count);
    }

    [Fact]
    public async Task PortInUse_IsReportedAsAProblem()
    {
        _lan.FailBind(IPAddress.Parse("192.168.1.10"), SocketError.AddressAlreadyInUse);

        var alice = await StartAsync("Alice", "192.168.1.10");
        await WaitUntil(() => alice.Service.Status.State == DiscoveryState.Failed);

        Assert.Contains("already in use", Assert.Single(alice.Service.Status.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoEligibleInterfaces_ReportsNoNetwork()
    {
        var alice = await StartAsync("Alice", interfaces: []);

        await WaitUntil(() => alice.Service.Status.State == DiscoveryState.NoNetwork);
    }

    [Fact]
    public async Task NetworkChange_OpensNewInterfacesAndClosesRemovedOnes()
    {
        var alice = await StartAsync("Alice", interfaces: []);
        await WaitUntil(() => alice.Service.Status.State == DiscoveryState.NoNetwork);

        var wifi = Interface("192.168.1.10");
        alice.Interfaces.Set([wifi]);
        await WaitUntil(() => alice.Service.Status.State == DiscoveryState.Running);
        Assert.Equal([wifi], alice.Service.Status.Interfaces);

        // Joining the network sends a request, so an existing peer answers straight away.
        var bob = await StartAsync("Bob", "192.168.1.20");
        await WaitUntil(() => alice.Registry.Count == 1 && bob.Registry.Count == 1);

        alice.Interfaces.Set([]);
        await WaitUntil(() => alice.Service.Status.State == DiscoveryState.NoNetwork);
        Assert.False(_lan.IsBound(wifi.Address));
    }

    [Fact]
    public async Task AnnounceNow_PropagatesARename()
    {
        var alice = await StartAsync("Alice", "192.168.1.10");
        var bob = await StartAsync("Bob", "192.168.1.20");
        await WaitUntil(() => bob.Registry.Count == 1);

        alice.Identity = alice.Identity with { Username = "Alicia" };
        alice.Service.AnnounceNow();

        await WaitUntil(() => bob.Registry.Snapshot().Single().Username == "Alicia");
    }

    private async Task<Node> StartAsync(
        string username,
        string? address = null,
        IReadOnlyList<LanInterface>? interfaces = null,
        TimeSpan? announcement = null,
        TimeSpan? offlineTimeout = null)
    {
        var node = new Node(username, interfaces ?? [Interface(address!)], offlineTimeout ?? TimeSpan.FromSeconds(20));
        var options = new DiscoveryOptions
        {
            DiscoveryPort = Port,
            AppVersion = "0.2.0",
            AnnouncementInterval = announcement ?? TimeSpan.FromHours(1),
            MaintenanceInterval = TimeSpan.FromMilliseconds(20),
            NetworkChangeDebounce = TimeSpan.FromMilliseconds(20),
            InterfaceRefreshInterval = TimeSpan.FromHours(1),
        };

        node.Service = new DiscoveryService(node, node.Interfaces, _lan, node.Registry, options, TimeProvider.System, NullLogger<DiscoveryService>.Instance);
        _services.Add(node.Service);
        await node.Service.StartAsync(CancellationToken.None);

        // The loop runs on the thread pool; wait until its sockets are open so tests don't race it.
        await WaitUntil(() => node.Service.Status.State != DiscoveryState.Starting);
        return node;
    }

    private static LanInterface Interface(string address) =>
        new("eth0", "Ethernet", new Ipv4Subnet(IPAddress.Parse(address), HomeNetwork.PrefixLength));

    private static byte[] Encode(DiscoveryPacketType type, Guid id, string username) =>
        DiscoveryPacketCodec.Encode(new DiscoveryPacket(type, id, username, 45821, "0.2.0"));

    private static async Task WaitUntil(Func<bool> condition)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(5))
            {
                Assert.Fail("Timed out waiting for the condition.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class Node(string username, IReadOnlyList<LanInterface> interfaces, TimeSpan offlineTimeout) : ILocalIdentityProvider
    {
        public DeviceIdentity Identity { get; set; } =
            new(Guid.NewGuid(), username, "SHA256:00", "AA==", DateTimeOffset.UtcNow, DeviceIdentity.CurrentVersion);

        public FakeInterfaces Interfaces { get; } = new(interfaces);

        public PeerRegistry Registry { get; } = new(offlineTimeout);

        public DiscoveryService Service { get; set; } = null!;
    }

    private sealed class FakeInterfaces(IReadOnlyList<LanInterface> initial) : ILanInterfaceProvider
    {
        private volatile IReadOnlyList<LanInterface> _current = initial;

        public event EventHandler? InterfacesChanged;

        public IReadOnlyList<LanInterface> GetEligibleInterfaces() => _current;

        public void Set(IReadOnlyList<LanInterface> interfaces)
        {
            _current = interfaces;
            InterfacesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Delivers datagrams by destination address; a subnet broadcast reaches every channel in the subnet.</summary>
    private sealed class FakeLan : IDiscoveryChannelFactory
    {
        private readonly Lock _gate = new();
        private readonly List<FakeChannel> _channels = [];
        private readonly Dictionary<IPAddress, SocketError> _bindFailures = [];
        private int _delivered;

        public int Delivered => Volatile.Read(ref _delivered);

        public IDiscoveryChannel Open(LanInterface lanInterface, int port)
        {
            lock (_gate)
            {
                if (_bindFailures.TryGetValue(lanInterface.Address, out var error))
                {
                    throw new SocketException((int)error);
                }

                var channel = new FakeChannel(this, lanInterface, port);
                _channels.Add(channel);
                return channel;
            }
        }

        public FakeChannel Raw(IPAddress address) =>
            (FakeChannel)Open(new LanInterface("raw", "Raw", new Ipv4Subnet(address, HomeNetwork.PrefixLength)), Port);

        public void FailBind(IPAddress address, SocketError error)
        {
            lock (_gate)
            {
                _bindFailures[address] = error;
            }
        }

        public bool IsBound(IPAddress address)
        {
            lock (_gate)
            {
                return _channels.Any(c => c.LanInterface.Address.Equals(address));
            }
        }

        public void Remove(FakeChannel channel)
        {
            lock (_gate)
            {
                _channels.Remove(channel);
            }
        }

        public void Deliver(FakeChannel from, byte[] data, IPEndPoint destination)
        {
            List<FakeChannel> targets;
            lock (_gate)
            {
                targets = _channels.Where(c => c.Port == destination.Port
                    && (c.LanInterface.Address.Equals(destination.Address)
                        || (c.LanInterface.Subnet.BroadcastAddress.Equals(destination.Address)
                            && c.LanInterface.Subnet.Contains(from.LanInterface.Address)))).ToList();
            }

            foreach (var target in targets)
            {
                Interlocked.Increment(ref _delivered);
                target.Inbox.Writer.TryWrite(new DiscoveryDatagram(data, new IPEndPoint(from.LanInterface.Address, from.Port)));
            }
        }
    }

    private sealed class FakeChannel(FakeLan lan, LanInterface lanInterface, int port) : IDiscoveryChannel
    {
        public Channel<DiscoveryDatagram> Inbox { get; } = Channel.CreateUnbounded<DiscoveryDatagram>();

        public LanInterface LanInterface { get; } = lanInterface;

        public int Port { get; } = port;

        public ValueTask SendAsync(ReadOnlyMemory<byte> data, IPEndPoint destination, CancellationToken cancellationToken)
        {
            lan.Deliver(this, data.ToArray(), destination);
            return ValueTask.CompletedTask;
        }

        public async ValueTask<DiscoveryDatagram> ReceiveAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await Inbox.Reader.ReadAsync(cancellationToken);
            }
            catch (ChannelClosedException)
            {
                throw new ObjectDisposedException(nameof(FakeChannel));
            }
        }

        public async Task<List<DiscoveryPacket>> CollectAsync(TimeSpan duration, DiscoveryPacketType type)
        {
            var packets = new List<DiscoveryPacket>();
            using var timeout = new CancellationTokenSource(duration);
            try
            {
                while (true)
                {
                    var datagram = await Inbox.Reader.ReadAsync(timeout.Token);
                    if (DiscoveryPacketCodec.TryDecode(datagram.Data.Span, out var packet, out _) && packet.Type == type)
                    {
                        packets.Add(packet);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return packets;
            }
        }

        public void Dispose()
        {
            lan.Remove(this);
            Inbox.Writer.TryComplete();
        }
    }
}
