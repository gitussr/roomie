using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Roomie.Core.Files;
using Roomie.Core.Messaging;
using Roomie.Core.Protocol;
using Roomie.Core.Trust;
using Roomie.Network.Connections;

namespace Roomie.Network.Files;

public sealed class FileTransferOptions
{
    /// <summary>Where verified files end up.</summary>
    public required string DownloadsDirectory { get; init; }

    /// <summary>Where files stay until they are verified. Emptied at startup.</summary>
    public required string IncomingDirectory { get; init; }

    /// <summary>Files moving at once with each device, in each direction (architecture.md §8).</summary>
    public int MaxActivePerDevice { get; init; } = 2;

    /// <summary>Unanswered offers kept from one device. More are declined, so a device can't flood the list.</summary>
    public int MaxOpenOffersPerDevice { get; init; } = 20;

    /// <summary>Space that must stay free on the disk after accepting a file.</summary>
    public long MinFreeBytesAfterAccept { get; init; } = 50L * 1024 * 1024;

    /// <summary>A write that takes longer means the connection is stuck, and the session is closed.</summary>
    public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How often progress may refresh the screen.</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(250);
}

/// <summary>Null <see cref="Error"/> means it worked.</summary>
public sealed record FileActionResult(Guid? TransferId, string? Error);

/// <summary>
/// One-to-one file transfer (architecture.md §8). Nothing is written until the receiver's user accepts, and a
/// file reaches the downloads folder only after its hash matches. Interrupted transfers aren't resumed: both
/// sides mark them failed, and the sender can send the file again.
/// </summary>
public sealed partial class FileTransferService : BackgroundService
{
    private const string OutgoingContentType = "application/octet-stream";

    private static readonly FileTransferState[] OpenStates =
    [
        FileTransferState.Waiting, FileTransferState.Offered, FileTransferState.Accepted, FileTransferState.Transferring, FileTransferState.Verifying,
    ];

    private readonly ConnectionManager _connections;
    private readonly IFileTransferStore _store;
    private readonly ITrustStore _trust;
    private readonly FileTransferOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<FileTransferService> _logger;
    private readonly IDisposable _registration;
    private readonly Channel<Guid> _work = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Receiving> _receiving = [];
    private readonly Dictionary<Guid, Sending> _sending = [];
    private readonly Dictionary<(Guid Peer, Guid Transfer), long> _progress = [];
    private long _lastProgressTicks;

    public FileTransferService(
        ConnectionManager connections,
        IFileTransferStore store,
        ITrustStore trust,
        FileTransferOptions options,
        TimeProvider time,
        ILogger<FileTransferService> logger)
    {
        _connections = connections;
        _store = store;
        _trust = trust;
        _options = options;
        _time = time;
        _logger = logger;

        _registration = connections.Dispatcher.Register(
            [RmpMessageTypes.FileOffer, RmpMessageTypes.FileAccept, RmpMessageTypes.FileReject, RmpMessageTypes.FileComplete, RmpMessageTypes.FileAck],
            HandleMessageAsync);
        connections.Dispatcher.RegisterChunks(HandleChunkAsync);
        connections.SessionOpened += OnSessionOpened;

        RecoverAfterRestart();
    }

    /// <summary>Raised on any thread when a transfer changes stage.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised on any thread, a few times a second at most, while data is moving.</summary>
    public event EventHandler? ProgressChanged;

    /// <summary>Raised when another device offers a file.</summary>
    public event EventHandler? IncomingOffer;

    /// <summary>Hashes a file and offers it. A device that is offline gets the offer when it reconnects.</summary>
    public async Task<FileActionResult> SendFileAsync(Guid peerId, string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (_trust.FindTrusted(peerId) is null || _trust.IsBlocked(peerId))
        {
            return new FileActionResult(null, "Pair with this device before sending it files.");
        }

        string sha256;
        long size;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return new FileActionResult(null, "That file doesn't exist any more.");
            }

            size = info.Length;
            if (size > RmpContentLimits.MaxFileSizeBytes)
            {
                return new FileActionResult(null, $"This file is too large. The limit is {RmpContentLimits.MaxFileSizeBytes / (1024 * 1024)} MB.");
            }

            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (stream.ConfigureAwait(false))
            {
                sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
                size = stream.Length;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FileActionResult(null, "Roomie couldn't read that file. It may be open in another program.");
        }

