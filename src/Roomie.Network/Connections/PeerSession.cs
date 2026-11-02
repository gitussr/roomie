using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Roomie.Core.Connections;
using Roomie.Core.Protocol;

namespace Roomie.Network.Connections;

public enum SessionCloseKind
{
    /// <summary>This computer closed the session.</summary>
    Local,

    /// <summary>The peer sent <c>GOODBYE</c>.</summary>
    RemoteGoodbye,

    /// <summary>The TCP connection ended or failed.</summary>
    ConnectionLost,

    /// <summary>Nothing arrived from the peer for the missed-heartbeat threshold.</summary>
    HeartbeatTimeout,

    /// <summary>One side broke the protocol and the session was closed after an <c>ERROR</c>.</summary>
    ProtocolError,
}

/// <param name="Reason">The <c>GOODBYE</c> reason or <c>ERROR</c> code involved, if any.</param>
public sealed record SessionCloseInfo(SessionCloseKind Kind, string Description, string? Reason = null);

/// <summary>
/// One authenticated TLS session with a peer, after the HELLO exchange. Handles heartbeats and <c>GOODBYE</c>,
/// applies <see cref="RmpMessagePolicy"/> to everything else, and hands what passes to the dispatcher.
/// A frame that can't be parsed, or that claims the wrong sender, closes the session. A well-formed message
/// that is refused only gets an <c>ERROR</c>, unless the peer keeps sending them.
/// </summary>
public sealed partial class PeerSession : IAsyncDisposable
{
    private static readonly TimeSpan GoodbyeTimeout = TimeSpan.FromSeconds(2);

    private readonly SslStream _stream;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly TimeSpan _heartbeatInterval;
    private readonly int _missedHeartbeats;
    private readonly RmpDispatcher _dispatcher;
    private readonly RmpNegotiatedSession _negotiated;
    private readonly SlidingWindowRateLimiter _rejections;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource<SessionCloseInfo> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _lastReceivedTicks;
    private int _closing;
    private volatile bool _isTrusted;

    internal PeerSession(
        SslStream stream,
        Guid localDeviceId,
        RemoteDeviceCertificate peer,
        bool isTrusted,
        SessionDirection direction,
        IPEndPoint remoteEndPoint,
        HelloPayload remoteHello,
        RmpNegotiatedSession negotiated,
        RmpDispatcher dispatcher,
        ConnectionOptions options,
        TimeProvider time,
        ILogger logger)
    {
        _stream = stream;
        _time = time;
        _logger = logger;
        _heartbeatInterval = options.HeartbeatInterval;
        _missedHeartbeats = options.MissedHeartbeatThreshold;
        _dispatcher = dispatcher;
        _negotiated = negotiated;
        _rejections = new SlidingWindowRateLimiter(time, options.MaxRejectedMessagesPerMinute, TimeSpan.FromMinutes(1), maxTracked: 1);

        LocalDeviceId = localDeviceId;
        RemoteDeviceId = peer.DeviceId;
        RemoteFingerprint = peer.Fingerprint;
        RemotePublicKey = peer.PublicKey;
        _isTrusted = isTrusted;
        Direction = direction;
        RemoteEndPoint = remoteEndPoint;
        RemoteHello = remoteHello;
        Security = TlsHandshake.Describe(stream);
        EstablishedAt = time.GetUtcNow();
        _lastReceivedTicks = EstablishedAt.UtcTicks;
    }

    public Guid SessionId { get; } = Guid.NewGuid();

    public Guid LocalDeviceId { get; }

    public Guid RemoteDeviceId { get; }

    public string RemoteFingerprint { get; }

    /// <summary>DER SubjectPublicKeyInfo of the key the peer authenticated with in TLS.</summary>
    public byte[] RemotePublicKey { get; }

    /// <summary>
    /// Whether the peer's pinned fingerprint matched when the session opened, or pairing completed on it.
    /// Until then only session and pairing messages are accepted.
    /// </summary>
    public bool IsTrusted => _isTrusted;

