using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Roomie.Core.Connections;
using Roomie.Core.Discovery;
using Roomie.Core.Identity;
using Roomie.Core.Protocol;
using Roomie.Core.Trust;
using Roomie.Network.Discovery;

namespace Roomie.Network.Connections;

/// <summary>
/// Owns every TCP/TLS session (architecture.md §4.2): a listener per eligible LAN interface, outbound
/// connections, the HELLO exchange, one session per device, duplicate resolution, and automatic
/// reconnection of trusted devices. Unpaired devices may connect, but only for pairing.
/// </summary>
public sealed partial class ConnectionManager : BackgroundService
{
    private const int OurProtocolVersion = 1;
    private const int OurMinimumVersion = 1;

    private static readonly TimeSpan ForgetAfter = TimeSpan.FromMinutes(5);

    private readonly ILocalIdentityProvider _identity;
    private readonly ITlsCredentialProvider _tls;
    private readonly ILanInterfaceProvider _interfaces;
    private readonly PeerRegistry _registry;
    private readonly ITrustStore _trust;
    private readonly ConnectionOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<ConnectionManager> _logger;
    private readonly SemaphoreSlim _pendingHandshakes;
    private readonly SlidingWindowRateLimiter _inboundLimiter;

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, DeviceState> _devices = [];

    // Owned by the RunAsync loop.
    private readonly Dictionary<string, ListenerState> _listeners = [];
    private string _lastProblems = string.Empty;

    private volatile SslStreamCertificateContext? _credentials;
    private volatile IReadOnlyList<LanInterface> _activeInterfaces = [];
    private volatile IReadOnlyList<IPEndPoint> _listeningEndPoints = [];
    private volatile IReadOnlyList<string> _problems = [];
    private CancellationToken _stopping;
    private int _networkChangePending;
    private long _networkChangedAtTicks;

    public ConnectionManager(
        ILocalIdentityProvider identity,
        ITlsCredentialProvider tls,
        ILanInterfaceProvider interfaces,
        PeerRegistry registry,
        ITrustStore trust,
        ConnectionOptions options,
        TimeProvider time,
        ILogger<ConnectionManager> logger)
    {
        _identity = identity;
        _tls = tls;
        _interfaces = interfaces;
        _registry = registry;
        _trust = trust;
        _options = options;
        _time = time;
        _logger = logger;
        Dispatcher = new RmpDispatcher(logger);
        _pendingHandshakes = new SemaphoreSlim(options.MaxPendingHandshakes);
        _inboundLimiter = new SlidingWindowRateLimiter(time, options.MaxInboundPerAddressPerMinute, TimeSpan.FromMinutes(1));
    }

    /// <summary>Raised on a background thread when any device's connection state or the listener status changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when a session becomes the active session for its device.</summary>
    public event EventHandler<PeerSession>? SessionOpened;

    /// <summary>Raised when a live session becomes trusted, because pairing finished on it.</summary>
    public event EventHandler<PeerSession>? SessionTrusted;

    /// <summary>Where services register for the message types they own. Register before the host starts.</summary>
    public RmpDispatcher Dispatcher { get; }

    public IReadOnlyList<IPEndPoint> ListeningEndPoints => _listeningEndPoints;

    /// <summary>Why listening failed on some interfaces, for display.</summary>
    public IReadOnlyList<string> Problems => _problems;

    public IReadOnlyList<PeerConnectionInfo> Snapshot()
    {
        lock (_gate)
        {
            return _devices.Values.Select(ToInfo).ToList();
        }
    }

    public PeerConnectionInfo GetInfo(Guid deviceId)
    {
        lock (_gate)
        {
            return _devices.TryGetValue(deviceId, out var state) ? ToInfo(state) : new PeerConnectionInfo(deviceId, PeerConnectionState.Disconnected);
        }
    }

    public bool TryGetSession(Guid deviceId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out PeerSession? session)
    {
        lock (_gate)
        {
            session = _devices.TryGetValue(deviceId, out var state) ? state.Session : null;
            return session is not null;
        }
    }

    /// <summary>
    /// A user-initiated connection, allowed whichever device ID is smaller (architecture.md §3.1). Returns
    /// once the session is up or every known address has failed.
    /// </summary>
    public Task<PeerConnectionInfo> ConnectAsync(Guid deviceId, CancellationToken cancellationToken = default) =>
        ConnectCoreAsync(deviceId, automatic: false, cancellationToken);

