using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Roomie.Core.Connections;
using Roomie.Core.Discovery;
using Roomie.Core.Trust;
using Roomie.Infrastructure.Identity;
using Roomie.Network.Connections;
using Roomie.Network.Discovery;
using Roomie.Network.Pairing;

namespace Roomie.Tests.Pairing;

/// <summary>Full pairing between real connection managers over loopback TLS.</summary>
public sealed class PairingServiceTests : IAsyncLifetime
{
    private static readonly LanInterface Loopback = new("lo", "Loopback", new Ipv4Subnet(IPAddress.Loopback, 8));

    private readonly string _root = Path.Combine(Path.GetTempPath(), "roomie-tests", Guid.NewGuid().ToString("N"));
    private readonly List<Node> _nodes = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var node in _nodes)
        {
            await node.DisposeAsync();
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task BothApprove_PairsBothDevices()
    {
        var (alice, bob) = await TwoNodesAsync();

        var started = await alice.Pairing.StartAsync(bob.Id);
        Assert.Equal(PairingStage.Requesting, started.Stage);

        await WaitUntil(() => alice.Stage == PairingStage.Comparing && bob.Stage == PairingStage.Comparing);
        Assert.Equal(alice.Pairing.Current!.Code, bob.Pairing.Current!.Code);
        Assert.Equal(PairingRole.Initiator, alice.Pairing.Current.Role);
        Assert.Equal(PairingRole.Responder, bob.Pairing.Current.Role);
        Assert.Null(alice.Trust.FindTrusted(bob.Id));

        alice.Pairing.Approve();
        await WaitUntil(() => alice.Stage == PairingStage.WaitingForPeer);
        Assert.Null(alice.Trust.FindTrusted(bob.Id));

        bob.Pairing.Approve();
        await WaitUntil(() => alice.Stage == PairingStage.Paired && bob.Stage == PairingStage.Paired);

        Assert.Equal(bob.Store.Identity.CertificateFingerprint, alice.Trust.FindTrusted(bob.Id)!.CertificateFingerprint);
        Assert.Equal(alice.Store.Identity.CertificateFingerprint, bob.Trust.FindTrusted(alice.Id)!.CertificateFingerprint);
        await WaitUntil(() => alice.Connections.GetInfo(bob.Id).State == PeerConnectionState.ConnectedTrusted
            && bob.Connections.GetInfo(alice.Id).State == PeerConnectionState.ConnectedTrusted);
    }

    [Fact]
    public async Task ApprovalOnOneSideOnly_PairsNeither()
    {
        var (alice, bob) = await TwoNodesAsync();
        await alice.Pairing.StartAsync(bob.Id);
        await WaitUntil(() => alice.Stage == PairingStage.Comparing && bob.Stage == PairingStage.Comparing);

        alice.Pairing.Approve();
        bob.Pairing.Cancel();

        await WaitUntil(() => alice.Stage == PairingStage.Declined && bob.Stage == PairingStage.Declined);
        Assert.Null(alice.Trust.FindTrusted(bob.Id));
        Assert.Null(bob.Trust.FindTrusted(alice.Id));
        Assert.Contains("didn't confirm the code", alice.Pairing.Current!.Message, StringComparison.Ordinal);
        Assert.Equal(PeerConnectionState.ConnectedUntrusted, alice.Connections.GetInfo(bob.Id).State);
    }

    [Fact]
    public async Task PairingStartedWhileDisconnected_ConnectsFirst()
    {
        var (alice, bob) = await TwoNodesAsync(connect: false);

        await alice.Pairing.StartAsync(bob.Id);

        await WaitUntil(() => bob.Stage == PairingStage.Comparing);
        Assert.True(alice.Connections.GetInfo(bob.Id).IsConnected);
    }

    [Fact]
    public async Task BothPressPair_AtTheSameTime_OneAttemptWins()
    {
        var (alice, bob) = await TwoNodesAsync();

        await Task.WhenAll(alice.Pairing.StartAsync(bob.Id), bob.Pairing.StartAsync(alice.Id));

        await WaitUntil(() => alice.Stage == PairingStage.Comparing && bob.Stage == PairingStage.Comparing);
        Assert.Equal(alice.Pairing.Current!.PairingId, bob.Pairing.Current!.PairingId);
        Assert.Equal(alice.Pairing.Current.Code, bob.Pairing.Current.Code);

        var smaller = ConnectionManager.CompareIds(alice.Id, bob.Id) < 0 ? alice : bob;
        Assert.Equal(PairingRole.Initiator, smaller.Pairing.Current!.Role);
    }

    [Fact]
    public async Task Block_FromAnIncomingRequest_EndsTheSession()
    {
        var (alice, bob) = await TwoNodesAsync();
        await alice.Pairing.StartAsync(bob.Id);
        await WaitUntil(() => bob.Stage == PairingStage.Comparing);

        bob.Pairing.Block();

        Assert.True(bob.Trust.IsBlocked(alice.Id));
        await WaitUntil(() => !alice.Connections.GetInfo(bob.Id).IsConnected);
        Assert.Null(alice.Trust.FindTrusted(bob.Id));
        Assert.False(alice.Pairing.Current!.IsActive);
    }

    [Fact]
    public async Task SecondDevice_IsToldItsBusy()
    {
        var (alice, bob) = await TwoNodesAsync();
        var carol = await StartAsync("Carol");
        carol.Sees(bob);

        await alice.Pairing.StartAsync(bob.Id);
        await WaitUntil(() => bob.Stage == PairingStage.Comparing);

        await carol.Pairing.StartAsync(bob.Id);

        await WaitUntil(() => carol.Stage == PairingStage.Failed);
        Assert.Contains("busy", carol.Pairing.Current!.Message, StringComparison.Ordinal);
        Assert.Equal(alice.Id, bob.Pairing.Current!.DeviceId);
    }

    [Fact]
    public async Task ForgettingAPairedDevice_EndsItsTrustedSession()
    {
        var (alice, bob) = await TwoNodesAsync();
        await alice.Pairing.StartAsync(bob.Id);
        await WaitUntil(() => alice.Stage == PairingStage.Comparing && bob.Stage == PairingStage.Comparing);
        alice.Pairing.Approve();
        bob.Pairing.Approve();
        await WaitUntil(() => alice.Connections.GetInfo(bob.Id).State == PeerConnectionState.ConnectedTrusted);

        alice.Trust.Remove(bob.Id);

        await WaitUntil(() => alice.Connections.GetInfo(bob.Id).State == PeerConnectionState.Disconnected);
    }

    [Fact]
    public async Task RepeatedFailures_RequireANewSession()
    {
        var (alice, bob) = await TwoNodesAsync();

        for (var i = 0; i < 3; i++)
        {
            await WaitUntil(() => alice.Connections.TryGetSession(bob.Id, out _));
            await alice.Pairing.StartAsync(bob.Id);
            await WaitUntil(() => alice.Stage == PairingStage.Comparing);
            alice.Pairing.Cancel();
            await WaitUntil(() => bob.Pairing.Current is { IsActive: false });
        }

        await WaitUntil(() => !alice.Connections.GetInfo(bob.Id).IsConnected);
    }

    private async Task<(Node Alice, Node Bob)> TwoNodesAsync(bool connect = true)
    {
        var alice = await StartAsync("Alice");
        var bob = await StartAsync("Bob");
        alice.Sees(bob);
        bob.Sees(alice);
        if (connect)
        {
            await alice.Connections.ConnectAsync(bob.Id);
            await WaitUntil(() => bob.Connections.GetInfo(alice.Id).IsConnected);
        }

        return (alice, bob);
    }

    private async Task<Node> StartAsync(string username)
    {
        var store = new DeviceIdentityStore(Path.Combine(_root, Guid.NewGuid().ToString("N")));
        store.LoadOrCreate(username);
        var registry = new PeerRegistry(TimeSpan.FromMinutes(1));
        var trust = new InMemoryTrustStore();
        var connections = new ConnectionManager(store, store, new StaticInterfaces(), registry, trust,
            new ConnectionOptions { Port = 0, AppVersion = "0.4.0", MaintenanceInterval = TimeSpan.FromMilliseconds(20) },
            TimeProvider.System, NullLogger<ConnectionManager>.Instance);
        var pairing = new PairingService(connections, trust, registry, store, store, new PairingOptions(), TimeProvider.System, NullLogger<PairingService>.Instance);

        var node = new Node(store, registry, trust, connections, pairing);
        _nodes.Add(node);
        await connections.StartAsync(CancellationToken.None);
        await WaitUntil(() => connections.ListeningEndPoints.Count == 1);
        return node;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(10))
            {
                Assert.Fail("Timed out waiting for the condition.");
            }

            await Task.Delay(20);
        }
    }

    private sealed class StaticInterfaces : ILanInterfaceProvider
    {
        public event EventHandler? InterfacesChanged
        {
            add { }
            remove { }
        }

        public IReadOnlyList<LanInterface> GetEligibleInterfaces() => [Loopback];
    }

    private sealed class Node(DeviceIdentityStore store, PeerRegistry registry, InMemoryTrustStore trust, ConnectionManager connections, PairingService pairing) : IAsyncDisposable
    {
        public DeviceIdentityStore Store { get; } = store;

        public PeerRegistry Registry { get; } = registry;

        public InMemoryTrustStore Trust { get; } = trust;

        public ConnectionManager Connections { get; } = connections;

        public PairingService Pairing { get; } = pairing;

        public Guid Id => Store.Identity.DeviceId;

        public PairingStage? Stage => Pairing.Current?.Stage;

        public void Sees(Node other) => Registry.Observe(
            new DiscoveryPacket(DiscoveryPacketType.Announce, other.Id, other.Store.Identity.Username, other.Connections.ListeningEndPoints[0].Port, "0.4.0"),
            IPAddress.Loopback,
            DateTimeOffset.UtcNow);

        public async ValueTask DisposeAsync()
        {
            Pairing.Dispose();
            await Connections.StopAsync(CancellationToken.None);
            Connections.Dispose();
            Store.Dispose();
        }
    }
}