    public SessionDirection Direction { get; }

    public IPEndPoint RemoteEndPoint { get; }

    public HelloPayload RemoteHello { get; }

    /// <summary>The protocol version both sides agreed on.</summary>
    public int ProtocolVersion => _negotiated.ProtocolVersion;

    /// <summary>The capabilities both sides advertised. Check with <see cref="Supports"/> before using a feature.</summary>
    public IReadOnlySet<string> Capabilities => _negotiated.Capabilities;

    public string Security { get; }

    public DateTimeOffset EstablishedAt { get; }

    /// <summary>The device that opened the TCP connection; decides which duplicate survives (architecture.md §4.2).</summary>
    public Guid InitiatorDeviceId => Direction == SessionDirection.Outbound ? LocalDeviceId : RemoteDeviceId;

    public Task<SessionCloseInfo> Completion => _closed.Task;

    public bool IsClosed => Volatile.Read(ref _closing) != 0;

    /// <summary>Whether both sides agreed on this optional feature for this session.</summary>
    public bool Supports(string capability) => _negotiated.Capabilities.Contains(capability);

    internal void MarkTrusted() => _isTrusted = true;

    internal void Start()
    {
        _ = Task.Run(ReadLoopAsync);
        _ = Task.Run(HeartbeatLoopAsync);
    }