        // The name sent is what any receiver will make safe anyway. Doing it here keeps it within the protocol's rules.
        var filename = SafeFilename.Create(Path.GetFileName(path));
        var transfer = new StoredTransfer(Guid.CreateVersion7(_time.GetUtcNow()), peerId, MessageDirection.Outgoing, filename, size, sha256, OutgoingContentType, FileTransferState.Waiting, _time.GetUtcNow(), path);
        try
        {
            _store.AddTransfer(transfer);
        }
        catch (MessageStoreException)
        {
            return new FileActionResult(null, "Roomie couldn't save this transfer, so it wasn't started.");
        }

        RaiseChanged();
        _work.Writer.TryWrite(peerId);
        return new FileActionResult(transfer.TransferId, null);
    }

    /// <summary>Accepts an offer. The other device must be connected, because it is waiting to send.</summary>
    public async Task<FileActionResult> AcceptAsync(Guid peerId, Guid transferId, CancellationToken cancellationToken = default)
    {
        try
        {
            if (_store.GetTransfer(peerId, MessageDirection.Incoming, transferId) is not { State: FileTransferState.Offered } offer)
            {
                return new FileActionResult(transferId, "This offer is no longer open.");
            }

            if (!_connections.TryGetSession(peerId, out var session) || !session.IsTrusted)
            {
                return new FileActionResult(transferId, "That device isn't connected right now. Try again when it's back.");
            }

            if (FreeSpace() is { } free && free < offer.FileSize + _options.MinFreeBytesAfterAccept)
            {
                return new FileActionResult(transferId, "There isn't enough free disk space for this file.");
            }

            var temporary = TemporaryPath(transferId);
            var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous);
            lock (_gate)
            {
                _receiving[transferId] = new Receiving(peerId, session.SessionId, offer, stream, temporary);
            }

            if (!_store.TryMoveTransfer(peerId, MessageDirection.Incoming, transferId, FileTransferState.Accepted, null, null, FileTransferState.Offered))
            {
                await AbandonReceivingAsync(transferId).ConfigureAwait(false);
                return new FileActionResult(transferId, "This offer is no longer open.");
            }

            RaiseChanged();
            await session.SendAsync(RmpMessageTypes.FileAccept, SessionPayloads.ToElement(new FileAcceptPayload(transferId)), cancellationToken).ConfigureAwait(false);
            return new FileActionResult(transferId, null);
        }
        catch (MessageStoreException)
        {
            await AbandonReceivingAsync(transferId).ConfigureAwait(false);
            return new FileActionResult(transferId, "Roomie couldn't save this answer.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException)
        {
            await AbandonReceivingAsync(transferId).ConfigureAwait(false);
            FinishIncoming(peerId, transferId, FileTransferState.Failed, "Roomie couldn't start receiving this file.");
            return new FileActionResult(transferId, "Roomie couldn't start receiving this file.");
        }
    }

    public async Task<FileActionResult> DeclineAsync(Guid peerId, Guid transferId, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!_store.TryMoveTransfer(peerId, MessageDirection.Incoming, transferId, FileTransferState.Rejected, null, null, FileTransferState.Offered))
            {
                return new FileActionResult(transferId, "This offer is no longer open.");
            }

            RaiseChanged();
        }
        catch (MessageStoreException)
        {
            return new FileActionResult(transferId, "Roomie couldn't save this answer.");
        }

        // If it can't be told now, its offer is answered the same way when it asks again.
        await TellAsync(peerId, RmpMessageTypes.FileReject, new FileRejectPayload(transferId, FileRejectReasons.Declined), cancellationToken).ConfigureAwait(false);
        return new FileActionResult(transferId, null);
    }

    /// <summary>Stops a transfer in either direction, and removes whatever had arrived.</summary>
    public async Task<FileActionResult> CancelAsync(Guid peerId, MessageDirection direction, Guid transferId, CancellationToken cancellationToken = default)
    {
        try
        {
            if (_store.GetTransfer(peerId, direction, transferId) is not { State: < FileTransferState.Completed } transfer)
            {
                return new FileActionResult(transferId, "This transfer has already ended.");
            }

            if (direction == MessageDirection.Outgoing)
            {
                StopSending(transferId);

                // The source path stays, so it can be sent again.
                _store.TryMoveTransfer(peerId, direction, transferId, FileTransferState.Canceled, transfer.LocalPath, null, OpenStates);
            }
            else
            {
                await AbandonReceivingAsync(transferId).ConfigureAwait(false);
                _store.TryMoveTransfer(peerId, direction, transferId, FileTransferState.Canceled, null, null, OpenStates);
            }

            RaiseChanged();
        }
        catch (MessageStoreException)
        {
            return new FileActionResult(transferId, "Roomie couldn't save this change.");
        }

        await TellAsync(peerId, RmpMessageTypes.FileReject, new FileRejectPayload(transferId, FileRejectReasons.Canceled), cancellationToken).ConfigureAwait(false);
        return new FileActionResult(transferId, null);
    }

    public IReadOnlyList<StoredTransfer> GetTransfers(Guid peerId, int maxTransfers) => _store.GetTransfers(peerId, maxTransfers);

    public IReadOnlyList<StoredTransfer> GetOpenTransfers() => _store.GetOpenTransfers();

    /// <summary>How many bytes have moved in a running transfer.</summary>
    public long GetProgress(Guid peerId, Guid transferId)
    {
        lock (_gate)
        {
            return _progress.GetValueOrDefault((peerId, transferId));
        }
    }

    public override void Dispose()
    {
        _registration.Dispose();
        _connections.SessionOpened -= OnSessionOpened;
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            await foreach (var peerId in _work.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await FlushAsync(peerId, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        await StopEverythingAsync().ConfigureAwait(false);
    }

    /// <summary>A transfer doesn't survive a restart. Offers and unconfirmed endings can still be finished.</summary>
    private void RecoverAfterRestart()
    {
        try
        {
            foreach (var transfer in _store.GetOpenTransfers())
            {
                var interrupted = transfer.Direction == MessageDirection.Outgoing
                    ? transfer.State is FileTransferState.Accepted or FileTransferState.Transferring
                    : transfer.State is FileTransferState.Accepted or FileTransferState.Transferring or FileTransferState.Verifying;
                if (interrupted)
                {
                    _store.UpdateTransfer(transfer.PeerId, transfer.Direction, transfer.TransferId, FileTransferState.Failed, transfer.LocalPath, "Roomie closed before this finished.");
                }
            }

            Directory.CreateDirectory(_options.IncomingDirectory);
            foreach (var leftover in Directory.GetFiles(_options.IncomingDirectory, "*.part"))
            {
                File.Delete(leftover);
            }
        }
        catch (Exception ex) when (ex is MessageStoreException or IOException or UnauthorizedAccessException)
        {
            LogRecoveryFailed(_logger, ex);
        }
    }

    private void OnSessionOpened(object? sender, PeerSession session)
    {
        _work.Writer.TryWrite(session.RemoteDeviceId);
        _ = session.Completion.ContinueWith(
            _ => OnSessionClosedAsync(session),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default).Unwrap();
    }

    /// <summary>Anything that was moving over this session can't continue on another, because there is no resuming.</summary>
    private async Task OnSessionClosedAsync(PeerSession session)
    {
        List<Guid> receiving;
        lock (_gate)
        {
            receiving = _receiving.Where(r => r.Value.SessionId == session.SessionId).Select(r => r.Key).ToList();
        }

        // Sends on this session end by themselves: their next write fails.

        foreach (var id in receiving)
        {
            await AbandonReceivingAsync(id).ConfigureAwait(false);
            FinishIncoming(session.RemoteDeviceId, id, FileTransferState.Failed, "The connection was lost. Ask for the file to be sent again.");
        }

        // Accepted, but its turn hadn't come.
        try
        {
            foreach (var waiting in _store.GetOpenTransfers().Where(t => t is { Direction: MessageDirection.Outgoing, State: FileTransferState.Accepted } && t.PeerId == session.RemoteDeviceId))
            {
                FinishOutgoing(waiting, FileTransferState.Failed, "The connection was lost. Send the file again.");
            }
        }
        catch (MessageStoreException)
        {
            // Restarting clears these. The store logged the failure.
        }
    }

    /// <summary>Sends offers and endings the device may have missed. Repeats are harmless: the receiver knows each transfer by ID.</summary>
    private async Task FlushAsync(Guid peerId, CancellationToken cancellationToken)
    {
        if (!_connections.TryGetSession(peerId, out var session) || !session.IsTrusted || !session.Supports(RmpCapabilities.FileTransfer))
        {
            return;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.WriteTimeout);

            foreach (var transfer in _store.GetOpenTransfers().Where(t => t.PeerId == peerId && t.Direction == MessageDirection.Outgoing))
            {
                if (transfer.State is FileTransferState.Waiting or FileTransferState.Offered)
                {
                    // Recorded before sending, because the answer can arrive before the send returns. An offer
                    // that doesn't get through is sent again on the next session either way.
                    if (!_store.TryMoveTransfer(peerId, MessageDirection.Outgoing, transfer.TransferId, FileTransferState.Offered, transfer.LocalPath, null, FileTransferState.Waiting, FileTransferState.Offered))
                    {
                        continue;
                    }

                    RaiseChanged();
                    var offer = new FileOfferPayload(transfer.TransferId, transfer.Filename, transfer.FileSize, transfer.Sha256, transfer.ContentType);
                    await session.SendAsync(RmpMessageTypes.FileOffer, SessionPayloads.ToElement(offer), timeout.Token).ConfigureAwait(false);
                }
                else if (transfer.State == FileTransferState.Verifying)
                {
                    await session.SendAsync(RmpMessageTypes.FileComplete, SessionPayloads.ToElement(new FileCompletePayload(transfer.TransferId)), timeout.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            LogWriteTimedOut(_logger, peerId);
            _ = session.CloseAsync(null, "Sending timed out.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or MessageStoreException)
        {
            LogFlushFailed(_logger, peerId, ex.Message);
        }
    }

    private async ValueTask HandleMessageAsync(PeerSession session, RmpEnvelope message, CancellationToken cancellationToken)
    {
        switch (message.Type)
        {
            case RmpMessageTypes.FileOffer when ContentPayloads.TryReadFileOffer(message.Payload, out var offer):
                await OnOfferAsync(session, offer, cancellationToken).ConfigureAwait(false);
                break;

            case RmpMessageTypes.FileAccept when FilePayloadReader.TryRead<FileAcceptPayload>(message.Payload, out var accept):
                await OnAcceptAsync(session, accept.TransferId, cancellationToken).ConfigureAwait(false);
                break;

            case RmpMessageTypes.FileReject when FilePayloadReader.TryRead<FileRejectPayload>(message.Payload, out var reject):
                await OnRejectAsync(session, reject).ConfigureAwait(false);
                break;

            case RmpMessageTypes.FileComplete when FilePayloadReader.TryRead<FileCompletePayload>(message.Payload, out var complete):
                await OnCompleteAsync(session, complete.TransferId, cancellationToken).ConfigureAwait(false);
                break;

            case RmpMessageTypes.FileAck when FilePayloadReader.TryRead<FileAckPayload>(message.Payload, out var ack):
                OnAck(session, ack);
                break;
        }
    }

    private async Task OnOfferAsync(PeerSession session, FileOfferPayload offer, CancellationToken cancellationToken)
    {
        var peerId = session.RemoteDeviceId;
        if (_store.GetTransfer(peerId, MessageDirection.Incoming, offer.TransferId) is { } known)
        {
            // The sender is asking again, so it didn't hear the answer.
            if (known.State == FileTransferState.Rejected)
            {
                await session.SendAsync(RmpMessageTypes.FileReject, SessionPayloads.ToElement(new FileRejectPayload(offer.TransferId, FileRejectReasons.Declined)), cancellationToken).ConfigureAwait(false);
            }
            else if (known.State is FileTransferState.Failed or FileTransferState.Canceled)
            {
                await session.SendAsync(RmpMessageTypes.FileReject, SessionPayloads.ToElement(new FileRejectPayload(offer.TransferId, FileRejectReasons.Failed)), cancellationToken).ConfigureAwait(false);
            }
            else if (known.State == FileTransferState.Completed)
            {
                await session.SendAsync(RmpMessageTypes.FileAck, SessionPayloads.ToElement(new FileAckPayload(offer.TransferId, true)), cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        var open = _store.GetOpenTransfers().Count(t => t.PeerId == peerId && t is { Direction: MessageDirection.Incoming, State: FileTransferState.Offered });
        if (open >= _options.MaxOpenOffersPerDevice)
        {
            await session.SendAsync(RmpMessageTypes.FileReject, SessionPayloads.ToElement(new FileRejectPayload(offer.TransferId, FileRejectReasons.Busy)), cancellationToken).ConfigureAwait(false);
            return;
        }

        // The offered name is only ever used through this: never as a path, and never shown unmade-safe.
        _store.AddTransfer(new StoredTransfer(offer.TransferId, peerId, MessageDirection.Incoming, SafeFilename.Create(offer.Filename), offer.FileSize, offer.Sha256, offer.ContentType, FileTransferState.Offered, _time.GetUtcNow()));
        RaiseChanged();
        IncomingOffer?.Invoke(this, EventArgs.Empty);
    }

    private async Task OnAcceptAsync(PeerSession session, Guid transferId, CancellationToken cancellationToken)
    {
        if (_store.GetTransfer(session.RemoteDeviceId, MessageDirection.Outgoing, transferId) is not { State: FileTransferState.Offered or FileTransferState.Waiting } transfer)
        {
            // Stopped here while the offer waited there. Without this, the other side would wait forever.
            await session.SendAsync(RmpMessageTypes.FileReject, SessionPayloads.ToElement(new FileRejectPayload(transferId, FileRejectReasons.Canceled)), cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_store.TryMoveTransfer(transfer.PeerId, MessageDirection.Outgoing, transferId, FileTransferState.Accepted, transfer.LocalPath, null, FileTransferState.Offered, FileTransferState.Waiting))
        {
            RaiseChanged();
            StartNextSends(session);
        }
    }

    private async Task OnRejectAsync(PeerSession session, FileRejectPayload reject)
    {
        var peerId = session.RemoteDeviceId;
        if (_store.GetTransfer(peerId, MessageDirection.Outgoing, reject.TransferId) is { State: < FileTransferState.Completed } outgoing)
        {
            StopSending(reject.TransferId);
            var (state, failure) = reject.Reason switch
            {
                FileRejectReasons.Declined => (FileTransferState.Rejected, (string?)null),
                FileRejectReasons.Canceled => (FileTransferState.Canceled, null),
                FileRejectReasons.Busy => (FileTransferState.Failed, "The other device has too many unanswered files. Try again later."),
                _ => (FileTransferState.Failed, "The other device couldn't receive this file."),
            };
            FinishOutgoing(outgoing, state, failure);
        }

        if (_store.GetTransfer(peerId, MessageDirection.Incoming, reject.TransferId) is { State: < FileTransferState.Completed })
        {
            await AbandonReceivingAsync(reject.TransferId).ConfigureAwait(false);
            FinishIncoming(peerId, reject.TransferId, reject.Reason == FileRejectReasons.Canceled ? FileTransferState.Canceled : FileTransferState.Failed,
                reject.Reason == FileRejectReasons.Canceled ? null : "The other device stopped sending this file.");
        }
    }

    private async ValueTask HandleChunkAsync(PeerSession session, FileChunk chunk, CancellationToken cancellationToken)
    {
        Receiving? receiving;
        lock (_gate)
        {
            _receiving.TryGetValue(chunk.TransferId, out receiving);
        }

        var peerId = session.RemoteDeviceId;
        if (receiving is null || receiving.PeerId != peerId)
        {
            // Chunks already on the way when a transfer ended are expected, and aren't an offense.
            if (_store.GetTransfer(peerId, MessageDirection.Incoming, chunk.TransferId) is { State: >= FileTransferState.Completed })
            {
                return;
            }

            throw new RmpProtocolException(RmpErrorCodes.TransferRejected, "That chunk doesn't belong to an accepted transfer.");
        }

        string? problem = null;
        await receiving.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (receiving.IsClosed)
            {
                return;
            }

            if (!receiving.HasStarted && CountReceiving(peerId) >= _options.MaxActivePerDevice)
            {
                problem = "The other device tried to send too many files at once.";
            }
            else if (chunk.Sequence != receiving.NextSequence)
            {
                problem = "Part of the file arrived out of order.";
            }
            else if (receiving.Received + chunk.Data.Length > receiving.Offer.FileSize)
            {
                problem = "More data arrived than the file's size allows.";
            }
            else
            {
                await receiving.Stream.WriteAsync(chunk.Data, cancellationToken).ConfigureAwait(false);
                receiving.Received += chunk.Data.Length;
                receiving.NextSequence++;
                SetProgress(peerId, chunk.TransferId, receiving.Received);
                if (!receiving.HasStarted)
                {
                    receiving.HasStarted = true;
                    _store.TryMoveTransfer(peerId, MessageDirection.Incoming, chunk.TransferId, FileTransferState.Transferring, null, null, FileTransferState.Accepted);
                    RaiseChanged();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = "Roomie couldn't save this file. The disk may be full.";
        }
        finally
        {
            receiving.Lock.Release();
        }

        if (problem is not null)
        {
            await AbandonReceivingAsync(chunk.TransferId).ConfigureAwait(false);
            FinishIncoming(peerId, chunk.TransferId, FileTransferState.Failed, problem);
            await session.SendAsync(RmpMessageTypes.FileReject, SessionPayloads.ToElement(new FileRejectPayload(chunk.TransferId, FileRejectReasons.Failed)), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task OnCompleteAsync(PeerSession session, Guid transferId, CancellationToken cancellationToken)
    {
        var peerId = session.RemoteDeviceId;
        var known = _store.GetTransfer(peerId, MessageDirection.Incoming, transferId)
            ?? throw new RmpProtocolException(RmpErrorCodes.TransferRejected, "That transfer isn't known here.");

        if (known.State >= FileTransferState.Completed)
        {
            // The sender restarted or lost the connection, and is asking how it ended.
            await session.SendAsync(RmpMessageTypes.FileAck, SessionPayloads.ToElement(new FileAckPayload(transferId, known.State == FileTransferState.Completed)), cancellationToken).ConfigureAwait(false);
            return;
        }

        Receiving? receiving;
        lock (_gate)
        {
            _receiving.TryGetValue(transferId, out receiving);
        }

        if (receiving is null || receiving.PeerId != peerId || known.State == FileTransferState.Verifying)
        {
            return;
        }

        if (receiving.Received != known.FileSize)
        {
            await AbandonReceivingAsync(transferId).ConfigureAwait(false);
            FinishIncoming(peerId, transferId, FileTransferState.Failed, "The file ended before all of it arrived.");
            await session.SendAsync(RmpMessageTypes.FileAck, SessionPayloads.ToElement(new FileAckPayload(transferId, false)), cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!_store.TryMoveTransfer(peerId, MessageDirection.Incoming, transferId, FileTransferState.Verifying, null, null, FileTransferState.Accepted, FileTransferState.Transferring))
        {
            return;
        }

        RaiseChanged();

        // Hashing up to 100 MB must not hold up this session's read loop.
        _ = Task.Run(() => VerifyAsync(session, receiving, cancellationToken), CancellationToken.None);
    }

    private async Task VerifyAsync(PeerSession session, Receiving receiving, CancellationToken cancellationToken)
    {
        var peerId = receiving.PeerId;
        var transferId = receiving.Offer.TransferId;
        var verified = false;
        try
        {
            await receiving.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (receiving.IsClosed)
                {
                    return;
                }

                await receiving.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                receiving.Stream.Flush(flushToDisk: true);
                receiving.Stream.Position = 0;

                // The bytes that reached the disk, read back: not a running total of what was passed to it.
                var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(receiving.Stream, cancellationToken).ConfigureAwait(false));
                verified = hash == receiving.Offer.Sha256;
                receiving.IsClosed = true;
                await receiving.Stream.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                receiving.Lock.Release();
            }

            lock (_gate)
            {
                _receiving.Remove(transferId);
                _progress.Remove((peerId, transferId));
            }

            if (verified)
            {
                var saved = MoveIntoDownloads(receiving);
                FinishIncoming(peerId, transferId, FileTransferState.Completed, null, saved);
            }
            else
            {
                DeleteQuietly(receiving.TemporaryPath);
                FinishIncoming(peerId, transferId, FileTransferState.Failed, "The file arrived damaged: its contents don't match what was offered. It was removed.");
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            verified = false;
            LogSaveFailed(_logger, transferId, ex.Message);
            DeleteQuietly(receiving.TemporaryPath);
            FinishIncoming(peerId, transferId, FileTransferState.Failed, "Roomie couldn't save this file. The disk may be full.");
        }

        try
        {
            await session.SendAsync(RmpMessageTypes.FileAck, SessionPayloads.ToElement(new FileAckPayload(transferId, verified)), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            // The sender asks how it ended on its next session.
            LogFlushFailed(_logger, peerId, ex.Message);
        }
    }

    private void OnAck(PeerSession session, FileAckPayload ack)
    {
        if (_store.GetTransfer(session.RemoteDeviceId, MessageDirection.Outgoing, ack.TransferId) is { State: FileTransferState.Verifying } transfer)
        {
            FinishOutgoing(transfer, ack.Verified ? FileTransferState.Completed : FileTransferState.Failed,
                ack.Verified ? null : "The other device didn't receive this file intact. Send it again.");
        }
    }

    private void StartNextSends(PeerSession session)
    {
        var peerId = session.RemoteDeviceId;
        List<StoredTransfer> next;
        lock (_gate)
        {
            var active = _sending.Values.Count(s => s.PeerId == peerId);
            var slots = _options.MaxActivePerDevice - active;
            if (slots <= 0)
            {
                return;
            }

            next = _store.GetOpenTransfers()
                .Where(t => t is { Direction: MessageDirection.Outgoing, State: FileTransferState.Accepted } && t.PeerId == peerId && !_sending.ContainsKey(t.TransferId))
                .OrderBy(t => t.CreatedAt)
                .Take(slots)
                .ToList();
            foreach (var transfer in next)
            {
                _sending[transfer.TransferId] = new Sending(peerId, session.SessionId, new CancellationTokenSource());
            }
        }

        foreach (var transfer in next)
        {
            _ = Task.Run(() => SendAsync(session, transfer), CancellationToken.None);
        }
    }

    private async Task SendAsync(PeerSession session, StoredTransfer transfer)
    {
        var transferId = transfer.TransferId;
        Sending sending;
        lock (_gate)
        {
            sending = _sending[transferId];
        }

        var cancellation = sending.Cancellation.Token;
        string? failure = null;
        try
        {
            if (!_store.TryMoveTransfer(transfer.PeerId, MessageDirection.Outgoing, transferId, FileTransferState.Transferring, transfer.LocalPath, null, FileTransferState.Accepted))
            {
                throw new OperationCanceledException();
            }

            RaiseChanged();

            var stream = new FileStream(transfer.LocalPath!, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (stream.ConfigureAwait(false))
            {
                var buffer = new byte[FileChunk.MaxDataLength];
                uint sequence = 0;
                long sent = 0;
                while (sent < transfer.FileSize)
                {
                    var wanted = (int)Math.Min(buffer.Length, transfer.FileSize - sent);
                    var read = await stream.ReadAtLeastAsync(buffer.AsMemory(0, wanted), wanted, throwOnEndOfStream: false, cancellation).ConfigureAwait(false);
                    if (read < wanted)
                    {
                        failure = "The file changed while it was being sent. Send it again.";
                        break;
                    }

                    // Stopping happens only here, between chunks. Interrupting a write could leave half a frame on
                    // the wire, which would end the whole session, with its messages and other transfers.
                    cancellation.ThrowIfCancellationRequested();
                    var chunkSequence = sequence++;
                    var chunkData = buffer.AsMemory(0, read);
                    await WriteOrCloseAsync(session, token => session.SendChunkAsync(transferId, chunkSequence, chunkData, token)).ConfigureAwait(false);

                    sent += read;
                    SetProgress(transfer.PeerId, transferId, sent);
                }
            }

            if (failure is null)
            {
                // Saved first: if the connection drops now, the next session asks how it ended.
                if (!_store.TryMoveTransfer(transfer.PeerId, MessageDirection.Outgoing, transferId, FileTransferState.Verifying, transfer.LocalPath, null, FileTransferState.Transferring))
                {
                    throw new OperationCanceledException();
                }

                RaiseChanged();
                var complete = SessionPayloads.ToElement(new FileCompletePayload(transferId));
                await WriteOrCloseAsync(session, token => session.SendAsync(RmpMessageTypes.FileComplete, complete, token)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // A cancel or refusal already recorded the outcome. Only shutting down hasn't, and the next start does.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException)
        {
            // Once all the data has gone, the next session can still find out how it ended.
            if (_store.GetTransfer(transfer.PeerId, MessageDirection.Outgoing, transferId) is { State: FileTransferState.Transferring })
            {
                failure = "Sending stopped: the file couldn't be read, or the connection was lost. Send it again.";
            }
        }
        catch (MessageStoreException ex)
        {
            LogFlushFailed(_logger, transfer.PeerId, ex.Message);
        }
        finally
        {
            lock (_gate)
            {
                _sending.Remove(transferId);
                _progress.Remove((transfer.PeerId, transferId));
            }

            sending.Cancellation.Dispose();
        }

        if (failure is not null)
        {
            FinishOutgoing(transfer, FileTransferState.Failed, failure);
            await TellAsync(transfer.PeerId, RmpMessageTypes.FileReject, new FileRejectPayload(transferId, FileRejectReasons.Failed), CancellationToken.None).ConfigureAwait(false);
        }

        if (!session.IsClosed)
        {
            StartNextSends(session);
        }

        RaiseChanged();
    }

    /// <summary>A write that doesn't finish in time may have left part of a frame, so the session can't be used again.</summary>
    private async Task WriteOrCloseAsync(PeerSession session, Func<CancellationToken, Task> write)
    {
        using var timeout = new CancellationTokenSource(_options.WriteTimeout);
        try
        {
            await write(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            LogWriteTimedOut(_logger, session.RemoteDeviceId);
            _ = session.CloseAsync(null, "Sending timed out.");
            throw new IOException("Sending timed out.");
        }
    }

    private void StopSending(Guid transferId)
    {
        lock (_gate)
        {
            if (_sending.TryGetValue(transferId, out var sending))
            {
                sending.Cancellation.Cancel();
            }
        }
    }

    private async Task AbandonReceivingAsync(Guid transferId)
    {
        Receiving? receiving;
        lock (_gate)
        {
            _receiving.Remove(transferId, out receiving);
        }

        if (receiving is null)
        {
            return;
        }

        lock (_gate)
        {
            _progress.Remove((receiving.PeerId, transferId));
        }

        await receiving.Lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!receiving.IsClosed)
            {
                receiving.IsClosed = true;
                await receiving.Stream.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            receiving.Lock.Release();
        }

        DeleteQuietly(receiving.TemporaryPath);
    }

    private async Task StopEverythingAsync()
    {
        List<Guid> receiving;
        lock (_gate)
        {
            receiving = [.. _receiving.Keys];
            foreach (var sending in _sending.Values)
            {
                sending.Cancellation.Cancel();
            }
        }

        foreach (var id in receiving)
        {
            await AbandonReceivingAsync(id).ConfigureAwait(false);
        }
    }

    private void FinishIncoming(Guid peerId, Guid transferId, FileTransferState state, string? failure, string? savedPath = null)
    {
        try
        {
            _store.TryMoveTransfer(peerId, MessageDirection.Incoming, transferId, state, savedPath, failure, OpenStates);
        }
        catch (MessageStoreException)
        {
            // Restarting clears this. The store logged the failure.
        }

        RaiseChanged();
    }

    private void FinishOutgoing(StoredTransfer transfer, FileTransferState state, string? failure)
    {
        try
        {
            _store.TryMoveTransfer(transfer.PeerId, MessageDirection.Outgoing, transfer.TransferId, state, transfer.LocalPath, failure, OpenStates);
        }
        catch (MessageStoreException)
        {
            // Restarting clears this. The store logged the failure.
        }

        RaiseChanged();
    }

    private async Task TellAsync<T>(Guid peerId, string type, T payload, CancellationToken cancellationToken)
    {
        if (!_connections.TryGetSession(peerId, out var session) || !session.IsTrusted || !session.Supports(RmpCapabilities.FileTransfer))
        {
            return;
        }

        try
        {
            await session.SendAsync(type, SessionPayloads.ToElement(payload), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            LogFlushFailed(_logger, peerId, ex.Message);
        }
    }

    /// <summary>Moves a verified file into place under a name nothing else has, and marks it as coming from outside this computer.</summary>
    private string MoveIntoDownloads(Receiving receiving)
    {
        Directory.CreateDirectory(_options.DownloadsDirectory);
        var name = receiving.Offer.Filename;
        for (var counter = 0; ; counter++)
        {
            var candidate = Path.Combine(_options.DownloadsDirectory, counter == 0 ? name : SafeFilename.WithCounter(name, counter));
            try
            {
                File.Move(receiving.TemporaryPath, candidate, overwrite: false);
                MarkAsFromElsewhere(candidate);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate) && counter < 1000)
            {
            }
        }
    }

    /// <summary>The same mark a browser leaves, so Windows warns before running a file from another device.</summary>
    private void MarkAsFromElsewhere(string path)
    {
        try
        {
            File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // FAT and exFAT disks can't hold this mark. The file is still fine.
            LogMarkFailed(_logger, path, ex.Message);
        }
    }

    private long? FreeSpace()
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(_options.IncomingDirectory));
            return root is null ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private int CountReceiving(Guid peerId)
    {
        lock (_gate)
        {
            return _receiving.Values.Count(r => r.PeerId == peerId && r.HasStarted);
        }
    }

    private void SetProgress(Guid peerId, Guid transferId, long bytes)
    {
        lock (_gate)
        {
            _progress[(peerId, transferId)] = bytes;
        }

        var now = _time.GetUtcNow().UtcTicks;
        var last = Interlocked.Read(ref _lastProgressTicks);
        if (now - last >= _options.ProgressInterval.Ticks && Interlocked.CompareExchange(ref _lastProgressTicks, now, last) == last)
        {
            ProgressChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private string TemporaryPath(Guid transferId)
    {
        Directory.CreateDirectory(_options.IncomingDirectory);
        return Path.Combine(_options.IncomingDirectory, $"{transferId:N}.part");
    }

    private void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Removed at the next startup.
            LogMarkFailed(_logger, path, ex.Message);
        }
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private sealed class Receiving(Guid peerId, Guid sessionId, StoredTransfer offer, FileStream stream, string temporaryPath)
    {
        public Guid PeerId { get; } = peerId;

        public Guid SessionId { get; } = sessionId;

        public StoredTransfer Offer { get; } = offer;

        public FileStream Stream { get; } = stream;

        public string TemporaryPath { get; } = temporaryPath;

        public SemaphoreSlim Lock { get; } = new(1, 1);

        public long Received { get; set; }

        public uint NextSequence { get; set; }

        public bool HasStarted { get; set; }

        public bool IsClosed { get; set; }
    }

    private sealed record Sending(Guid PeerId, Guid SessionId, CancellationTokenSource Cancellation);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't finish cleaning up after a restart")]
    private static partial void LogRecoveryFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sending a file to {DeviceId} timed out; closing the session")]
    private static partial void LogWriteTimedOut(ILogger logger, Guid deviceId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Couldn't send file messages to {DeviceId}: {Reason}")]
    private static partial void LogFlushFailed(ILogger logger, Guid deviceId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't save received file {TransferId}: {Reason}")]
    private static partial void LogSaveFailed(ILogger logger, Guid transferId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Couldn't change {Path}: {Reason}")]
    private static partial void LogMarkFailed(ILogger logger, string path, string reason);
}
