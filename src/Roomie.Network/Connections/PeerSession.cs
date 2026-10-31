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
/// One authenticated TLS session with a peer, after the HELLO exchange. Handles heartbeats, <c>GOODBYE</c>
/// and the pre-trust message gate; other messages are raised through <see cref="MessageReceived"/>.
/// </summary>
public sealed partial class PeerSession : IAsyncDisposable
{
    private static readonly TimeSpan GoodbyeTimeout = TimeSpan.FromSeconds(2);

    private readonly SslStream _stream;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly TimeSpan _heartbeatInterval;
    private readonly int _missedHeartbeats;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource<SessionCloseInfo> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _lastReceivedTicks;
    private int _closing;

    internal PeerSession(
        SslStream stream,
        Guid localDeviceId,
        RemoteDeviceCertificate peer,
        bool isTrusted,
        SessionDirection direction,
        IPEndPoint remoteEndPoint,
        HelloPayload remoteHello,
        TimeSpan heartbeatInterval,
        int missedHeartbeats,
        TimeProvider time,
        ILogger logger)
    {
        _stream = stream;
        _time = time;
        _logger = logger;
        _heartbeatInterval = heartbeatInterval;
        _missedHeartbeats = missedHeartbeats;

        LocalDeviceId = localDeviceId;
        RemoteDeviceId = peer.DeviceId;
        RemoteFingerprint = peer.Fingerprint;
        IsTrusted = isTrusted;
        Direction = direction;
        RemoteEndPoint = remoteEndPoint;
        RemoteHello = remoteHello;
        Security = TlsHandshake.Describe(stream);
        EstablishedAt = time.GetUtcNow();
        _lastReceivedTicks = EstablishedAt.UtcTicks;
    }

    /// <summary>Raised on a background thread for every message the session doesn't handle itself.</summary>
    public event EventHandler<RmpEnvelope>? MessageReceived;

    public Guid SessionId { get; } = Guid.NewGuid();

    public Guid LocalDeviceId { get; }

    public Guid RemoteDeviceId { get; }

    public string RemoteFingerprint { get; }

    public bool IsTrusted { get; }

    public SessionDirection Direction { get; }

    public IPEndPoint RemoteEndPoint { get; }

    public HelloPayload RemoteHello { get; }

    public string Security { get; }

    public DateTimeOffset EstablishedAt { get; }

    /// <summary>The device that opened the TCP connection; decides which duplicate survives (architecture.md §4.2).</summary>
    public Guid InitiatorDeviceId => Direction == SessionDirection.Outbound ? LocalDeviceId : RemoteDeviceId;

    public Task<SessionCloseInfo> Completion => _closed.Task;

    public bool IsClosed => Volatile.Read(ref _closing) != 0;

    internal void Start()
    {
        _ = Task.Run(ReadLoopAsync);
        _ = Task.Run(HeartbeatLoopAsync);
    }

    /// <exception cref="InvalidOperationException">The session is closed.</exception>
    public async Task SendAsync(string type, JsonElement payload, CancellationToken cancellationToken = default)
    {
        var envelope = RmpEnvelope.Create(type, LocalDeviceId, RemoteDeviceId, _time.GetUtcNow(), payload);
        await WriteAsync(envelope, cancellationToken).ConfigureAwait(false);
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

    private Task FailAsync(string code, string message) =>
        ShutdownAsync(new SessionCloseInfo(SessionCloseKind.ProtocolError, message, code),
            (RmpMessageTypes.Error, SessionPayloads.ToElement(new ErrorPayload(code, message))));

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
                    await FailAsync(RmpErrorCodes.InvalidFrame, "The message's sender or recipient doesn't match this session.").ConfigureAwait(false);
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
            case RmpMessageTypes.Heartbeat:
                await WriteAsync(RmpEnvelope.Create(RmpMessageTypes.HeartbeatAck, LocalDeviceId, RemoteDeviceId, _time.GetUtcNow(), SessionPayloads.Empty), cancellationToken).ConfigureAwait(false);
                return true;

            case RmpMessageTypes.HeartbeatAck:
                return true;

            case RmpMessageTypes.Hello:
                await FailAsync(RmpErrorCodes.InvalidFrame, "HELLO was sent twice.").ConfigureAwait(false);
                return false;

            case RmpMessageTypes.Goodbye:
                var reason = SessionPayloads.TryReadGoodbye(envelope.Payload, out var goodbye) ? goodbye.Reason : null;
                await ShutdownAsync(new SessionCloseInfo(SessionCloseKind.RemoteGoodbye, DescribeGoodbye(reason), reason), null).ConfigureAwait(false);
                return false;

            case RmpMessageTypes.Error:
                if (SessionPayloads.TryReadError(envelope.Payload, out var reported))
                {
                    LogPeerReportedError(_logger, RemoteDeviceId, reported.Code, reported.Message);
                }

                break;
        }

        if (!IsTrusted && !RmpMessageTypes.IsAllowedBeforeTrust(envelope.Type))
        {
            LogUntrustedMessageRefused(_logger, envelope.Type, RemoteDeviceId);
            var refusal = new ErrorPayload(RmpErrorCodes.PeerNotTrusted, "This device has not been paired.", envelope.MessageId);
            await WriteAsync(RmpEnvelope.Create(RmpMessageTypes.Error, LocalDeviceId, RemoteDeviceId, _time.GetUtcNow(), SessionPayloads.ToElement(refusal)), cancellationToken).ConfigureAwait(false);
            return true;
        }

        MessageReceived?.Invoke(this, envelope);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused {Type} from unpaired device {DeviceId}")]
    private static partial void LogUntrustedMessageRefused(ILogger logger, string type, Guid deviceId);
}