    /// <summary>Ends the session and stops automatic reconnection until the user connects again.</summary>
    public async Task DisconnectAsync(Guid deviceId)
    {
        PeerSession? session;
        lock (_gate)
        {
            if (!_devices.TryGetValue(deviceId, out var state))
            {
                return;
            }

            state.ManualDisconnect = true;
            session = state.Session;
            if (session is null && state.State == PeerConnectionState.Reconnecting)
            {
                state.State = PeerConnectionState.Disconnected;
            }
        }

        if (session is not null)
        {
            OnSessionClosed(session, await CloseAndWaitAsync(session).ConfigureAwait(false));
        }

        RaiseChanged();
    }

    private static async Task<SessionCloseInfo> CloseAndWaitAsync(PeerSession session)
    {
        await session.CloseAsync(GoodbyeReasons.Shutdown, "Disconnected.").ConfigureAwait(false);
        return await session.Completion.ConfigureAwait(false);
    }

    /// <summary>Upgrades a live session after pairing completed on it.</summary>
    public void MarkTrusted(PeerSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            session.MarkTrusted();
            if (_devices.TryGetValue(session.RemoteDeviceId, out var state) && state.Session == session)
            {
                state.State = PeerConnectionState.ConnectedTrusted;
                state.ChangedAt = _time.GetUtcNow();
            }
        }