    /// <summary>
    /// Sends a message, applying the same rules a receiver would, so this device never sends what the other
    /// side would have to refuse.
    /// </summary>
    /// <exception cref="InvalidOperationException">The session is closed, or the message isn't allowed on it.</exception>
    public async Task SendAsync(string type, JsonElement payload, CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        await SendAsync(type, payload, Guid.CreateVersion7(now), now, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a message under an ID and timestamp chosen by the caller. A retry must reuse both: the ID is how
    /// the recipient recognizes a message it already has (architecture.md §6).
    /// </summary>
    /// <exception cref="InvalidOperationException">The session is closed, or the message isn't allowed on it.</exception>
    public async Task SendAsync(string type, JsonElement payload, Guid messageId, DateTimeOffset timestamp, CancellationToken cancellationToken = default)
    {
        if (RmpMessagePolicy.Check(type, payload, IsTrusted, Capabilities) is { } refused)
        {
            throw new InvalidOperationException($"{type} can't be sent on this session: {refused.Message}");
        }

        await WriteAsync(new RmpEnvelope(type, messageId, LocalDeviceId, RemoteDeviceId, timestamp, payload), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Closes the session, first telling the peer why when <paramref name="goodbyeReason"/> is given.</summary>
    public Task CloseAsync(string? goodbyeReason, string description) =>
        ShutdownAsync(new SessionCloseInfo(SessionCloseKind.Local, description, goodbyeReason),
            goodbyeReason is null ? null : (RmpMessageTypes.Goodbye, SessionPayloads.ToElement(new GoodbyePayload(goodbyeReason))));

    /// <summary>Closes the session without a GOODBYE and releases its resources.</summary>
    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync(new SessionCloseInfo(SessionCloseKind.Local, "Disposed."), null).ConfigureAwait(false);
        _writeLock.Dispose();
        _lifetime.Dispose();
    }

    private Task FailAsync(string code, string message, Guid? requestId = null) =>
        ShutdownAsync(new SessionCloseInfo(SessionCloseKind.ProtocolError, message, code),
            (RmpMessageTypes.Error, SessionPayloads.ToElement(new ErrorPayload(code, message, requestId))));

    private async Task ReadLoopAsync()
    {
        var cancellationToken = _lifetime.Token;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await RmpFraming.ReadAsync(_stream, cancellationToken).ConfigureAwait(false);
                if (frame is null)
                {
                    await ShutdownAsync(new SessionCloseInfo(SessionCloseKind.ConnectionLost, "The other device closed the connection."), null).ConfigureAwait(false);
                    return;
                }

                Interlocked.Exchange(ref _lastReceivedTicks, _time.GetUtcNow().UtcTicks);

                if (frame.Value.Type != RmpFrameType.Control)
                {
                    // File transfer arrives in phase 9; until then a chunk frame is a protocol violation.
                    await FailAsync(RmpErrorCodes.InvalidFrame, "File chunks are not supported on this session.").ConfigureAwait(false);
                    return;
                }

                if (!RmpEnvelopeCodec.TryDecode(frame.Value.Payload, out var envelope, out var error))
                {
                    await FailAsync(RmpErrorCodes.InvalidFrame, error).ConfigureAwait(false);
                    return;
                }

                // The authenticated TLS session identifies the sender; the envelope must agree with it.
                if (envelope.SenderId != RemoteDeviceId || envelope.RecipientId != LocalDeviceId)
                {
                    await FailAsync(RmpErrorCodes.InvalidFrame, "The message's sender or recipient doesn't match this session.", envelope.MessageId).ConfigureAwait(false);
                    return;
                }

                if (!await HandleAsync(envelope, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }
        }
        catch (RmpProtocolException ex)
        {
            await FailAsync(ex.Code, ex.Message).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or AuthenticationException or InvalidOperationException)
        {
            await ShutdownAsync(new SessionCloseInfo(SessionCloseKind.ConnectionLost, "The connection was lost."), null).ConfigureAwait(false);
        }
    }

    /// <returns>False when the session has ended.</returns>
    private async Task<bool> HandleAsync(RmpEnvelope envelope, CancellationToken cancellationToken)
    {
        switch (envelope.Type)
        {
            case RmpMessageTypes.Hello:
                await FailAsync(RmpErrorCodes.InvalidFrame, "HELLO was sent twice.", envelope.MessageId).ConfigureAwait(false);
                return false;

            case RmpMessageTypes.Goodbye:
                var reason = SessionPayloads.TryReadGoodbye(envelope.Payload, out var goodbye) ? goodbye.Reason : null;
                await ShutdownAsync(new SessionCloseInfo(SessionCloseKind.RemoteGoodbye, DescribeGoodbye(reason), reason), null).ConfigureAwait(false);
                return false;

            case RmpMessageTypes.Error:
                await HandleErrorAsync(envelope, cancellationToken).ConfigureAwait(false);
                return true;
        }

        if (RmpMessagePolicy.Check(envelope.Type, envelope.Payload, IsTrusted, Capabilities) is { } refused)
        {
            return await RejectAsync(envelope, refused, cancellationToken).ConfigureAwait(false);
        }

        switch (envelope.Type)
        {
            case RmpMessageTypes.Heartbeat:
                await WriteAsync(RmpEnvelope.Create(RmpMessageTypes.HeartbeatAck, LocalDeviceId, RemoteDeviceId, _time.GetUtcNow(), SessionPayloads.Empty), cancellationToken).ConfigureAwait(false);
                return true;

            case RmpMessageTypes.HeartbeatAck:
                return true;
        }

        return await _dispatcher.DispatchAsync(this, envelope, cancellationToken).ConfigureAwait(false) is { } failed
            ? await RejectAsync(envelope, failed, cancellationToken).ConfigureAwait(false)
            : true;
    }

    /// <summary>An <c>ERROR</c> is never answered with another one, whatever is wrong with it: two devices could do that forever.</summary>
    private async Task HandleErrorAsync(RmpEnvelope envelope, CancellationToken cancellationToken)
    {
        if (!SessionPayloads.TryReadError(envelope.Payload, out var reported))
        {
            LogMalformedError(_logger, RemoteDeviceId);
            return;
        }

        LogPeerReportedError(_logger, RemoteDeviceId, reported.Code, reported.Message);
        await _dispatcher.DispatchErrorAsync(this, envelope, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Refuses one message and keeps the session, unless the peer has used up its allowance of refusals.</summary>
    /// <returns>False when the session has ended.</returns>
    private async Task<bool> RejectAsync(RmpEnvelope envelope, RmpRejection rejection, CancellationToken cancellationToken)
    {
        LogMessageRefused(_logger, envelope.Type, RemoteDeviceId, rejection.Code);

        if (!_rejections.TryAcquire(RemoteEndPoint.Address))
        {
            await FailAsync(RmpErrorCodes.RateLimited, "Too many refused messages.", envelope.MessageId).ConfigureAwait(false);
            return false;
        }

        var error = new ErrorPayload(rejection.Code, rejection.Message, envelope.MessageId);
        await WriteAsync(RmpEnvelope.Create(RmpMessageTypes.Error, LocalDeviceId, RemoteDeviceId, _time.GetUtcNow(), SessionPayloads.ToElement(error)), cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task HeartbeatLoopAsync()
    {
        var cancellationToken = _lifetime.Token;
        var timeout = _heartbeatInterval * _missedHeartbeats;
        try
        {
            using var timer = new PeriodicTimer(_heartbeatInterval, _time);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var silence = _time.GetUtcNow() - new DateTimeOffset(Interlocked.Read(ref _lastReceivedTicks), TimeSpan.Zero);
                if (silence > timeout)
                {
                    await ShutdownAsync(new SessionCloseInfo(SessionCloseKind.HeartbeatTimeout, $"No response from the other device for {timeout.TotalSeconds:0} seconds."), null).ConfigureAwait(false);
                    return;
                }

                await SendAsync(RmpMessageTypes.Heartbeat, SessionPayloads.Empty, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            await ShutdownAsync(new SessionCloseInfo(SessionCloseKind.ConnectionLost, "The connection was lost."), null).ConfigureAwait(false);
        }
    }

    private async Task WriteAsync(RmpEnvelope envelope, CancellationToken cancellationToken)
    {
        var frame = RmpFraming.Encode(RmpFrameType.Control, RmpEnvelopeCodec.Encode(envelope));
        if (IsClosed)
        {
            throw new InvalidOperationException("The session is closed.");
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ShutdownAsync(SessionCloseInfo info, (string Type, JsonElement Payload)? lastMessage)
    {
        if (Interlocked.Exchange(ref _closing, 1) != 0)
        {
            await _closed.Task.ConfigureAwait(false);
            return;
        }

        if (lastMessage is { } message)
        {
            // Best effort: a dead connection must not hold up closing.
            try
            {
                using var timeout = new CancellationTokenSource(GoodbyeTimeout);
                var frame = RmpFraming.Encode(RmpFrameType.Control, RmpEnvelopeCodec.Encode(
                    RmpEnvelope.Create(message.Type, LocalDeviceId, RemoteDeviceId, _time.GetUtcNow(), message.Payload)));
                await _writeLock.WaitAsync(timeout.Token).ConfigureAwait(false);
                try
                {
                    await _stream.WriteAsync(frame, timeout.Token).ConfigureAwait(false);
                    await _stream.FlushAsync(timeout.Token).ConfigureAwait(false);
                }
                finally
                {
                    _writeLock.Release();
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
            }
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _stream.DisposeAsync().ConfigureAwait(false);
        LogSessionClosed(_logger, RemoteDeviceId, info.Kind, info.Description);
        _closed.TrySetResult(info);
    }

    private static string DescribeGoodbye(string? reason) => reason switch
    {
        GoodbyeReasons.Shutdown => "The other device disconnected.",
        GoodbyeReasons.DuplicateSession => "Replaced by another connection to the same device.",
        GoodbyeReasons.PeerBlocked => "The other device has blocked this computer.",
        _ => "The other device ended the session.",
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Session with {DeviceId} closed ({Kind}): {Description}")]
    private static partial void LogSessionClosed(ILogger logger, Guid deviceId, SessionCloseKind kind, string description);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{DeviceId} reported {Code}: {Message}")]
    private static partial void LogPeerReportedError(ILogger logger, Guid deviceId, string code, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{DeviceId} sent a malformed ERROR")]
    private static partial void LogMalformedError(ILogger logger, Guid deviceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused {Type} from {DeviceId}: {Code}")]
    private static partial void LogMessageRefused(ILogger logger, string type, Guid deviceId, string code);
}
