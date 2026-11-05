using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Roomie.Core.Discovery;
using Roomie.Core.Identity;
using Roomie.Core.Pairing;
using Roomie.Core.Protocol;
using Roomie.Core.Trust;
using Roomie.Network.Connections;

namespace Roomie.Network.Pairing;

public enum PairingRole
{
    /// <summary>This computer sent <c>PAIR_REQUEST</c> (device A in architecture.md §3.1).</summary>
    Initiator,

    /// <summary>The other device asked to pair (device B).</summary>
    Responder,
}

public enum PairingStage
{
    /// <summary>Request sent; waiting for the other device's key.</summary>
    Requesting,

    /// <summary>Request received and answered; waiting for the initiator to reveal its key.</summary>
    AwaitingReveal,

    /// <summary>The code is ready. The user must compare it with the other screen.</summary>
    Comparing,

    /// <summary>This user approved; waiting for the other one.</summary>
    WaitingForPeer,

    Paired,

    /// <summary>One of the users declined or cancelled.</summary>
    Declined,

    /// <summary>Verification failed, the connection dropped, or the attempt timed out.</summary>
    Failed,
}

public sealed record PairingInfo(
    Guid PairingId,
    Guid DeviceId,
    string PeerName,
    PairingRole Role,
    PairingStage Stage,
    string? Code,
    string LocalFingerprint,
    string RemoteFingerprint,
    string? Message)
{
    public bool IsActive => Stage is PairingStage.Requesting or PairingStage.AwaitingReveal or PairingStage.Comparing or PairingStage.WaitingForPeer;

    public string DisplayId => DeviceIdFormat.ToDisplayId(DeviceId);
}

public sealed class PairingOptions
{
    /// <summary>An attempt not finished within this time fails.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Failed attempts with one device allowed within <see cref="FailureWindow"/>.</summary>
    public int MaxFailures { get; init; } = 5;

