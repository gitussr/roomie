using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Roomie.Core.Connections;
using Roomie.Core.Discovery;
using Roomie.Core.Protocol;
using Roomie.Core.Trust;
using Roomie.Infrastructure.Identity;
using Roomie.Network.Connections;
using Roomie.Network.Discovery;

namespace Roomie.Tests.Connections;

/// <summary>Two real connection managers talking TLS over loopback, with real device identities.</summary>
public sealed class ConnectionManagerTests : IAsyncLifetime
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
    public async Task UnpairedDevices_ConnectWithMutualTls()
    {
        var alice = await StartAsync("Alice");
        var bob = await StartAsync("Bob");
        alice.Sees(bob);

        var info = await alice.Manager.ConnectAsync(bob.Id);

        Assert.Equal(PeerConnectionState.ConnectedUntrusted, info.State);
        Assert.Equal(bob.Store.Identity.CertificateFingerprint, info.RemoteFingerprint);
        Assert.Equal(SessionDirection.Outbound, info.Direction);
        Assert.StartsWith("TLS 1.", info.Security, StringComparison.Ordinal);

        await WaitUntil(() => bob.Manager.GetInfo(alice.Id).State == PeerConnectionState.ConnectedUntrusted);
        var seenByBob = bob.Manager.GetInfo(alice.Id);
        Assert.Equal(alice.Store.Identity.CertificateFingerprint, seenByBob.RemoteFingerprint);
        Assert.Equal(SessionDirection.Inbound, seenByBob.Direction);
    }

    [Fact]
    public async Task Disconnect_EndsTheSessionOnBothSides()
    {
        var alice = await StartAsync("Alice");
        var bob = await StartAsync("Bob");
        alice.Sees(bob);
        await alice.Manager.ConnectAsync(bob.Id);
        await WaitUntil(() => bob.Manager.GetInfo(alice.Id).IsConnected);

        await alice.Manager.DisconnectAsync(bob.Id);

        Assert.Equal(PeerConnectionState.Disconnected, alice.Manager.GetInfo(bob.Id).State);
        await WaitUntil(() => bob.Manager.GetInfo(alice.Id).State == PeerConnectionState.Disconnected);
        Assert.Equal("The other device disconnected.", bob.Manager.GetInfo(alice.Id).LastError);
    }

    [Fact]
    public async Task ConnectingToAnUndiscoveredDevice_FailsWithAReason()
    {
        var alice = await StartAsync("Alice");

        var info = await alice.Manager.ConnectAsync(Guid.NewGuid());

        Assert.Equal(PeerConnectionState.Failed, info.State);
        Assert.Contains("isn't on the network", info.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NothingListening_FailsWithAFirewallHint()
    {
        var alice = await StartAsync("Alice");
        var port = FreePort();
        alice.Registry.Observe(new DiscoveryPacket(DiscoveryPacketType.Announce, Guid.NewGuid(), "Ghost", port, "0.3.0"), IPAddress.Loopback, DateTimeOffset.UtcNow);

        var info = await alice.Manager.ConnectAsync(alice.Registry.Snapshot()[0].DeviceId);

        Assert.Equal(PeerConnectionState.Failed, info.State);
        Assert.Contains("refused the connection", info.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PairedDevices_ConnectAsTrusted()
    {
        var alice = await StartAsync("Alice");
        var bob = await StartAsync("Bob");
        alice.Trusts(bob);
        bob.Trusts(alice);
        alice.Sees(bob);

        var info = await alice.Manager.ConnectAsync(bob.Id);

        Assert.Equal(PeerConnectionState.ConnectedTrusted, info.State);
        await WaitUntil(() => bob.Manager.GetInfo(alice.Id).State == PeerConnectionState.ConnectedTrusted);
    }

    [Fact]
    public async Task ChangedCertificateOfAPairedDevice_IsRejected()
    {
        var alice = await StartAsync("Alice");
        var bob = await StartAsync("Bob");
        alice.Sees(bob);

        // Bob paired with "Alice" earlier, but under a different key: this is not that device.
        bob.Trust.Trust(new TrustedPeer(alice.Id, "Alice", "SHA256:" + new string('A', 64), DateTimeOffset.UtcNow));

        var info = await alice.Manager.ConnectAsync(bob.Id);

        Assert.Equal(PeerConnectionState.Failed, info.State);
        Assert.Contains("doesn't recognize this computer's identity", info.LastError, StringComparison.Ordinal);
        await WaitUntil(() => bob.Manager.GetInfo(alice.Id).State == PeerConnectionState.IdentityMismatch);
    }

    [Fact]
    public async Task PinnedFingerprintMismatch_OnOutboundConnect_IsRejected()
    {
        var alice = await StartAsync("Alice");
        var bob = await StartAsync("Bob");
        alice.Sees(bob);
        alice.Trust.Trust(new TrustedPeer(bob.Id, "Bob", "SHA256:" + new string('B', 64), DateTimeOffset.UtcNow));

        var info = await alice.Manager.ConnectAsync(bob.Id);

        Assert.Equal(PeerConnectionState.IdentityMismatch, info.State);
        Assert.Contains("different identity", info.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BlockedDevices_AreTurnedAway()
    {
        var alice = await StartAsync("Alice");
        var bob = await StartAsync("Bob");
        alice.Sees(bob);
        bob.Trust.Block(alice.Id);

        var info = await alice.Manager.ConnectAsync(bob.Id);

        Assert.Equal(PeerConnectionState.Failed, info.State);
        Assert.Equal("The other device has blocked this computer.", info.LastError);
        Assert.Equal(PeerConnectionState.Blocked, bob.Manager.GetInfo(alice.Id).State);
    }

    [Fact]
    public async Task SimultaneousConnects_LeaveOneSessionInitiatedByTheSmallerId()
    {
        var alice = await StartAsync("Alice");
        var bob = await StartAsync("Bob");
        alice.Sees(bob);
        bob.Sees(alice);

        await Task.WhenAll(alice.Manager.ConnectAsync(bob.Id), bob.Manager.ConnectAsync(alice.Id));

        var smaller = ConnectionManager.CompareIds(alice.Id, bob.Id) < 0 ? alice.Id : bob.Id;
        await WaitUntil(() =>
            alice.Manager.TryGetSession(bob.Id, out var a) && bob.Manager.TryGetSession(alice.Id, out var b)
            && a.InitiatorDeviceId == smaller && b.InitiatorDeviceId == smaller
            && a.RemoteEndPoint.Port != 0 && b.RemoteEndPoint.Port != 0);

        await Task.Delay(300);
        Assert.True(alice.Manager.GetInfo(bob.Id).IsConnected);
        Assert.True(bob.Manager.GetInfo(alice.Id).IsConnected);
    }

    [Fact]
    public async Task LostSessionWithAPairedDevice_ReconnectsAutomatically()
    {
        var alice = await StartAsync("Alice");
        var bob = await StartAsync("Bob");
        alice.Trusts(bob);
        bob.Trusts(alice);
        alice.Sees(bob);
        bob.Sees(alice);

        await alice.Manager.ConnectAsync(bob.Id);
        Assert.True(alice.Manager.TryGetSession(bob.Id, out var first));

        // Drop the connection without a GOODBYE, as when a cable is pulled.
        await first.DisposeAsync();

        await WaitUntil(() => alice.Manager.TryGetSession(bob.Id, out var next) && next != first
            && alice.Manager.GetInfo(bob.Id).State == PeerConnectionState.ConnectedTrusted);
    }

    [Fact]
    public async Task PeerThatGoesSilent_TimesOut()
    {
        var bob = await StartAsync("Bob");
        await using var raw = await RawPeer.ConnectAsync(CreateStore("Raw"), bob);

        await WaitUntil(() => bob.Manager.GetInfo(raw.Id).State == PeerConnectionState.ConnectedUntrusted);
        await WaitUntil(() => bob.Manager.GetInfo(raw.Id).State == PeerConnectionState.Disconnected);

        Assert.Contains("No response", bob.Manager.GetInfo(raw.Id).LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HeartbeatsAreAnswered()
    {
        var bob = await StartAsync("Bob");
        await using var raw = await RawPeer.ConnectAsync(CreateStore("Raw"), bob);

        await raw.SendAsync(RmpMessageTypes.Heartbeat, SessionPayloads.Empty);

        var reply = await raw.ReadUntilAsync(RmpMessageTypes.HeartbeatAck);
        Assert.Equal(bob.Id, reply.SenderId);
    }

    [Fact]
    public async Task UnpairedPeer_CannotSendChatMessages()
    {
        var bob = await StartAsync("Bob");
        var received = 0;
        bob.Manager.MessageReceived += (_, _) => Interlocked.Increment(ref received);
        await using var raw = await RawPeer.ConnectAsync(CreateStore("Raw"), bob);

        await raw.SendAsync(RmpMessageTypes.ChatMessage, SessionPayloads.ToElement(new { body = "hi" }));

        var error = await raw.ReadUntilAsync(RmpMessageTypes.Error);
        Assert.True(SessionPayloads.TryReadError(error.Payload, out var payload));
        Assert.Equal(RmpErrorCodes.PeerNotTrusted, payload.Code);
        Assert.Equal(0, received);
        Assert.True(bob.Manager.GetInfo(raw.Id).IsConnected);
    }

    [Fact]
    public async Task SpoofedSenderId_ClosesTheSession()
    {
        var bob = await StartAsync("Bob");
        await using var raw = await RawPeer.ConnectAsync(CreateStore("Raw"), bob);

        await raw.SendAsync(RmpMessageTypes.Heartbeat, SessionPayloads.Empty, senderId: Guid.NewGuid());

        var error = await raw.ReadUntilAsync(RmpMessageTypes.Error);
        Assert.True(SessionPayloads.TryReadError(error.Payload, out var payload));
        Assert.Equal(RmpErrorCodes.InvalidFrame, payload.Code);
        await WaitUntil(() => bob.Manager.GetInfo(raw.Id).State == PeerConnectionState.Disconnected);
    }

    [Fact]
    public async Task OversizedFrame_ClosesTheSession()
    {
        var bob = await StartAsync("Bob");
        await using var raw = await RawPeer.ConnectAsync(CreateStore("Raw"), bob);

        // A length prefix of 16 MB: rejected from the header alone, before anything is allocated.
        await raw.WriteRawAsync([0x01, 0x00, 0x00, 0x00, 0x01]);

        var error = await raw.ReadUntilAsync(RmpMessageTypes.Error);
        Assert.True(SessionPayloads.TryReadError(error.Payload, out var payload));
        Assert.Equal(RmpErrorCodes.MessageTooLarge, payload.Code);
    }

    [Fact]
    public async Task Shutdown_SaysGoodbye()
    {
        var alice = await StartAsync("Alice");
        var bob = await StartAsync("Bob");
        alice.Sees(bob);
        await alice.Manager.ConnectAsync(bob.Id);
        await WaitUntil(() => bob.Manager.GetInfo(alice.Id).IsConnected);

        await alice.Manager.StopAsync(CancellationToken.None);

        await WaitUntil(() => bob.Manager.GetInfo(alice.Id).State == PeerConnectionState.Disconnected);
        Assert.Equal("The other device disconnected.", bob.Manager.GetInfo(alice.Id).LastError);
    }

    private DeviceIdentityStore CreateStore(string username)
    {
        var store = new DeviceIdentityStore(Path.Combine(_root, Guid.NewGuid().ToString("N")));
        store.LoadOrCreate(username);
        return store;
    }

    private async Task<Node> StartAsync(string username)
    {
        var store = CreateStore(username);
        var registry = new PeerRegistry(TimeSpan.FromMinutes(1));
        var trust = new InMemoryTrustStore();
        var options = new ConnectionOptions
        {
            Port = 0,
            AppVersion = "0.3.0",
            HeartbeatInterval = TimeSpan.FromMilliseconds(100),
            MissedHeartbeatThreshold = 3,
            ReconnectDelays = [TimeSpan.FromMilliseconds(50)],
            LargerIdDialDelay = TimeSpan.FromMilliseconds(500),
            MaintenanceInterval = TimeSpan.FromMilliseconds(20),
        };

        var manager = new ConnectionManager(store, store, new StaticInterfaces(), registry, trust, options, TimeProvider.System, NullLogger<ConnectionManager>.Instance);
        var node = new Node(store, registry, trust, manager);
        _nodes.Add(node);

        await manager.StartAsync(CancellationToken.None);
        await WaitUntil(() => manager.ListeningEndPoints.Count == 1);
        return node;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
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

    private sealed class Node(DeviceIdentityStore store, PeerRegistry registry, InMemoryTrustStore trust, ConnectionManager manager) : IAsyncDisposable
    {
        public DeviceIdentityStore Store { get; } = store;

        public PeerRegistry Registry { get; } = registry;

        public InMemoryTrustStore Trust { get; } = trust;

        public ConnectionManager Manager { get; } = manager;

        public Guid Id => Store.Identity.DeviceId;

        public int Port => Manager.ListeningEndPoints[0].Port;

        /// <summary>Simulates having received the other node's discovery announcement.</summary>
        public void Sees(Node other) => Registry.Observe(
            new DiscoveryPacket(DiscoveryPacketType.Announce, other.Id, other.Store.Identity.Username, other.Port, "0.3.0"),
            IPAddress.Loopback,
            DateTimeOffset.UtcNow);

        public void Trusts(Node other) => Trust.Trust(new TrustedPeer(
            other.Id, other.Store.Identity.Username, other.Store.Identity.CertificateFingerprint, DateTimeOffset.UtcNow));

        public async ValueTask DisposeAsync()
        {
            await Manager.StopAsync(CancellationToken.None);
            Manager.Dispose();
            Store.Dispose();
        }
    }

    /// <summary>A hand-driven peer for sending things a well-behaved Roomie never would.</summary>
    private sealed class RawPeer(DeviceIdentityStore store, SslStream ssl, Guid remoteId) : IAsyncDisposable
    {
        public Guid Id => store.Identity.DeviceId;

        public static async Task<RawPeer> ConnectAsync(DeviceIdentityStore store, Node target)
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, target.Port);
            var credentials = SslStreamCertificateContext.Create(store.TlsCertificate, null, offline: true);
            var (ssl, _) = await TlsHandshake.AuthenticateAsClientAsync(client.GetStream(), credentials, target.Id, TimeProvider.System, CancellationToken.None);

            var peer = new RawPeer(store, ssl, target.Id);
            await peer.SendAsync(RmpMessageTypes.Hello, SessionPayloads.ToElement(new HelloPayload(1, 1, "0.3.0", [])));
            await peer.ReadUntilAsync(RmpMessageTypes.Hello);
            return peer;
        }

        public async Task SendAsync(string type, System.Text.Json.JsonElement payload, Guid? senderId = null)
        {
            var envelope = RmpEnvelope.Create(type, senderId ?? Id, remoteId, DateTimeOffset.UtcNow, payload);
            await WriteRawAsync(RmpFraming.Encode(RmpFrameType.Control, RmpEnvelopeCodec.Encode(envelope)));
        }

        public async Task WriteRawAsync(byte[] data)
        {
            await ssl.WriteAsync(data);
            await ssl.FlushAsync();
        }

        public async Task<RmpEnvelope> ReadUntilAsync(string type)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                var frame = await RmpFraming.ReadAsync(ssl, timeout.Token) ?? throw new EndOfStreamException();
                Assert.True(RmpEnvelopeCodec.TryDecode(frame.Payload, out var envelope, out _));
                if (envelope.Type == type)
                {
                    return envelope;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await ssl.DisposeAsync();
            store.Dispose();
        }
    }
}
