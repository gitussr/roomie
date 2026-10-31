using System.Collections.Frozen;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Roomie.Core.Configuration;
using Roomie.Core.Discovery;
using Roomie.Core.Identity;

namespace Roomie.Network.Discovery;

public sealed class DiscoveryOptions
{
    public int DiscoveryPort { get; init; } = NetworkDefaults.DiscoveryUdpPort;
    public int MessagingTcpPort { get; init; } = NetworkDefaults.MessagingTcpPort;
    public string AppVersion { get; init; } = "0.0.0";
    public TimeSpan AnnouncementInterval { get; init; } = NetworkDefaults.AnnouncementInterval;
    public TimeSpan ResponseMaxDelay { get; init; } = NetworkDefaults.DiscoveryResponseMaxDelay;
    public TimeSpan ResponseMinInterval { get; init; } = NetworkDefaults.DiscoveryResponseMinInterval;

    /// <summary>How often expired peers are pruned and timers checked.</summary>
    public TimeSpan MaintenanceInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Network-change events arrive in bursts; wait this long for them to settle.</summary>
    public TimeSpan NetworkChangeDebounce { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Re-checks adapters even without a change event, and retries ports that failed to bind.</summary>
    public TimeSpan InterfaceRefreshInterval { get; init; } = TimeSpan.FromSeconds(10);
}

public enum DiscoveryState
{
    Starting,

    /// <summary>Listening on at least one interface. <see cref="DiscoveryStatus.Problems"/> may still list others.</summary>
    Running,

    /// <summary>No active adapter has a private or link-local IPv4 address.</summary>
    NoNetwork,

    /// <summary>Eligible adapters exist but none could be used, e.g. the port is in use.</summary>
    Failed,

    Stopped,
}

public sealed record DiscoveryStatus(DiscoveryState State, IReadOnlyList<LanInterface> Interfaces, IReadOnlyList<string> Problems)
{
    public static DiscoveryStatus Initial { get; } = new(DiscoveryState.Starting, [], []);
}

/// <summary>
/// UDP discovery (architecture.md §2): one socket per eligible LAN interface; a request at startup and
/// after network changes; an announcement every 5 seconds; rate-limited, randomly delayed unicast
/// responses. Discovered devices go into the <see cref="PeerRegistry"/>, which is all this service
/// does with them: discovery never establishes connection or trust.
/// </summary>
public sealed partial class DiscoveryService : BackgroundService
{
    private static readonly TimeSpan DuplicateIdWarningInterval = TimeSpan.FromMinutes(1);

    private readonly ILocalIdentityProvider _identity;
    private readonly ILanInterfaceProvider _interfaces;
    private readonly IDiscoveryChannelFactory _channelFactory;
    private readonly PeerRegistry _registry;
    private readonly DiscoveryOptions _options;
    private readonly ILogger<DiscoveryService> _logger;
    private readonly TimeProvider _time;
    private readonly ResponseRateLimiter _rateLimiter;

    // Owned by the RunAsync loop; never touched concurrently.
    private readonly Dictionary<string, ChannelState> _channels = [];
    private string _lastProblems = string.Empty;

    private volatile FrozenSet<IPAddress> _localAddresses = FrozenSet<IPAddress>.Empty;
    private volatile DiscoveryStatus _status = DiscoveryStatus.Initial;
    private int _networkChangePending;
    private long _networkChangedAtTicks;
    private int _announceRequested;
    private long _lastDuplicateWarningTicks;

    public DiscoveryService(
        ILocalIdentityProvider identity,
        ILanInterfaceProvider interfaces,
        IDiscoveryChannelFactory channelFactory,
        PeerRegistry registry,
        DiscoveryOptions options,
        TimeProvider time,
        ILogger<DiscoveryService> logger)
    {
        _identity = identity;
        _interfaces = interfaces;
        _channelFactory = channelFactory;
        _registry = registry;
        _options = options;
        _time = time;
        _logger = logger;
        _rateLimiter = new ResponseRateLimiter(time, options.ResponseMinInterval);
    }

    /// <summary>Raised on the discovery thread whenever <see cref="Status"/> changes.</summary>
    public event EventHandler? StatusChanged;

    public DiscoveryStatus Status => _status;

    /// <summary>Sends an announcement at the next maintenance tick, e.g. after the username changed.</summary>
    public void AnnounceNow() => Interlocked.Exchange(ref _announceRequested, 1);