    public TimeSpan FailureWindow { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>After this many failures on one session, it is closed and a new connection is required.</summary>
    public int MaxFailuresPerSession { get; init; } = 3;

    /// <summary>
    /// Requests that may reach the user from one network address within <see cref="RequestWindow"/>. Each one interrupts
    /// someone, and a new device ID costs nothing, so counting per device wouldn't stop a computer asking again and again.
    /// </summary>
    public int MaxRequestsPerAddress { get; init; } = 5;

    public TimeSpan RequestWindow { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// Mutual pairing (architecture.md §3): a commit-then-reveal key exchange over an authenticated TLS
/// session, a six-digit code both users compare, and approval on both sides before either side pins
/// the other's fingerprint. One attempt runs at a time.
/// </summary>
public sealed partial class PairingService : IDisposable
{
    private readonly ConnectionManager _connections;
    private readonly ITrustStore _trust;
    private readonly PeerRegistry _registry;
    private readonly ILocalIdentityProvider _identity;
    private readonly IDeviceSigner _signer;
    private readonly PairingOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<PairingService> _logger;
    private readonly IDisposable _registration;

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, List<DateTimeOffset>> _failures = [];
    private readonly Dictionary<Guid, int> _sessionFailures = [];
    private readonly SlidingWindowRateLimiter _requests;
    private Attempt? _current;

    public PairingService(
        ConnectionManager connections,
        ITrustStore trust,
        PeerRegistry registry,
        ILocalIdentityProvider identity,
        IDeviceSigner signer,
        PairingOptions options,
        TimeProvider time,
        ILogger<PairingService> logger)
    {
        _connections = connections;
        _trust = trust;
        _registry = registry;
        _identity = identity;
        _signer = signer;
        _options = options;
        _time = time;
        _logger = logger;
        _requests = new SlidingWindowRateLimiter(time, options.MaxRequestsPerAddress, options.RequestWindow);

        _registration = _connections.Dispatcher.Register(
            [RmpMessageTypes.PairRequest, RmpMessageTypes.PairResponse, RmpMessageTypes.PairReveal, RmpMessageTypes.PairApprove, RmpMessageTypes.PairReject],
            HandleMessageAsync);
    }

    /// <summary>Raised on a background thread whenever <see cref="Current"/> changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when another device asks to pair, so the UI can get the user's attention.</summary>
    public event EventHandler<PairingInfo>? IncomingRequest;

    /// <summary>The active attempt, or the last finished one until <see cref="Dismiss"/>.</summary>
    public PairingInfo? Current
    {
        get
        {
            lock (_gate)
            {
                return _current?.ToInfo(_identity.Identity.CertificateFingerprint);
            }
        }
    }

    /// <summary>Starts pairing with a discovered device, connecting first if necessary.</summary>
    /// <exception cref="InvalidOperationException">Another pairing is in progress.</exception>
    public async Task<PairingInfo> StartAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        if (Current is { IsActive: true })
        {
            throw new InvalidOperationException("Another pairing is already in progress.");
        }

        if (!_connections.TryGetSession(deviceId, out var session))
        {
            var connection = await _connections.ConnectAsync(deviceId, cancellationToken).ConfigureAwait(false);
            if (!_connections.TryGetSession(deviceId, out session))
            {
                return Finish(new Attempt(Guid.NewGuid(), null, deviceId, NameOf(deviceId), PairingRole.Initiator),
                    PairingStage.Failed, connection.LastError ?? "Couldn't connect to the device.");
            }
        }

        Attempt attempt;
        byte[] commitment;
        lock (_gate)
        {
            if (_current is { IsActive: true })
            {
                throw new InvalidOperationException("Another pairing is already in progress.");
            }

            attempt = new Attempt(Guid.NewGuid(), session, deviceId, NameOf(deviceId), PairingRole.Initiator);
            if (TooManyFailures(deviceId))
            {
                _current = attempt;
                attempt.Stage = PairingStage.Failed;
                attempt.Message = "Too many failed pairing attempts with this device. Wait a few minutes, then try again.";
                return Raise(attempt);
            }

            attempt.Ephemeral = PairingProtocol.CreateEphemeralKey();
            attempt.OwnKey = attempt.Ephemeral.PublicKey.ExportSubjectPublicKeyInfo();
            attempt.OwnNonce = PairingProtocol.NewNonce();
            attempt.Commitment = PairingProtocol.Commit(attempt.OwnKey, attempt.OwnNonce);
            attempt.Stage = PairingStage.Requesting;
            commitment = attempt.Commitment;
            ReplaceCurrent(attempt);
        }

        LogStarted(_logger, deviceId, attempt.Id);
        Watch(attempt);
        Send(attempt, RmpMessageTypes.PairRequest, new PairRequestPayload(attempt.Id, commitment));
        return Raise(attempt);
    }

    /// <summary>The user confirmed that both screens show the same code.</summary>
    public void Approve()
    {
        Attempt? attempt;
        bool complete;
        lock (_gate)
        {
            attempt = _current;
            if (attempt is not { Stage: PairingStage.Comparing })
            {
                return;
            }

            attempt.LocalApproved = true;
            complete = attempt.RemoteApproved;
            attempt.Stage = complete ? attempt.Stage : PairingStage.WaitingForPeer;
        }

        Send(attempt, RmpMessageTypes.PairApprove, new PairApprovePayload(attempt.Id));
        if (complete)
        {
            Complete(attempt);
        }
        else
        {
            Raise(attempt);
        }
    }

    /// <summary>Declines or cancels the active attempt. Neither device will trust the other.</summary>
    public void Cancel()
    {
        Attempt? attempt;
        string reason;
        lock (_gate)
        {
            attempt = _current;
            if (attempt is not { IsActive: true })
            {
                return;
            }

            reason = attempt.Stage is PairingStage.Comparing or PairingStage.WaitingForPeer ? PairRejectReasons.Declined : PairRejectReasons.Cancelled;
        }

        Send(attempt, RmpMessageTypes.PairReject, new PairRejectPayload(attempt.Id, reason));
        Finish(attempt, PairingStage.Declined, reason == PairRejectReasons.Declined
            ? "You didn't confirm the code, so the devices were not paired."
            : "You cancelled pairing.", countsAsFailure: reason == PairRejectReasons.Declined);
    }

    /// <summary>Declines the attempt and blocks the device, which also ends its session.</summary>
    public void Block()
    {
        Guid deviceId;
        lock (_gate)
        {
            if (_current is null)
            {
                return;
            }

            deviceId = _current.DeviceId;
        }

        Cancel();
        _trust.Block(deviceId);
        LogBlocked(_logger, deviceId);
    }

    /// <summary>Clears a finished attempt from <see cref="Current"/>.</summary>
    public void Dismiss()
    {
        lock (_gate)
        {
            if (_current is { IsActive: false })
            {
                _current = null;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _registration.Dispose();
        lock (_gate)
        {
            _current?.DisposeKeys();
        }
    }

    private ValueTask HandleMessageAsync(PeerSession session, RmpEnvelope message, CancellationToken cancellationToken)
    {
        var payload = message.Payload;
        switch (message.Type)
        {
            case RmpMessageTypes.PairRequest when PairingMessages.TryReadRequest(payload, out var request):
                OnRequest(session, request);
                break;
            case RmpMessageTypes.PairResponse when PairingMessages.TryReadKey(payload, out var response):
                OnResponse(session, response);
                break;
            case RmpMessageTypes.PairReveal when PairingMessages.TryReadKey(payload, out var reveal):
                OnReveal(session, reveal);
                break;
            case RmpMessageTypes.PairApprove when PairingMessages.TryReadApprove(payload, out var approve):
                OnApprove(session, approve);
                break;
            case RmpMessageTypes.PairReject when PairingMessages.TryReadReject(payload, out var reject):
                OnReject(session, reject);
                break;
            default:
                LogMalformed(_logger, message.Type, session.RemoteDeviceId);
                FailIfCurrent(session, null, "The other device sent an invalid pairing message.");
                break;
        }

        return ValueTask.CompletedTask;
    }

    private void OnRequest(PeerSession session, PairRequestPayload request)
    {
        var localId = _identity.Identity.DeviceId;
        var remoteId = session.RemoteDeviceId;
        Attempt attempt;
        string? refusal = null;

        lock (_gate)
        {
            if (_current is { IsActive: true } current)
            {
                var crossing = current.DeviceId == remoteId && current.Role == PairingRole.Initiator && current.Stage == PairingStage.Requesting;
                if (crossing && ConnectionManager.CompareIds(localId, remoteId) < 0)
                {
                    // Both users pressed Pair at once. The request from the smaller device ID goes ahead, so
                    // ignore theirs; they will see ours and drop their own the same way.
                    return;
                }

                if (!crossing)
                {
                    refusal = PairRejectReasons.Busy;
                }
                else
                {
                    current.DisposeKeys();
                }
            }

            if (refusal is null && (TooManyFailures(remoteId) || _sessionFailures.GetValueOrDefault(remoteId) >= _options.MaxFailuresPerSession || !_requests.TryAcquire(session.RemoteEndPoint.Address)))
            {
                refusal = PairRejectReasons.TooManyAttempts;
            }

            attempt = new Attempt(request.PairingId, session, remoteId, NameOf(remoteId), PairingRole.Responder);
            if (refusal is null)
            {
                attempt.Ephemeral = PairingProtocol.CreateEphemeralKey();
                attempt.OwnKey = attempt.Ephemeral.PublicKey.ExportSubjectPublicKeyInfo();
                attempt.OwnNonce = PairingProtocol.NewNonce();
                attempt.Commitment = request.Commitment;
                attempt.Transcript = PairingProtocol.Transcript(
                    session.RemoteFingerprint, _identity.Identity.CertificateFingerprint,
                    remoteId, localId, request.Commitment, attempt.OwnKey, attempt.OwnNonce);
                attempt.Stage = PairingStage.AwaitingReveal;
                ReplaceCurrent(attempt);
            }
        }

        if (refusal is not null)
        {
            LogRefused(_logger, remoteId, refusal);
            Send(attempt, RmpMessageTypes.PairReject, new PairRejectPayload(request.PairingId, refusal));
            return;
        }

        LogIncoming(_logger, remoteId, attempt.Id);
        Watch(attempt);
        var signature = _signer.SignData(attempt.Transcript);
        Send(attempt, RmpMessageTypes.PairResponse, new PairKeyPayload(attempt.Id, attempt.OwnKey!, attempt.OwnNonce!, signature));
        var info = Raise(attempt);
        IncomingRequest?.Invoke(this, info);
    }

    private void OnResponse(PeerSession session, PairKeyPayload response)
    {
        Attempt attempt;
        lock (_gate)
        {
            if (!IsCurrent(session, response.PairingId, PairingRole.Initiator, PairingStage.Requesting, out attempt!))
            {
                FailUnexpected(session, response.PairingId);
                return;
            }

            var localId = _identity.Identity.DeviceId;
            attempt.Transcript = PairingProtocol.Transcript(
                _identity.Identity.CertificateFingerprint, session.RemoteFingerprint,
                localId, session.RemoteDeviceId, attempt.Commitment!, response.EphemeralKey, response.Nonce);

            // The responder signs with the key from its TLS certificate: this binds the code to the device
            // this session authenticated, not just to whoever sits on the wire.
            if (!PairingProtocol.VerifySignature(session.RemotePublicKey, attempt.Transcript, response.Signature)
                || !PairingProtocol.TryImportEphemeralKey(response.EphemeralKey, out var peerKey))
            {
                attempt.Pending = PairRejectReasons.VerificationFailed;
            }
            else
            {
                using (peerKey)
                {
                    attempt.Code = PairingProtocol.ComputeCode(attempt.Ephemeral!, peerKey, attempt.OwnNonce!, response.Nonce, attempt.Transcript);
                }

                attempt.Stage = PairingStage.Comparing;
            }
        }

        if (attempt.Pending is not null)
        {
            FailVerification(attempt);
            return;
        }

        var signature = _signer.SignData(PairingProtocol.InitiatorSignedData(attempt.Transcript!, attempt.OwnKey!, attempt.OwnNonce!));
        Send(attempt, RmpMessageTypes.PairReveal, new PairKeyPayload(attempt.Id, attempt.OwnKey!, attempt.OwnNonce!, signature));
        attempt.DisposeKeys();
        Raise(attempt);
    }

    private void OnReveal(PeerSession session, PairKeyPayload reveal)
    {
        Attempt attempt;
        lock (_gate)
        {
            if (!IsCurrent(session, reveal.PairingId, PairingRole.Responder, PairingStage.AwaitingReveal, out attempt!))
            {
                FailUnexpected(session, reveal.PairingId);
                return;
            }

            // The initiator committed to this key before seeing ours; a key that doesn't match the commitment
            // means it was chosen afterwards, which is exactly the attack the commitment prevents.
            var valid = PairingProtocol.MatchesCommitment(attempt.Commitment!, reveal.EphemeralKey, reveal.Nonce)
                && PairingProtocol.VerifySignature(session.RemotePublicKey,
                    PairingProtocol.InitiatorSignedData(attempt.Transcript!, reveal.EphemeralKey, reveal.Nonce), reveal.Signature);

            if (valid && PairingProtocol.TryImportEphemeralKey(reveal.EphemeralKey, out var peerKey))
            {
                using (peerKey)
                {
                    attempt.Code = PairingProtocol.ComputeCode(attempt.Ephemeral!, peerKey, reveal.Nonce, attempt.OwnNonce!, attempt.Transcript!);
                }

                attempt.Stage = PairingStage.Comparing;
                attempt.DisposeKeys();
            }
            else
            {
                attempt.Pending = PairRejectReasons.VerificationFailed;
            }
        }

        if (attempt.Pending is not null)
        {
            FailVerification(attempt);
            return;
        }

        Raise(attempt);
    }

    private void OnApprove(PeerSession session, PairApprovePayload approve)
    {
        Attempt attempt;
        bool complete;
        lock (_gate)
        {
            var current = _current;
            if (current is null || current.Session != session || current.Id != approve.PairingId
                || current.Stage is not (PairingStage.Comparing or PairingStage.WaitingForPeer))
            {
                FailUnexpected(session, approve.PairingId);
                return;
            }

            attempt = current;
            attempt.RemoteApproved = true;
            complete = attempt.LocalApproved;
        }

        if (complete)
        {
            Complete(attempt);
        }
        else
        {
            Raise(attempt);
        }
    }

    private void OnReject(PeerSession session, PairRejectPayload reject)
    {
        Attempt? attempt;
        lock (_gate)
        {
            attempt = _current;
            if (attempt is null || attempt.Session != session || attempt.Id != reject.PairingId || !attempt.IsActive)
            {
                return;
            }
        }

        var name = attempt.PeerName;
        var (stage, message) = reject.Reason switch
        {
            PairRejectReasons.Declined => (PairingStage.Declined, $"{name} didn't confirm the code, so the devices were not paired."),
            PairRejectReasons.Cancelled => (PairingStage.Declined, $"{name} cancelled pairing."),
            PairRejectReasons.Busy => (PairingStage.Failed, $"{name} is busy pairing with another device. Try again in a moment."),
            PairRejectReasons.Timeout => (PairingStage.Failed, "Pairing timed out."),
            PairRejectReasons.TooManyAttempts => (PairingStage.Failed, $"{name} refused: too many failed attempts. Wait a few minutes, then try again."),
            PairRejectReasons.VerificationFailed => (PairingStage.Failed, $"{name} couldn't verify this computer's pairing data. The connection may have been tampered with."),
            _ => (PairingStage.Failed, $"{name} ended pairing."),
        };

        Finish(attempt, stage, message, countsAsFailure: reject.Reason is not (PairRejectReasons.Busy or PairRejectReasons.Cancelled));
    }

    private void Complete(Attempt attempt)
    {
        var session = attempt.Session!;
        _trust.Trust(new TrustedPeer(attempt.DeviceId, attempt.PeerName, session.RemoteFingerprint, _time.GetUtcNow()));
        _connections.MarkTrusted(session);

        lock (_gate)
        {
            _failures.Remove(attempt.DeviceId);
            _sessionFailures.Remove(attempt.DeviceId);
        }

        LogPaired(_logger, attempt.DeviceId, session.RemoteFingerprint);
        Finish(attempt, PairingStage.Paired, $"This computer and {attempt.PeerName} are now paired.");
    }

    private void FailVerification(Attempt attempt)
    {
        LogVerificationFailed(_logger, attempt.DeviceId, attempt.Id);
        Send(attempt, RmpMessageTypes.PairReject, new PairRejectPayload(attempt.Id, PairRejectReasons.VerificationFailed));
        Finish(attempt, PairingStage.Failed,
            $"The pairing data from {attempt.PeerName} didn't verify. Someone may be interfering with the connection. The devices were not paired.");
    }

    // Must be called with the lock held. A pairing message that doesn't fit the current attempt is a protocol
    // violation; only the current attempt on the same session is failed by it.
    private void FailUnexpected(PeerSession session, Guid pairingId)
    {
        LogUnexpected(_logger, session.RemoteDeviceId, pairingId);
        if (_current is { IsActive: true } current && current.Session == session && current.Id == pairingId)
        {
            current.Pending = PairRejectReasons.VerificationFailed;
            _ = Task.Run(() => FailVerification(current));
        }
    }

    private void FailIfCurrent(PeerSession session, Guid? pairingId, string message)
    {
        Attempt? attempt;
        lock (_gate)
        {
            attempt = _current is { IsActive: true } current && current.Session == session && (pairingId is null || current.Id == pairingId)
                ? current
                : null;
        }

        if (attempt is not null)
        {
            Send(attempt, RmpMessageTypes.PairReject, new PairRejectPayload(attempt.Id, PairRejectReasons.VerificationFailed));
            Finish(attempt, PairingStage.Failed, message);
        }
    }

    private void Watch(Attempt attempt)
    {
        _ = Task.Run(async () =>
        {
            var closed = attempt.Session!.Completion;
            var timeout = Task.Delay(_options.Timeout, _time);
            var first = await Task.WhenAny(closed, timeout).ConfigureAwait(false);

            if (!IsStillActive(attempt))
            {
                return;
            }

            if (first == timeout)
            {
                Send(attempt, RmpMessageTypes.PairReject, new PairRejectPayload(attempt.Id, PairRejectReasons.Timeout));
                Finish(attempt, PairingStage.Failed, "Pairing timed out. Start again when both people are ready.");
            }
            else
            {
                Finish(attempt, PairingStage.Failed, $"The connection to {attempt.PeerName} was lost.", countsAsFailure: false);
            }
        });
    }

    private PairingInfo Finish(Attempt attempt, PairingStage stage, string message, bool countsAsFailure = true)
    {
        var closeSession = false;
        lock (_gate)
        {
            if (_current is not null && _current != attempt && _current.IsActive)
            {
                // A newer attempt has taken over; this one is already abandoned.
                return attempt.ToInfo(_identity.Identity.CertificateFingerprint);
            }

            if (attempt.IsActive || attempt.Session is null)
            {
                attempt.Stage = stage;
                attempt.Message = message;
                attempt.DisposeKeys();
                _current = attempt;

                if (stage != PairingStage.Paired && countsAsFailure)
                {
                    var now = _time.GetUtcNow();
                    var list = _failures.TryGetValue(attempt.DeviceId, out var existing) ? existing : _failures[attempt.DeviceId] = [];
                    list.Add(now);
                    list.RemoveAll(t => now - t > _options.FailureWindow);

                    var perSession = _sessionFailures[attempt.DeviceId] = _sessionFailures.GetValueOrDefault(attempt.DeviceId) + 1;
                    closeSession = perSession >= _options.MaxFailuresPerSession;
                }
            }
        }

        if (closeSession && attempt.Session is { } session)
        {
            // "Require a new session after repeated failures" (architecture.md §3.1).
            LogTooManyFailures(_logger, attempt.DeviceId);
            lock (_gate)
            {
                _sessionFailures.Remove(attempt.DeviceId);
            }

            _ = session.CloseAsync(GoodbyeReasons.Shutdown, "Too many failed pairing attempts.");
        }

        return Raise(attempt);
    }

    private bool IsStillActive(Attempt attempt)
    {
        lock (_gate)
        {
            return _current == attempt && attempt.IsActive;
        }
    }

    // Lock held.
    private bool IsCurrent(PeerSession session, Guid pairingId, PairingRole role, PairingStage stage, out Attempt? attempt)
    {
        attempt = _current;
        return attempt is not null && attempt.Session == session && attempt.Id == pairingId && attempt.Role == role && attempt.Stage == stage;
    }

    // Lock held.
    private bool TooManyFailures(Guid deviceId)
    {
        if (!_failures.TryGetValue(deviceId, out var list))
        {
            return false;
        }

        var now = _time.GetUtcNow();
        list.RemoveAll(t => now - t > _options.FailureWindow);
        return list.Count >= _options.MaxFailures;
    }

    // Lock held.
    private void ReplaceCurrent(Attempt attempt)
    {
        if (_current is not null && _current != attempt)
        {
            _current.DisposeKeys();
        }

        _current = attempt;
    }

    private string NameOf(Guid deviceId) =>
        _registry.TryGet(deviceId, out var peer) ? peer.Username
        : _trust.FindTrusted(deviceId)?.Username ?? DeviceIdFormat.ToDisplayId(deviceId);

    private void Send<T>(Attempt attempt, string type, T payload)
    {
        if (attempt.Session is not { } session)
        {
            return;
        }

        // Queued per attempt, so messages leave in the order they were decided (REVEAL before APPROVE).
        var element = SessionPayloads.ToElement(payload);
        lock (attempt.SendGate)
        {
            attempt.SendQueue = attempt.SendQueue.ContinueWith(
                _ => SendCoreAsync(session, type, element, attempt.DeviceId),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default).Unwrap();
        }
    }

    private async Task SendCoreAsync(PeerSession session, string type, JsonElement payload, Guid deviceId)
    {
        try
        {
            await session.SendAsync(type, payload).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            LogSendFailed(_logger, type, deviceId, ex.Message);
        }
    }

    private PairingInfo Raise(Attempt attempt)
    {
        var info = attempt.ToInfo(_identity.Identity.CertificateFingerprint);
        Changed?.Invoke(this, EventArgs.Empty);
        return info;
    }

    private sealed class Attempt(Guid id, PeerSession? session, Guid deviceId, string peerName, PairingRole role)
    {
        public Guid Id { get; } = id;
        public PeerSession? Session { get; } = session;
        public Guid DeviceId { get; } = deviceId;
        public string PeerName { get; } = peerName;
        public PairingRole Role { get; } = role;
        public PairingStage Stage { get; set; } = PairingStage.Failed;
        public ECDiffieHellman? Ephemeral { get; set; }
        public byte[]? OwnKey { get; set; }
        public byte[]? OwnNonce { get; set; }
        public byte[]? Commitment { get; set; }
        public byte[]? Transcript { get; set; }
        public string? Code { get; set; }
        public bool LocalApproved { get; set; }
        public bool RemoteApproved { get; set; }
        public string? Message { get; set; }

        public Lock SendGate { get; } = new();

        public Task SendQueue { get; set; } = Task.CompletedTask;

        /// <summary>A rejection decided under the lock, to be sent after it is released.</summary>
        public string? Pending { get; set; }

        public bool IsActive => Stage is PairingStage.Requesting or PairingStage.AwaitingReveal or PairingStage.Comparing or PairingStage.WaitingForPeer;

        /// <summary>The ephemeral key is only for deriving the code (architecture.md §3.1); drop it as soon as possible.</summary>
        public void DisposeKeys()
        {
            Ephemeral?.Dispose();
            Ephemeral = null;
        }

        public PairingInfo ToInfo(string localFingerprint) => new(
            Id, DeviceId, PeerName, Role, Stage, Code, localFingerprint, Session?.RemoteFingerprint ?? string.Empty, Message);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Pairing {PairingId} with {DeviceId} started")]
    private static partial void LogStarted(ILogger logger, Guid deviceId, Guid pairingId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pairing request {PairingId} from {DeviceId}")]
    private static partial void LogIncoming(ILogger logger, Guid deviceId, Guid pairingId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Refused pairing request from {DeviceId}: {Reason}")]
    private static partial void LogRefused(ILogger logger, Guid deviceId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Paired with {DeviceId}, pinned {Fingerprint}")]
    private static partial void LogPaired(ILogger logger, Guid deviceId, string fingerprint);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pairing {PairingId} with {DeviceId} failed verification")]
    private static partial void LogVerificationFailed(ILogger logger, Guid deviceId, Guid pairingId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Unexpected pairing message {PairingId} from {DeviceId}")]
    private static partial void LogUnexpected(ILogger logger, Guid deviceId, Guid pairingId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Malformed {Type} from {DeviceId}")]
    private static partial void LogMalformed(ILogger logger, string type, Guid deviceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Too many failed pairing attempts with {DeviceId}; closing the session")]
    private static partial void LogTooManyFailures(ILogger logger, Guid deviceId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Blocked {DeviceId}")]
    private static partial void LogBlocked(ILogger logger, Guid deviceId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Couldn't send {Type} to {DeviceId}: {Reason}")]
    private static partial void LogSendFailed(ILogger logger, string type, Guid deviceId, string reason);
}