        LogSessionTrusted(_logger, session.RemoteDeviceId);
        RaiseChanged();
        SessionTrusted?.Invoke(this, session);
    }

    /// <summary>
    /// Ends trusted sessions whose trust was revoked, and every session with a device that was blocked:
    /// a session must never keep privileges its device no longer has.
    /// </summary>
    private void OnTrustChanged(object? sender, EventArgs e)
    {
        var toClose = new List<(PeerSession Session, bool Blocked)>();
        lock (_gate)
        {
            foreach (var state in _devices.Values)
            {
                var blocked = _trust.IsBlocked(state.DeviceId);
                if (blocked && state.Session is null)
                {
                    state.State = PeerConnectionState.Blocked;
                }

                if (state.Session is not { } session)
                {
                    if (!blocked && state.State == PeerConnectionState.Blocked)
                    {
                        state.State = PeerConnectionState.Disconnected;
                        state.LastError = null;
                    }

                    continue;
                }

                if (blocked || (session.IsTrusted && _trust.FindTrusted(state.DeviceId) is null))
                {
                    toClose.Add((session, blocked));
                    state.ManualDisconnect = true;
                }
            }
        }

        foreach (var (session, blocked) in toClose)
        {
            _ = CloseRevokedAsync(session, blocked);
        }

        RaiseChanged();
    }

    private async Task CloseRevokedAsync(PeerSession session, bool blocked)
    {
        var info = await CloseWithAsync(session, blocked ? GoodbyeReasons.PeerBlocked : GoodbyeReasons.Shutdown,
            blocked ? "You blocked this device." : "You removed this device.").ConfigureAwait(false);
        OnSessionClosed(session, info);
        if (blocked)
        {
            SetTerminalState(session.RemoteDeviceId, PeerConnectionState.Blocked, "You blocked this device.");
        }
    }

    private static async Task<SessionCloseInfo> CloseWithAsync(PeerSession session, string reason, string description)
    {
        await session.CloseAsync(reason, description).ConfigureAwait(false);
        return await session.Completion.ConfigureAwait(false);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Run(() => RunAsync(stoppingToken), stoppingToken);

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        _stopping = cancellationToken;
        _credentials = SslStreamCertificateContext.Create(_tls.TlsCertificate, additionalCertificates: null, offline: true);

        _interfaces.InterfacesChanged += OnInterfacesChanged;
        _trust.Changed += OnTrustChanged;
        try
        {
            ReconcileListeners();
            var nextRefresh = _time.GetUtcNow() + _options.InterfaceRefreshInterval;

            using var timer = new PeriodicTimer(_options.MaintenanceInterval, _time);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var now = _time.GetUtcNow();
                var changedAt = new DateTimeOffset(Interlocked.Read(ref _networkChangedAtTicks), TimeSpan.Zero);
                if ((Volatile.Read(ref _networkChangePending) == 1 && now - changedAt >= _options.NetworkChangeDebounce) || now >= nextRefresh)
                {
                    Interlocked.Exchange(ref _networkChangePending, 0);
                    ReconcileListeners();
                    nextRefresh = now + _options.InterfaceRefreshInterval;
                }

                MaintainReconnects(now);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _interfaces.InterfacesChanged -= OnInterfacesChanged;
            _trust.Changed -= OnTrustChanged;
            await ShutdownAsync().ConfigureAwait(false);
        }
    }

    private void OnInterfacesChanged(object? sender, EventArgs e)
    {
        Interlocked.Exchange(ref _networkChangedAtTicks, _time.GetUtcNow().UtcTicks);
        Interlocked.Exchange(ref _networkChangePending, 1);
    }

    private void ReconcileListeners()
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

        foreach (var key in _listeners.Keys.Where(key => !wantedByKey.ContainsKey(key)).ToList())
        {
            var state = _listeners[key];
            _listeners.Remove(key);
            state.Stop();
            LogListenerClosed(_logger, state.Interface);
        }

        var problems = new List<string>();
        foreach (var (key, lanInterface) in wantedByKey)
        {
            if (_listeners.ContainsKey(key))
            {
                continue;
            }

            var listener = new TcpListener(lanInterface.Address, _options.Port) { ExclusiveAddressUse = true };
            try
            {
                listener.Start(backlog: 32);
            }
            catch (SocketException ex)
            {
                listener.Dispose();
                problems.Add(ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied
                    ? $"TCP port {_options.Port} is already in use on {lanInterface}. Is another copy of Roomie running?"
                    : $"Couldn't accept connections on {lanInterface}: {ex.Message}");
                continue;
            }

            var listenerState = new ListenerState(listener, lanInterface, CancellationTokenSource.CreateLinkedTokenSource(_stopping));
            _ = Task.Run(() => AcceptLoopAsync(listenerState), CancellationToken.None);
            _listeners.Add(key, listenerState);
            LogListening(_logger, lanInterface, ((IPEndPoint)listener.LocalEndpoint).Port);
        }

        var problemText = string.Join('\n', problems);
        var changed = problemText != _lastProblems;
        if (changed)
        {
            _lastProblems = problemText;
            foreach (var problem in problems)
            {
                LogListenFailed(_logger, problem);
            }
        }

        _activeInterfaces = wanted;
        _listeningEndPoints = _listeners.Values.Select(l => (IPEndPoint)l.Listener.LocalEndpoint).ToList();
        _problems = problems;

        if (changed)
        {
            RaiseChanged();
        }
    }

    private async Task AcceptLoopAsync(ListenerState state)
    {
        var cancellationToken = state.Cancellation.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await state.Listener.AcceptSocketAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                LogAcceptFailed(_logger, state.Interface, ex);
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

            _ = Task.Run(() => HandleInboundAsync(socket, state.Interface), CancellationToken.None);
        }
    }

    private async Task HandleInboundAsync(Socket socket, LanInterface lanInterface)
    {
        var remote = (IPEndPoint)socket.RemoteEndPoint!;
        var address = Ipv4Subnet.Normalize(remote.Address);

        if (address is null || !lanInterface.Subnet.Contains(address))
        {
            LogInboundRefused(_logger, remote, "outside the interface's subnet");
            socket.Dispose();
            return;
        }

        if (!_inboundLimiter.TryAcquire(address) || !_pendingHandshakes.Wait(0))
        {
            LogInboundRefused(_logger, remote, "too many connection attempts");
            socket.Dispose();
            return;
        }

        try
        {
            socket.NoDelay = true;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
            timeout.CancelAfter(_options.HandshakeTimeout);

            var (ssl, peer) = await TlsHandshake.AuthenticateAsServerAsync(new NetworkStream(socket, ownsSocket: true), _credentials!, _time, timeout.Token).ConfigureAwait(false);
            var error = await EstablishAsync(ssl, peer, SessionDirection.Inbound, new IPEndPoint(address, remote.Port), timeout.Token).ConfigureAwait(false);
            if (error is not null)
            {
                LogInboundRejected(_logger, peer.DeviceId, remote, error);
            }
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException or OperationCanceledException or SocketException or ObjectDisposedException)
        {
            LogInboundHandshakeFailed(_logger, remote, ex.Message);
            socket.Dispose();
        }
        finally
        {
            _pendingHandshakes.Release();
        }
    }

    private async Task<PeerConnectionInfo> ConnectCoreAsync(Guid deviceId, bool automatic, CancellationToken cancellationToken)
    {
        if (deviceId == _identity.Identity.DeviceId)
        {
            throw new ArgumentException("A device can't connect to itself.", nameof(deviceId));
        }

        var credentials = _credentials;
        lock (_gate)
        {
            var state = GetOrCreate(deviceId);
            if (credentials is null)
            {
                state.State = PeerConnectionState.Failed;
                state.LastError = "Roomie's network service hasn't started.";
                return ToInfo(state);
            }

            if (_trust.IsBlocked(deviceId))
            {
                state.State = PeerConnectionState.Blocked;
                return ToInfo(state);
            }

            if (state.Session is not null || state.Connecting)
            {
                return ToInfo(state);
            }

            state.Connecting = true;

            // A retry by itself leaves a warning showing until the identity is actually right.
            if (!automatic || state.State != PeerConnectionState.IdentityMismatch)
            {
                state.State = PeerConnectionState.Connecting;
                state.LastError = null;
            }

            if (!automatic)
            {
                state.ManualDisconnect = false;
            }
        }

        RaiseChanged();

        string? lastError = null;
        try
        {
            if (!_registry.TryGet(deviceId, out var peer))
            {
                lastError = "This device isn't on the network right now.";
            }
            else
            {
                var interfaces = _activeInterfaces;
                var addresses = peer.Addresses.Where(a => interfaces.Any(i => i.Subnet.Contains(a))).ToList();
                lastError = addresses.Count == 0 ? "This device isn't on a network this computer is connected to." : null;

                foreach (var address in addresses)
                {
                    lastError = await DialAsync(new IPEndPoint(address, peer.TcpPort), deviceId, credentials, cancellationToken).ConfigureAwait(false);
                    if (lastError is null)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                var state = GetOrCreate(deviceId);
                state.Connecting = false;
                if (lastError is not null && state.Session is null)
                {
                    if (state.State is not (PeerConnectionState.IdentityMismatch or PeerConnectionState.Blocked))
                    {
                        var trusted = _trust.FindTrusted(deviceId) is not null;
                        state.State = trusted && !state.ManualDisconnect ? PeerConnectionState.Reconnecting : PeerConnectionState.Failed;
                        state.LastError = lastError;
                        state.ChangedAt = _time.GetUtcNow();
                    }

                    if (automatic)
                    {
                        state.FailedAttempts++;
                        state.NextAttemptAt = _time.GetUtcNow() + ReconnectDelay(state.FailedAttempts);
                    }
                }
            }
        }

        if (lastError is not null)
        {
            LogConnectFailed(_logger, deviceId, lastError);
        }

        RaiseChanged();
        return GetInfo(deviceId);
    }

    /// <returns>Null on success, otherwise a description of the failure for the user.</returns>
    private async Task<string?> DialAsync(IPEndPoint endPoint, Guid deviceId, SslStreamCertificateContext credentials, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping);
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            linked.CancelAfter(_options.ConnectTimeout);
            try
            {
                await socket.ConnectAsync(endPoint, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_stopping.IsCancellationRequested)
            {
                socket.Dispose();
                return $"No answer from {endPoint} within {_options.ConnectTimeout.TotalSeconds:0} seconds. A firewall may be blocking TCP port {endPoint.Port}.";
            }

            SetAuthenticating(deviceId);
            linked.CancelAfter(_options.HandshakeTimeout);
            var (ssl, peer) = await TlsHandshake.AuthenticateAsClientAsync(new NetworkStream(socket, ownsSocket: true), credentials, deviceId, _time, linked.Token).ConfigureAwait(false);
            return await EstablishAsync(ssl, peer, SessionDirection.Outbound, endPoint, linked.Token).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            return ex.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => $"{endPoint} refused the connection. Roomie may not be running there, or a firewall is blocking TCP port {endPoint.Port}.",
                SocketError.HostUnreachable or SocketError.NetworkUnreachable => $"{endPoint} can't be reached from this computer.",
                _ => $"Couldn't connect to {endPoint}: {ex.Message}",
            };
        }
        catch (AuthenticationException ex)
        {
            socket.Dispose();
            return $"The secure connection failed: {ex.Message}";
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            socket.Dispose();
            return "The connection was lost during the secure handshake.";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            socket.Dispose();
            return "The other device didn't finish the secure handshake in time.";
        }
    }

    /// <summary>
    /// Runs the trust checks and HELLO exchange on a fresh TLS stream and registers the session. Takes
    /// ownership of <paramref name="ssl"/>.
    /// </summary>
    /// <returns>Null on success, otherwise why the session was refused.</returns>
    private async Task<string?> EstablishAsync(SslStream ssl, RemoteDeviceCertificate peer, SessionDirection direction, IPEndPoint remote, CancellationToken cancellationToken)
    {
        var localId = _identity.Identity.DeviceId;
        var keepStream = false;
        try
        {
            if (peer.DeviceId == localId)
            {
                LogSelfConnection(_logger, remote);
                return "That device is using this computer's own identity.";
            }

            if (_trust.IsBlocked(peer.DeviceId))
            {
                SetTerminalState(peer.DeviceId, PeerConnectionState.Blocked, "You blocked this device.");
                await RejectAsync(ssl, RmpMessageTypes.Goodbye, SessionPayloads.ToElement(new GoodbyePayload(GoodbyeReasons.PeerBlocked)), localId, peer.DeviceId).ConfigureAwait(false);
                return "The device is blocked.";
            }

            var pinned = _trust.FindTrusted(peer.DeviceId);
            if (pinned is not null && !CertificateFingerprint.AreEqual(pinned.CertificateFingerprint, peer.Fingerprint))
            {
                LogIdentityMismatch(_logger, peer.DeviceId, pinned.CertificateFingerprint, peer.Fingerprint);
                var mismatch = SetTerminalState(peer.DeviceId, PeerConnectionState.IdentityMismatch,
                    "This device presented a different identity from the one you paired with. It may be another computer pretending to be it, or it was reinstalled. Pair again only if you're sure it's the right device.");
                await RejectAsync(ssl, RmpMessageTypes.Error, SessionPayloads.ToElement(new ErrorPayload(RmpErrorCodes.IdentityMismatch, "This device's identity doesn't match the one paired earlier.")), localId, peer.DeviceId).ConfigureAwait(false);
                return mismatch;
            }

            var trusted = pinned is not null;
            var refusal = CheckCapacity(peer, trusted, remote.Address);
            if (refusal is not null)
            {
                await RejectAsync(ssl, RmpMessageTypes.Error, SessionPayloads.ToElement(new ErrorPayload(refusal.Value.Code, refusal.Value.Message)), localId, peer.DeviceId).ConfigureAwait(false);
                return refusal.Value.Message;
            }

            var ourHello = new HelloPayload(OurProtocolVersion, OurMinimumVersion, _options.AppVersion, _options.Capabilities);
            await SendFrameAsync(ssl, RmpMessageTypes.Hello, SessionPayloads.ToElement(ourHello), localId, peer.DeviceId, cancellationToken).ConfigureAwait(false);

            var (hello, rejection) = await ReadHelloAsync(ssl, localId, peer.DeviceId, cancellationToken).ConfigureAwait(false);
            if (hello is null)
            {
                return rejection;
            }

            var negotiated = RmpNegotiatedSession.Negotiate(ourHello, hello);
            if (negotiated is null)
            {
                await SendQuietlyAsync(ssl, RmpMessageTypes.Error, SessionPayloads.ToElement(new ErrorPayload(RmpErrorCodes.UnsupportedVersion, "Incompatible Roomie protocol version.")), localId, peer.DeviceId).ConfigureAwait(false);
                return $"The other device runs Roomie {hello.ApplicationVersion}, which can't talk to this version.";
            }

            var session = new PeerSession(ssl, localId, peer, trusted, direction, remote, hello, negotiated, Dispatcher, _options, _time, _logger);
            keepStream = true;
            Register(session);
            return null;
        }
        catch (RmpProtocolException ex)
        {
            await SendQuietlyAsync(ssl, RmpMessageTypes.Error, SessionPayloads.ToElement(new ErrorPayload(ex.Code, "Invalid handshake message.")), localId, peer.DeviceId).ConfigureAwait(false);
            return "The other device sent an invalid response.";
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            return "The connection was lost during the handshake.";
        }
        catch (OperationCanceledException) when (!_stopping.IsCancellationRequested)
        {
            return "The other device didn't finish the handshake in time.";
        }
        finally
        {
            if (!keepStream)
            {
                await ssl.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private (string Code, string Message)? CheckCapacity(RemoteDeviceCertificate peer, bool trusted, IPAddress address)
    {
        lock (_gate)
        {
            // Two different certificates claiming one device ID: trust neither (architecture.md §1.1).
            if (_devices.TryGetValue(peer.DeviceId, out var existing) && existing.Session is { IsClosed: false } current
                && !CertificateFingerprint.AreEqual(current.RemoteFingerprint, peer.Fingerprint))
            {
                LogIdentityConflict(_logger, peer.DeviceId, current.RemoteFingerprint, peer.Fingerprint);
                return (RmpErrorCodes.IdentityMismatch, "Another device with a different identity is already connected under this device ID.");
            }

            var sessions = _devices.Values.Where(d => d.Session is not null).ToList();
            if (sessions.Count >= _options.MaxSessions)
            {
                return (RmpErrorCodes.RateLimited, "Too many connections.");
            }

            if (!trusted && sessions.Count(d => d.Session!.IsTrusted == false) >= _options.MaxUntrustedSessions)
            {
                return (RmpErrorCodes.RateLimited, "Too many connections from unpaired devices.");
            }

            // A device connecting again replaces its own session, so it doesn't count against itself.
            if (!trusted && sessions.Count(d => !d.Session!.IsTrusted && d.DeviceId != peer.DeviceId && d.Session.RemoteEndPoint.Address.Equals(address)) >= _options.MaxUntrustedSessionsPerAddress)
            {
                return (RmpErrorCodes.RateLimited, "Too many connections from unpaired devices at that address.");
            }

            return null;
        }
    }

    private static async Task<(HelloPayload? Hello, string? Rejection)> ReadHelloAsync(SslStream ssl, Guid localId, Guid remoteId, CancellationToken cancellationToken)
    {
        var frame = await RmpFraming.ReadAsync(ssl, cancellationToken).ConfigureAwait(false)
            ?? throw new IOException("The connection closed during the handshake.");

        if (frame.Type != RmpFrameType.Control)
        {
            throw new RmpProtocolException("Expected HELLO.");
        }

        if (!RmpEnvelopeCodec.TryDecode(frame.Payload, out var envelope, out var error))
        {
            throw new RmpProtocolException(error);
        }

        if (envelope.SenderId != remoteId || envelope.RecipientId != localId)
        {
            throw new RmpProtocolException("HELLO sender or recipient doesn't match the TLS identity.");
        }

        switch (envelope.Type)
        {
            case RmpMessageTypes.Hello when SessionPayloads.TryReadHello(envelope.Payload, out var hello):
                return (hello, null);

            case RmpMessageTypes.Error when SessionPayloads.TryReadError(envelope.Payload, out var reported):
                return (null, reported.Code switch
                {
                    RmpErrorCodes.IdentityMismatch => "The other device doesn't recognize this computer's identity. It may have been paired with a previous identity of this computer.",
                    RmpErrorCodes.UnsupportedVersion => "The other device runs a version of Roomie that can't talk to this one.",
                    RmpErrorCodes.RateLimited => "The other device is busy. Try again in a moment.",
                    _ => $"The other device refused the connection ({reported.Code}).",
                });

            case RmpMessageTypes.Goodbye:
                var reason = SessionPayloads.TryReadGoodbye(envelope.Payload, out var goodbye) ? goodbye.Reason : null;
                return (null, reason == GoodbyeReasons.PeerBlocked ? "The other device has blocked this computer." : "The other device ended the connection.");

            default:
                throw new RmpProtocolException("Expected HELLO.");
        }
    }

    private void Register(PeerSession session)
    {
        PeerSession? loser = null;
        lock (_gate)
        {
            var state = GetOrCreate(session.RemoteDeviceId);
            if (state.Session is { IsClosed: false } existing)
            {
                if (KeepNewSession(existing, session))
                {
                    loser = existing;
                    state.Session = session;
                }
                else
                {
                    loser = session;
                }
            }
            else
            {
                state.Session = session;
            }

            if (state.Session == session)
            {
                state.State = session.IsTrusted ? PeerConnectionState.ConnectedTrusted : PeerConnectionState.ConnectedUntrusted;
                state.Fingerprint = session.RemoteFingerprint;
                state.Direction = session.Direction;
                state.RemoteEndPoint = session.RemoteEndPoint;
                state.Security = session.Security;
                state.LastError = null;
                state.FailedAttempts = 0;
                state.SeenWithoutSessionSince = null;
                state.ChangedAt = _time.GetUtcNow();
            }
        }

        if (loser is not null)
        {
            LogDuplicateSession(_logger, session.RemoteDeviceId, loser.InitiatorDeviceId);
            _ = loser.CloseAsync(GoodbyeReasons.DuplicateSession, "Replaced by another connection to the same device.");
        }

        if (loser != session)
        {
            LogSessionOpened(_logger, session.RemoteDeviceId, session.Direction, session.RemoteEndPoint, session.Security, session.IsTrusted, session.ProtocolVersion, session.Capabilities);
            _ = ObserveAsync(session);
            session.Start();
            SessionOpened?.Invoke(this, session);
        }

        RaiseChanged();
    }

    /// <summary>
    /// Both sides apply the same rule, so they agree on the survivor: keep the session whose TCP initiator
    /// has the smaller device ID (architecture.md §4.2). If the same side opened both, the newer one wins:
    /// the old one is most likely a connection the other side already considers dead.
    /// </summary>
    internal static bool KeepNewSession(PeerSession existing, PeerSession candidate)
    {
        if (existing.InitiatorDeviceId == candidate.InitiatorDeviceId)
        {
            return true;
        }

        return CompareIds(candidate.InitiatorDeviceId, existing.InitiatorDeviceId) < 0;
    }

    internal static int CompareIds(Guid left, Guid right) => string.CompareOrdinal(left.ToString("D"), right.ToString("D"));

    private async Task ObserveAsync(PeerSession session) =>
        OnSessionClosed(session, await session.Completion.ConfigureAwait(false));

    /// <summary>Idempotent: only acts while <paramref name="session"/> is still the device's current session.</summary>
    private void OnSessionClosed(PeerSession session, SessionCloseInfo info)
    {
        lock (_gate)
        {
            if (!_devices.TryGetValue(session.RemoteDeviceId, out var state) || state.Session != session)
            {
                return;
            }

            state.Session = null;
            state.ChangedAt = _time.GetUtcNow();
            var userClosed = info.Kind == SessionCloseKind.Local && info.Reason == GoodbyeReasons.Shutdown;
            if (userClosed || !session.IsTrusted || state.ManualDisconnect)
            {
                state.State = PeerConnectionState.Disconnected;
                state.LastError = userClosed ? null : info.Description;
            }
            else
            {
                state.State = PeerConnectionState.Reconnecting;
                state.LastError = info.Description;
                state.FailedAttempts = 0;
                state.NextAttemptAt = _time.GetUtcNow() + ReconnectDelay(1);
            }
        }

        RaiseChanged();
    }

    private void MaintainReconnects(DateTimeOffset now)
    {
        var localId = _identity.Identity.DeviceId;
        var due = new List<Guid>();

        lock (_gate)
        {
            foreach (var peer in _registry.Snapshot())
            {
                if (_trust.FindTrusted(peer.DeviceId) is null || _trust.IsBlocked(peer.DeviceId))
                {
                    continue;
                }

                var state = GetOrCreate(peer.DeviceId);
                // An identity warning doesn't stop this. Anyone can connect claiming a paired device's ID, and giving
                // up on the real device would hand them an easy way to cut it off. Each dial still checks the pin.
                if (state.Session is not null || state.Connecting || state.ManualDisconnect)
                {
                    continue;
                }

                if (CompareIds(localId, peer.DeviceId) > 0)
                {
                    // The larger side only dials when the smaller side hasn't managed to within a grace period,
                    // e.g. because a firewall blocks that direction.
                    state.SeenWithoutSessionSince ??= now;
                    if (now - state.SeenWithoutSessionSince < _options.LargerIdDialDelay)
                    {
                        continue;
                    }
                }

                if (now >= state.NextAttemptAt)
                {
                    due.Add(peer.DeviceId);
                }
            }

            // Forget idle devices that are gone from the network, so the table stays bounded.
            foreach (var (id, state) in _devices.ToList())
            {
                if (state.Session is null && !state.Connecting && now - state.ChangedAt > ForgetAfter && !_registry.TryGet(id, out _))
                {
                    _devices.Remove(id);
                }
            }
        }

        foreach (var deviceId in due)
        {
            _ = ConnectCoreAsync(deviceId, automatic: true, _stopping);
        }
    }

    private TimeSpan ReconnectDelay(int attempt)
    {
        var delays = _options.ReconnectDelays;
        var delay = delays[Math.Clamp(attempt - 1, 0, delays.Count - 1)];
        return delay * (1 + (Random.Shared.NextDouble() * _options.ReconnectJitter));
    }

    private async Task ShutdownAsync()
    {
        foreach (var listener in _listeners.Values)
        {
            listener.Stop();
        }

        _listeners.Clear();
        _listeningEndPoints = [];

        List<PeerSession> sessions;
        lock (_gate)
        {
            sessions = _devices.Values.Select(d => d.Session).OfType<PeerSession>().ToList();
        }

        await Task.WhenAll(sessions.Select(s => s.CloseAsync(GoodbyeReasons.Shutdown, "Roomie is closing."))).ConfigureAwait(false);
    }

    private void SetAuthenticating(Guid deviceId)
    {
        lock (_gate)
        {
            var state = GetOrCreate(deviceId);
            if (state.Session is null && state.State != PeerConnectionState.IdentityMismatch)
            {
                state.State = PeerConnectionState.Authenticating;
            }
        }

        RaiseChanged();
    }

    private string SetTerminalState(Guid deviceId, PeerConnectionState terminal, string description)
    {
        lock (_gate)
        {
            var state = GetOrCreate(deviceId);
            if (state.Session is null)
            {
                state.State = terminal;
                state.LastError = description;
                state.ChangedAt = _time.GetUtcNow();
            }
        }

        RaiseChanged();
        return description;
    }

    private DeviceState GetOrCreate(Guid deviceId)
    {
        if (!_devices.TryGetValue(deviceId, out var state))
        {
            state = new DeviceState(deviceId) { ChangedAt = _time.GetUtcNow() };
            _devices.Add(deviceId, state);
        }

        return state;
    }

    private static PeerConnectionInfo ToInfo(DeviceState state) => new(
        state.DeviceId,
        state.State,
        state.Fingerprint,
        state.Session is null ? null : state.Direction,
        state.Session is null ? null : state.RemoteEndPoint,
        state.Session is null ? null : state.Security,
        state.LastError);

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private static async Task SendFrameAsync(SslStream ssl, string type, JsonElement payload, Guid localId, Guid remoteId, CancellationToken cancellationToken)
    {
        var envelope = RmpEnvelope.Create(type, localId, remoteId, DateTimeOffset.UtcNow, payload);
        await ssl.WriteAsync(RmpFraming.Encode(RmpFrameType.Control, RmpEnvelopeCodec.Encode(envelope)), cancellationToken).ConfigureAwait(false);
        await ssl.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Turns a peer away before the HELLO exchange. The peer's HELLO is read first: closing a TCP socket
    /// with unread data makes Windows send a reset, which would destroy the message telling it why.
    /// </summary>
    private static async Task RejectAsync(SslStream ssl, string type, JsonElement payload, Guid localId, Guid remoteId)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await RmpFraming.ReadAsync(ssl, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or RmpProtocolException)
        {
        }

        await SendQuietlyAsync(ssl, type, payload, localId, remoteId).ConfigureAwait(false);
    }

    /// <summary>Tells the peer why it is being turned away and closes TLS cleanly. Best effort, bounded by a short timeout.</summary>
    private static async Task SendQuietlyAsync(SslStream ssl, string type, JsonElement payload, Guid localId, Guid remoteId)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await SendFrameAsync(ssl, type, payload, localId, remoteId, timeout.Token).ConfigureAwait(false);
            await ssl.ShutdownAsync().WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
        }
    }

    private sealed class DeviceState(Guid deviceId)
    {
        public Guid DeviceId { get; } = deviceId;
        public PeerConnectionState State { get; set; } = PeerConnectionState.Disconnected;
        public PeerSession? Session { get; set; }
        public bool Connecting { get; set; }
        public bool ManualDisconnect { get; set; }
        public string? Fingerprint { get; set; }
        public SessionDirection? Direction { get; set; }
        public IPEndPoint? RemoteEndPoint { get; set; }
        public string? Security { get; set; }
        public string? LastError { get; set; }
        public int FailedAttempts { get; set; }
        public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.MinValue;
        public DateTimeOffset? SeenWithoutSessionSince { get; set; }
        public DateTimeOffset ChangedAt { get; set; }
    }

    private sealed class ListenerState(TcpListener listener, LanInterface lanInterface, CancellationTokenSource cancellation)
    {
        public TcpListener Listener { get; } = listener;
        public LanInterface Interface { get; } = lanInterface;
        public CancellationTokenSource Cancellation { get; } = cancellation;

        public void Stop()
        {
            Cancellation.Cancel();
            Listener.Stop();
            Listener.Dispose();
            Cancellation.Dispose();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Accepting connections on {Interface}, TCP port {Port}")]
    private static partial void LogListening(ILogger logger, LanInterface @interface, int port);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stopped accepting connections on {Interface}")]
    private static partial void LogListenerClosed(ILogger logger, LanInterface @interface);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connections: {Problem}")]
    private static partial void LogListenFailed(ILogger logger, string problem);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't list network interfaces")]
    private static partial void LogInterfaceEnumerationFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Accepting connections failed on {Interface}")]
    private static partial void LogAcceptFailed(ILogger logger, LanInterface @interface, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Refused connection from {Remote}: {Reason}")]
    private static partial void LogInboundRefused(ILogger logger, IPEndPoint remote, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Inbound handshake from {Remote} failed: {Reason}")]
    private static partial void LogInboundHandshakeFailed(ILogger logger, IPEndPoint remote, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Refused session from {DeviceId} at {Remote}: {Reason}")]
    private static partial void LogInboundRejected(ILogger logger, Guid deviceId, IPEndPoint remote, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Connecting to {DeviceId} failed: {Reason}")]
    private static partial void LogConnectFailed(ILogger logger, Guid deviceId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session with {DeviceId} opened ({Direction}, {Remote}, {Security}, trusted: {Trusted}, RMP/{ProtocolVersion}, capabilities: {Capabilities})")]
    private static partial void LogSessionOpened(ILogger logger, Guid deviceId, SessionDirection direction, IPEndPoint remote, string security, bool trusted, int protocolVersion, IReadOnlySet<string> capabilities);

    [LoggerMessage(Level = LogLevel.Information, Message = "Duplicate session with {DeviceId}; keeping the one initiated by the smaller ID, closing the one initiated by {LoserInitiator}")]
    private static partial void LogDuplicateSession(ILogger logger, Guid deviceId, Guid loserInitiator);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Identity mismatch for paired device {DeviceId}: pinned {Pinned}, presented {Presented}")]
    private static partial void LogIdentityMismatch(ILogger logger, Guid deviceId, string pinned, string presented);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Identity conflict for {DeviceId}: connected as {Existing}, new connection presented {Presented}")]
    private static partial void LogIdentityConflict(ILogger logger, Guid deviceId, string existing, string presented);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session with {DeviceId} is now trusted")]
    private static partial void LogSessionTrusted(ILogger logger, Guid deviceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A device at {Remote} presented this computer's own identity")]
    private static partial void LogSelfConnection(ILogger logger, IPEndPoint remote);
}