    // Run on the thread pool: the host is started from the WPF UI thread, and the discovery loop must not
    // capture its synchronization context.
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Run(() => RunAsync(stoppingToken), stoppingToken);

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            DiscoveryPacketCodec.Encode(BuildPacket(DiscoveryPacketType.Announce));
        }
        catch (ArgumentException ex)
        {
            LogInvalidLocalPacket(_logger, ex);
            SetStatus(new DiscoveryStatus(DiscoveryState.Failed, [], ["Roomie's own discovery information is invalid. See the log."]));
            return;
        }

        _interfaces.InterfacesChanged += OnInterfacesChanged;
        try
        {
            await ReconcileAsync(requestOnAll: false, cancellationToken).ConfigureAwait(false);

            var now = _time.GetUtcNow();
            var nextAnnounce = now + _options.AnnouncementInterval;
            var nextRefresh = now + _options.InterfaceRefreshInterval;

            using var timer = new PeriodicTimer(_options.MaintenanceInterval, _time);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                now = _time.GetUtcNow();

                var changedAt = new DateTimeOffset(Interlocked.Read(ref _networkChangedAtTicks), TimeSpan.Zero);
                if (Volatile.Read(ref _networkChangePending) == 1 && now - changedAt >= _options.NetworkChangeDebounce)
                {
                    Interlocked.Exchange(ref _networkChangePending, 0);
                    LogNetworkChanged(_logger);
                    await ReconcileAsync(requestOnAll: true, cancellationToken).ConfigureAwait(false);
                    nextRefresh = now + _options.InterfaceRefreshInterval;
                }
                else if (now >= nextRefresh)
                {
                    await ReconcileAsync(requestOnAll: false, cancellationToken).ConfigureAwait(false);
                    nextRefresh = now + _options.InterfaceRefreshInterval;
                }

                if (Interlocked.Exchange(ref _announceRequested, 0) == 1 || now >= nextAnnounce)
                {
                    await BroadcastAsync(DiscoveryPacketType.Announce, _channels.Values, cancellationToken).ConfigureAwait(false);
                    nextAnnounce = now + _options.AnnouncementInterval;
                }

                _registry.Prune(now);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _interfaces.InterfacesChanged -= OnInterfacesChanged;
            await CloseAllAsync().ConfigureAwait(false);
            SetStatus(new DiscoveryStatus(DiscoveryState.Stopped, [], []));
        }
    }

    private void OnInterfacesChanged(object? sender, EventArgs e)
    {
        Interlocked.Exchange(ref _networkChangedAtTicks, _time.GetUtcNow().UtcTicks);
        Interlocked.Exchange(ref _networkChangePending, 1);
    }

    /// <summary>Opens channels for new interfaces, closes channels for vanished ones, and updates the status.</summary>
    private async Task ReconcileAsync(bool requestOnAll, CancellationToken cancellationToken)
    {
        IReadOnlyList<LanInterface> wanted;
        try
        {
            wanted = _interfaces.GetEligibleInterfaces();
        }
        catch (NetworkInformationException ex)
        {
            LogInterfaceEnumerationFailed(_logger, ex);
            return;
        }

        var wantedByKey = wanted.GroupBy(i => i.Key).ToDictionary(g => g.Key, g => g.First());

        foreach (var key in _channels.Keys.Where(key => !wantedByKey.ContainsKey(key)).ToList())
        {
            var state = _channels[key];
            _channels.Remove(key);
            LogInterfaceClosed(_logger, state.Channel.LanInterface);
            await CloseAsync(state).ConfigureAwait(false);
        }

        var opened = new List<ChannelState>();
        var problems = new List<string>();

        foreach (var (key, lanInterface) in wantedByKey)
        {
            if (_channels.ContainsKey(key))
            {
                continue;
            }

            try
            {
                var channel = _channelFactory.Open(lanInterface, _options.DiscoveryPort);
                var state = new ChannelState(channel, CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
                state.ReceiveLoop = Task.Run(() => ReceiveLoopAsync(state), CancellationToken.None);
                _channels.Add(key, state);
                opened.Add(state);
                LogInterfaceOpened(_logger, lanInterface, _options.DiscoveryPort);
            }
            catch (SocketException ex)
            {
                problems.Add(DescribeBindFailure(lanInterface, ex));
            }
        }

        var problemText = string.Join('\n', problems);
        if (problemText != _lastProblems)
        {
            _lastProblems = problemText;
            foreach (var problem in problems)
            {
                LogBindFailed(_logger, problem);
            }
        }

        _localAddresses = wanted.Select(i => i.Address).ToFrozenSet();

        var active = _channels.Values.Select(s => s.Channel.LanInterface).OrderBy(i => i.Name, StringComparer.CurrentCulture).ToList();
        var stateNow = wanted.Count == 0 ? DiscoveryState.NoNetwork
            : active.Count == 0 ? DiscoveryState.Failed
            : DiscoveryState.Running;
        SetStatus(new DiscoveryStatus(stateNow, active, problems));

        // A request makes peers answer within half a second instead of us waiting for their next announcement.
        var requestTargets = requestOnAll ? _channels.Values.ToList() : opened;
        await BroadcastAsync(DiscoveryPacketType.Request, requestTargets, cancellationToken).ConfigureAwait(false);
        await BroadcastAsync(DiscoveryPacketType.Announce, opened, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(ChannelState state)
    {
        var cancellationToken = state.Cancellation.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            DiscoveryDatagram datagram;
            try
            {
                datagram = await state.Channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                LogReceiveFailed(_logger, state.Channel.LanInterface, ex);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), _time, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            Handle(state, datagram, cancellationToken);
        }
    }

    private void Handle(ChannelState state, DiscoveryDatagram datagram, CancellationToken cancellationToken)
    {
        var remote = Ipv4Subnet.Normalize(datagram.RemoteEndPoint.Address);
        if (remote is null)
        {
            return;
        }

        if (!DiscoveryPacketCodec.TryDecode(datagram.Data.Span, out var packet, out var error))
        {
            LogPacketRejected(_logger, remote, error);
            return;
        }

        if (packet.DeviceId == _identity.Identity.DeviceId)
        {
            // Our own broadcasts come back to us. The same ID from any other address means another computer
            // is running with a copy of this identity (architecture.md §1.1).
            if (!_localAddresses.Contains(remote))
            {
                WarnDuplicateDeviceId(remote);
            }

            return;
        }

        if (!state.Channel.LanInterface.Subnet.Contains(remote))
        {
            LogOutsideSubnet(_logger, remote, state.Channel.LanInterface);
            return;
        }

        switch (_registry.Observe(packet, remote, _time.GetUtcNow()))
        {
            case PeerObservation.Added:
                LogPeerDiscovered(_logger, packet.DisplayId, packet.Username, remote, packet.AppVersion);
                break;
            case PeerObservation.Rejected:
                LogRegistryFull(_logger, remote);
                return;
        }

        if (packet.Type == DiscoveryPacketType.Request && _rateLimiter.TryAcquire(remote))
        {
            _ = RespondAsync(state, datagram.RemoteEndPoint, cancellationToken);
        }
    }

    private async Task RespondAsync(ChannelState state, IPEndPoint requester, CancellationToken cancellationToken)
    {
        try
        {
            // Spreads the answers out so a request doesn't trigger a burst from every peer at once.
            var delay = Random.Shared.Next(0, (int)_options.ResponseMaxDelay.TotalMilliseconds + 1);
            await Task.Delay(TimeSpan.FromMilliseconds(delay), _time, cancellationToken).ConfigureAwait(false);
            await SendAsync(state, DiscoveryPacketType.Response, requester, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task BroadcastAsync(DiscoveryPacketType type, IEnumerable<ChannelState> channels, CancellationToken cancellationToken)
    {
        foreach (var state in channels.ToList())
        {
            var destination = new IPEndPoint(state.Channel.LanInterface.Subnet.BroadcastAddress, _options.DiscoveryPort);
            await SendAsync(state, type, destination, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SendAsync(ChannelState state, DiscoveryPacketType type, IPEndPoint destination, CancellationToken cancellationToken)
    {
        var data = DiscoveryPacketCodec.Encode(BuildPacket(type));
        try
        {
            await state.Channel.SendAsync(data, destination, cancellationToken).ConfigureAwait(false);
            state.SendFailing = false;
        }
        catch (ObjectDisposedException)
        {
            // The interface went away while the send was queued.
        }
        catch (SocketException ex)
        {
            if (!state.SendFailing)
            {
                state.SendFailing = true;
                LogSendFailed(_logger, state.Channel.LanInterface, destination, ex);
            }
        }
    }

    private DiscoveryPacket BuildPacket(DiscoveryPacketType type)
    {
        var identity = _identity.Identity;
        return new DiscoveryPacket(type, identity.DeviceId, identity.Username, _options.MessagingTcpPort, _options.AppVersion);
    }

    private void WarnDuplicateDeviceId(IPAddress remote)
    {
        var now = _time.GetUtcNow().UtcTicks;
        var last = Interlocked.Read(ref _lastDuplicateWarningTicks);
        if (now - last >= DuplicateIdWarningInterval.Ticks
            && Interlocked.CompareExchange(ref _lastDuplicateWarningTicks, now, last) == last)
        {
            LogDuplicateDeviceId(_logger, remote);
        }
    }

    private string DescribeBindFailure(LanInterface lanInterface, SocketException ex) => ex.SocketErrorCode switch
    {
        SocketError.AddressAlreadyInUse or SocketError.AccessDenied =>
            $"UDP port {_options.DiscoveryPort} is already in use on {lanInterface}. Is another copy of Roomie running?",
        SocketError.AddressNotAvailable => $"{lanInterface} is no longer available.",
        _ => $"Couldn't listen on {lanInterface}: {ex.Message}",
    };

    private void SetStatus(DiscoveryStatus status)
    {
        var previous = _status;
        if (previous.State == status.State
            && previous.Interfaces.SequenceEqual(status.Interfaces)
            && previous.Problems.SequenceEqual(status.Problems))
        {
            return;
        }

        _status = status;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task CloseAllAsync()
    {
        foreach (var state in _channels.Values.ToList())
        {
            await CloseAsync(state).ConfigureAwait(false);
        }

        _channels.Clear();
        _localAddresses = FrozenSet<IPAddress>.Empty;
    }

    private static async Task CloseAsync(ChannelState state)
    {
        await state.Cancellation.CancelAsync().ConfigureAwait(false);
        state.Channel.Dispose();
        if (state.ReceiveLoop is { } loop)
        {
            await loop.ConfigureAwait(false);
        }

        state.Cancellation.Dispose();
    }

    private sealed class ChannelState(IDiscoveryChannel channel, CancellationTokenSource cancellation)
    {
        public IDiscoveryChannel Channel { get; } = channel;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task? ReceiveLoop { get; set; }
        public bool SendFailing { get; set; }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Discovery listening on {Interface}, UDP port {Port}")]
    private static partial void LogInterfaceOpened(ILogger logger, LanInterface @interface, int port);

    [LoggerMessage(Level = LogLevel.Information, Message = "Discovery stopped listening on {Interface}")]
    private static partial void LogInterfaceClosed(ILogger logger, LanInterface @interface);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Discovery: {Problem}")]
    private static partial void LogBindFailed(ILogger logger, string problem);

    [LoggerMessage(Level = LogLevel.Information, Message = "Network configuration changed; refreshing discovery interfaces")]
    private static partial void LogNetworkChanged(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't list network interfaces")]
    private static partial void LogInterfaceEnumerationFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Discovered {DisplayId} \"{Username}\" at {Address} (Roomie {AppVersion})")]
    private static partial void LogPeerDiscovered(ILogger logger, string displayId, string username, IPAddress address, string appVersion);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Ignored discovery packet from {Address}: {Error}")]
    private static partial void LogPacketRejected(ILogger logger, IPAddress address, DiscoveryPacketError error);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Ignored discovery packet from {Address}, which is outside the subnet of {Interface}")]
    private static partial void LogOutsideSubnet(ILogger logger, IPAddress address, LanInterface @interface);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Peer registry is full; ignored a new device at {Address}")]
    private static partial void LogRegistryFull(ILogger logger, IPAddress address);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Another computer at {Address} is announcing this computer's device ID. Its identity was probably copied from here.")]
    private static partial void LogDuplicateDeviceId(ILogger logger, IPAddress address);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Discovery receive failed on {Interface}")]
    private static partial void LogReceiveFailed(ILogger logger, LanInterface @interface, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Discovery send failed on {Interface} to {Destination}")]
    private static partial void LogSendFailed(ILogger logger, LanInterface @interface, IPEndPoint destination, Exception exception);

    [LoggerMessage(Level = LogLevel.Critical, Message = "This computer's discovery packet is invalid; discovery is disabled")]
    private static partial void LogInvalidLocalPacket(ILogger logger, Exception exception);
}
