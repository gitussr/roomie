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
    /// <summary>Where verified files end up unless the user chose a folder, and always for a transfer accepted before they did.</summary>
    public required string DownloadsDirectory { get; init; }

    /// <summary>The folder the user chose for received files. Null means <see cref="DownloadsDirectory"/>.</summary>
    public string? ReceiveFolder { get; init; }

    /// <summary>Where files stay until they are verified. What a transfer still open needs survives a restart.</summary>
    public required string IncomingDirectory { get; init; }

    /// <summary>The largest file this device will send.</summary>
    public long MaxFileSizeBytes { get; init; } = RmpContentLimits.MaxFileSizeBytes;

    /// <summary>
    /// How much may arrive between forcing the file to disk and recording how far it got. A power cut loses at
    /// most this much, and those bytes are simply sent again.
    /// </summary>
    public long CheckpointBytes { get; init; } = 64L * 1024 * 1024;

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
/// file reaches the downloads folder only after its hash matches. A lost connection or a restart only pauses a
/// transfer: when the devices next connect, the receiver says how much it has, and the sender continues from there.
/// </summary>
public sealed partial class FileTransferService : BackgroundService
{
    private const string OutgoingContentType = "application/octet-stream";

    private static readonly FileTransferState[] OpenStates =
    [
        FileTransferState.Waiting, FileTransferState.Offered, FileTransferState.Accepted, FileTransferState.Transferring, FileTransferState.Verifying,
    ];

    /// <summary>Hidden folder for unfinished files, inside a folder the user chose.</summary>
    private const string PartialFolderName = ".roomie-partial";

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

    // Answers that allow sending, each valid only on the session it arrived on. A transfer with no entry here, or
    // one from an old session, must wait to hear how much the receiver has.
    private readonly Dictionary<Guid, (Guid SessionId, long Offset)> _ready = [];
    private readonly CancellationTokenSource _stopping = new();
    private readonly Dictionary<(Guid Peer, Guid Transfer), long> _progress = [];
    private long _lastProgressTicks;
    private volatile string _receiveFolder;

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
        _receiveFolder = Path.GetFullPath(options.ReceiveFolder ?? options.DownloadsDirectory);

        _registration = connections.Dispatcher.Register(
            [RmpMessageTypes.FileOffer, RmpMessageTypes.FileAccept, RmpMessageTypes.FileResume, RmpMessageTypes.FileReject, RmpMessageTypes.FileComplete, RmpMessageTypes.FileAck],
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

    /// <summary>Where newly accepted files will be saved.</summary>
    public string ReceiveFolder => _receiveFolder;

    /// <summary>
    /// Changes where newly accepted files are saved. A transfer already accepted finishes where it started,
    /// because its unfinished file is on that drive.
    /// </summary>
    /// <returns>Why the folder can't be used, or null if it is now in use.</returns>
    public string? TrySetReceiveFolder(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        try
        {
            var full = Path.GetFullPath(folder);
            Directory.CreateDirectory(full);

            // The only reliable way to know a folder can be written to is to write to it.
            var probe = Path.Combine(full, $".roomie-{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            _receiveFolder = full;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            LogSaveFailed(_logger, Guid.Empty, ex.Message);
            return "Roomie can't save files in that folder. Choose another one.";
        }
    }

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
        long sourceWriteTicks;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return new FileActionResult(null, "That file doesn't exist any more.");
            }

            size = info.Length;
            if (size > _options.MaxFileSizeBytes)
            {
                return new FileActionResult(null, $"This file is too large. The limit is {_options.MaxFileSizeBytes / (1024 * 1024 * 1024)} GB.");
            }

            // Taken before hashing, so a change made while hashing is noticed when sending starts.
            sourceWriteTicks = info.LastWriteTimeUtc.Ticks;

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
        var transfer = new StoredTransfer(Guid.CreateVersion7(_time.GetUtcNow()), peerId, MessageDirection.Outgoing, filename, size, sha256, OutgoingContentType, FileTransferState.Waiting, _time.GetUtcNow(), path, SourceWriteTicks: sourceWriteTicks);
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

            // Checked before anything can change: a folder on a disk that was unplugged shouldn't lose the offer.
            if (!CanUseReceiveFolder())
            {
                return new FileActionResult(transferId, "The folder for received files isn't available. Plug its drive back in, or choose another in Settings.");
            }

            if (FreeSpace() is { } free && free < offer.FileSize + _options.MinFreeBytesAfterAccept)
            {
                return new FileActionResult(transferId, "There isn't enough free disk space for this file.");
            }

            var temporary = PathForNewFile(peerId, transferId);
            var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous);
            lock (_gate)
            {
                _receiving[transferId] = new Receiving(peerId, session.SessionId, offer, stream, temporary);
            }

            if (!_store.TryMoveTransfer(peerId, MessageDirection.Incoming, transferId, FileTransferState.Accepted, null, null, FileTransferState.Offered))
            {
                await AbandonReceivingAsync(peerId, transferId).ConfigureAwait(false);
                return new FileActionResult(transferId, "This offer is no longer open.");
            }

            RaiseChanged();
            try
            {
                await session.SendAsync(RmpMessageTypes.FileAccept, SessionPayloads.ToElement(new FileAcceptPayload(transferId)), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                // The answer is saved, so losing the connection now only pauses the transfer. A session may have
                // replaced this one before this entry existed, so nothing else would pick it up.
                LogFlushFailed(_logger, peerId, ex.Message);
                _work.Writer.TryWrite(peerId);
            }

            return new FileActionResult(transferId, null);
        }
        catch (MessageStoreException)
        {
            await AbandonReceivingAsync(peerId, transferId).ConfigureAwait(false);
            return new FileActionResult(transferId, "Roomie couldn't save this answer.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException)
        {
            LogSaveFailed(_logger, transferId, ex.Message);
            await FailReceivingAsync(peerId, transferId, FileTransferState.Failed, "Roomie couldn't start receiving this file.").ConfigureAwait(false);
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
                // Recorded first, so nothing can start the transfer up again while its file is being removed.
                _store.TryMoveTransfer(peerId, direction, transferId, FileTransferState.Canceled, null, null, OpenStates);
                await AbandonReceivingAsync(peerId, transferId).ConfigureAwait(false);
            }

            ForgetProgress(peerId, transferId);

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

    public IReadOnlyList<StoredTransfer> GetRecentTransfers(int maxTransfers) => _store.GetRecentTransfers(maxTransfers);

    public StoredTransfer? GetTransfer(Guid peerId, MessageDirection direction, Guid transferId) => _store.GetTransfer(peerId, direction, transferId);

    public IReadOnlyList<StoredTransfer> GetOpenTransfers() => _store.GetOpenTransfers();

    /// <summary>How many bytes have moved in a running transfer.</summary>
    public long GetProgress(Guid peerId, Guid transferId)
    {
        lock (_gate)
        {
            return _progress.GetValueOrDefault((peerId, transferId));
        }
    }

    /// <summary>Whether a transfer is waiting to continue, because the devices aren't connected or haven't caught up yet.</summary>
    public bool IsPaused(StoredTransfer transfer)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        if (transfer.State is not (FileTransferState.Accepted or FileTransferState.Transferring))
        {
            return false;
        }

        lock (_gate)
        {
            return transfer.Direction == MessageDirection.Outgoing
                ? !_sending.ContainsKey(transfer.TransferId) && !_ready.ContainsKey(transfer.TransferId)
                : !_receiving.ContainsKey(transfer.TransferId);
        }
    }

    public override void Dispose()
    {
        _registration.Dispose();
        _connections.SessionOpened -= OnSessionOpened;
        _stopping.Dispose();
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

    /// <summary>
    /// Open transfers carry on after a restart. Anything in the incoming folder that none of them needs is a
    /// leftover. A file being verified is checked again from the start, so it goes back a stage.
    /// </summary>
    private void RecoverAfterRestart()
    {
        try
        {
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var transfer in _store.GetOpenTransfers().Where(t => t is { Direction: MessageDirection.Incoming, State: FileTransferState.Accepted or FileTransferState.Transferring or FileTransferState.Verifying }))
            {
                keep.Add(Path.GetFullPath(PartialPathOf(transfer)));
                _store.TryMoveTransfer(transfer.PeerId, MessageDirection.Incoming, transfer.TransferId, FileTransferState.Transferring, null, null, FileTransferState.Verifying);
                _progress[(transfer.PeerId, transfer.TransferId)] = transfer.ReceivedBytes;
            }

            Directory.CreateDirectory(_options.IncomingDirectory);
            var partials = Path.Combine(_receiveFolder, PartialFolderName);
            var folders = Directory.Exists(partials) ? new[] { _options.IncomingDirectory, partials } : [_options.IncomingDirectory];
            foreach (var leftover in folders.SelectMany(f => Directory.GetFiles(f, "*.part")).Where(f => !keep.Contains(Path.GetFullPath(f))))
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

    /// <summary>What was moving over this session pauses, keeping what arrived. Sends on it end by themselves: their next write fails.</summary>
    private async Task OnSessionClosedAsync(PeerSession session)
    {
        List<Receiving> receiving;
        lock (_gate)
        {
            receiving = _receiving.Values.Where(r => r.SessionId == session.SessionId && !r.IsVerifying).ToList();
            foreach (var id in _ready.Where(r => r.Value.SessionId == session.SessionId).Select(r => r.Key).ToList())
            {
                _ready.Remove(id);
            }
        }

        foreach (var item in receiving)
        {
            await PauseReceivingAsync(item).ConfigureAwait(false);
        }

        RaiseChanged();

        // A new session may have opened while this one was closing, and couldn't continue these until now.
        _work.Writer.TryWrite(session.RemoteDeviceId);
    }

    /// <summary>
    /// Sends offers and endings the device may have missed, and asks for the rest of anything paused. Repeats are
    /// harmless: the receiver knows each transfer by ID.
    /// </summary>
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

            foreach (var transfer in _store.GetOpenTransfers().Where(t => t.PeerId == peerId))
            {
                if (transfer.Direction == MessageDirection.Incoming)
                {
                    if (transfer.State is FileTransferState.Accepted or FileTransferState.Transferring)
                    {
                        await ResumeReceivingAsync(session, transfer, timeout.Token).ConfigureAwait(false);
                    }
                }
                else if (transfer.State is FileTransferState.Waiting or FileTransferState.Offered)
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
                else if (IsPaused(transfer))
                {
                    // Paused here. If it ended there while the devices were apart, the answer to this says so.
                    var offer = new FileOfferPayload(transfer.TransferId, transfer.Filename, transfer.FileSize, transfer.Sha256, transfer.ContentType);
                    await session.SendAsync(RmpMessageTypes.FileOffer, SessionPayloads.ToElement(offer), timeout.Token).ConfigureAwait(false);
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

            case RmpMessageTypes.FileResume when FilePayloadReader.TryRead<FileResumePayload>(message.Payload, out var resume):
                await OnResumeAsync(session, resume, cancellationToken).ConfigureAwait(false);
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
            else if (known.State == FileTransferState.Canceled)
            {
                await session.SendAsync(RmpMessageTypes.FileReject, SessionPayloads.ToElement(new FileRejectPayload(offer.TransferId, FileRejectReasons.Canceled)), cancellationToken).ConfigureAwait(false);
            }
            else if (known.State == FileTransferState.Failed)
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
        if (_store.GetTransfer(session.RemoteDeviceId, MessageDirection.Outgoing, transferId) is not { State: FileTransferState.Offered or FileTransferState.Waiting or FileTransferState.Accepted or FileTransferState.Transferring } transfer)
        {
            // Stopped here while the offer waited there. Without this, the other side would wait forever.
            await session.SendAsync(RmpMessageTypes.FileReject, SessionPayloads.ToElement(new FileRejectPayload(transferId, FileRejectReasons.Canceled)), cancellationToken).ConfigureAwait(false);
            return;
        }

        BeginSending(session, transfer, 0);
    }

    /// <summary>The receiver's word for how much it has. Sending from there only repeats what it already agreed to receive.</summary>
    private async Task OnResumeAsync(PeerSession session, FileResumePayload resume, CancellationToken cancellationToken)
    {
        var known = _store.GetTransfer(session.RemoteDeviceId, MessageDirection.Outgoing, resume.TransferId);
        if (known is not { State: < FileTransferState.Completed } transfer)
        {
            var reason = known?.State switch
            {
                FileTransferState.Canceled => FileRejectReasons.Canceled,
                FileTransferState.Rejected => FileRejectReasons.Declined,
                _ => FileRejectReasons.Failed,
            };
            await session.SendAsync(RmpMessageTypes.FileReject, SessionPayloads.ToElement(new FileRejectPayload(resume.TransferId, reason)), cancellationToken).ConfigureAwait(false);
            return;
        }

        if (resume.Offset > transfer.FileSize)
        {
            FinishOutgoing(transfer, FileTransferState.Failed, "The other device reported more of this file than exists.");
            await session.SendAsync(RmpMessageTypes.FileReject, SessionPayloads.ToElement(new FileRejectPayload(resume.TransferId, FileRejectReasons.Failed)), cancellationToken).ConfigureAwait(false);
            return;
        }

        BeginSending(session, transfer, resume.Offset);
    }

    private void BeginSending(PeerSession session, StoredTransfer transfer, long offset)
    {
        lock (_gate)
        {
            _ready[transfer.TransferId] = (session.SessionId, offset);

            // A send still running on a dead session gives way, and the new one starts when it has finished.
            if (_sending.TryGetValue(transfer.TransferId, out var running))
            {
                running.Cancellation.Cancel();
            }
        }

        if (_store.TryMoveTransfer(transfer.PeerId, MessageDirection.Outgoing, transfer.TransferId, FileTransferState.Accepted, transfer.LocalPath, null, FileTransferState.Offered, FileTransferState.Waiting, FileTransferState.Accepted, FileTransferState.Transferring, FileTransferState.Verifying))
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
            await FailReceivingAsync(peerId, reject.TransferId, reject.Reason == FileRejectReasons.Canceled ? FileTransferState.Canceled : FileTransferState.Failed,
                reject.Reason == FileRejectReasons.Canceled ? null : "The other device stopped sending this file.").ConfigureAwait(false);
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
        if (receiving is null || receiving.PeerId != peerId || receiving.SessionId != session.SessionId)
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
                receiving.ResumePending = false;
                SetProgress(peerId, chunk.TransferId, receiving.Received);
                if (receiving.Received - receiving.Checkpointed >= _options.CheckpointBytes)
                {
                    await CheckpointAsync(receiving).ConfigureAwait(false);
                }

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
            await FailReceivingAsync(peerId, chunk.TransferId, FileTransferState.Failed, problem).ConfigureAwait(false);
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

        if (receiving is null || receiving.PeerId != peerId || receiving.SessionId != session.SessionId || known.State == FileTransferState.Verifying)
        {
            return;
        }

        if (receiving.Received != known.FileSize)
        {
            // The sender finished on an earlier session and is asking how it ended. FILE_RESUME is its answer.
            if (receiving.ResumePending)
            {
                return;
            }

            await FailReceivingAsync(peerId, transferId, FileTransferState.Failed, "The file ended before all of it arrived.").ConfigureAwait(false);
            await session.SendAsync(RmpMessageTypes.FileAck, SessionPayloads.ToElement(new FileAckPayload(transferId, false)), cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!_store.TryMoveTransfer(peerId, MessageDirection.Incoming, transferId, FileTransferState.Verifying, null, null, FileTransferState.Accepted, FileTransferState.Transferring))
        {
            return;
        }

        receiving.IsVerifying = true;
        RaiseChanged();

        // Hashing a large file must not hold up this session's read loop, and doesn't need the session: losing
        // the connection now only delays telling the sender how it went.
        _ = Task.Run(() => VerifyAsync(receiving), CancellationToken.None);
    }

    private async Task VerifyAsync(Receiving receiving)
    {
        var cancellationToken = _stopping.Token;
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

                // Recorded first: if Roomie closes during the check, the check starts again and nothing is sent twice.
                await CheckpointAsync(receiving).ConfigureAwait(false);
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
                RemovePartialFolderIfEmpty(receiving.TemporaryPath);
                FinishIncoming(peerId, transferId, FileTransferState.Completed, null, saved);
            }
            else
            {
                DeleteQuietly(receiving.TemporaryPath);
                RemovePartialFolderIfEmpty(receiving.TemporaryPath);
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
            await FailReceivingAsync(peerId, transferId, FileTransferState.Failed, "Roomie couldn't save this file. The disk may be full.").ConfigureAwait(false);
        }

        // Goes to whichever session is open by now. If none is, the sender asks how it ended on its next one.
        await TellAsync(peerId, RmpMessageTypes.FileAck, new FileAckPayload(transferId, verified), CancellationToken.None).ConfigureAwait(false);
    }

    private void OnAck(PeerSession session, FileAckPayload ack)
    {
        if (_store.GetTransfer(session.RemoteDeviceId, MessageDirection.Outgoing, ack.TransferId) is { State: FileTransferState.Accepted or FileTransferState.Transferring or FileTransferState.Verifying } transfer)
        {
            StopSending(ack.TransferId);
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
                .Where(t => t is { Direction: MessageDirection.Outgoing, State: FileTransferState.Accepted } && t.PeerId == peerId && !_sending.ContainsKey(t.TransferId)
                    && _ready.TryGetValue(t.TransferId, out var ready) && ready.SessionId == session.SessionId)
                .OrderBy(t => t.CreatedAt)
                .Take(slots)
                .ToList();
            foreach (var transfer in next)
            {
                _ready.Remove(transfer.TransferId, out var answer);
                _sending[transfer.TransferId] = new Sending(peerId, session.SessionId, new CancellationTokenSource(), answer.Offset);
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
                // A changed file would fail its hash at the very end, after everything had been sent for nothing.
                if (stream.Length != transfer.FileSize || (transfer.SourceWriteTicks != 0 && File.GetLastWriteTimeUtc(transfer.LocalPath!).Ticks != transfer.SourceWriteTicks))
                {
                    failure = "The file changed after it was offered. Send it again.";
                }

                var buffer = new byte[FileChunk.MaxDataLength];
                uint sequence = 0;
                var sent = sending.Offset;
                stream.Position = Math.Min(sent, stream.Length);
                SetProgress(transfer.PeerId, transferId, sent);
                while (failure is null && sent < transfer.FileSize)
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
            // A cancel or refusal already recorded the outcome. Otherwise this is paused or has been replaced.
        }
        catch (ConnectionLostException ex)
        {
            // Paused. The receiver says where to continue from when the devices next connect.
            LogFlushFailed(_logger, transfer.PeerId, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogFlushFailed(_logger, transfer.PeerId, ex.Message);
            failure = "Sending stopped: the file couldn't be read. Send it again.";
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
            }

            sending.Cancellation.Dispose();
        }

        if (failure is not null)
        {
            FinishOutgoing(transfer, FileTransferState.Failed, failure);
            await TellAsync(transfer.PeerId, RmpMessageTypes.FileReject, new FileRejectPayload(transferId, FileRejectReasons.Failed), CancellationToken.None).ConfigureAwait(false);
        }

        // Not always the session this ran on: one that replaced it may have been waiting for this to end.
        if (_connections.TryGetSession(transfer.PeerId, out var current))
        {
            StartNextSends(current);
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
            throw new ConnectionLostException("Sending timed out.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            throw new ConnectionLostException(ex.Message);
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

    /// <summary>Connects a paused incoming transfer to this session, and tells the sender where to continue from.</summary>
    private async Task ResumeReceivingAsync(PeerSession session, StoredTransfer transfer, CancellationToken cancellationToken)
    {
        var transferId = transfer.TransferId;
        Receiving? existing;
        lock (_gate)
        {
            _receiving.TryGetValue(transferId, out existing);
        }

        if (existing is not null)
        {
            if (existing.SessionId == session.SessionId || existing.IsVerifying)
            {
                return;
            }

            await PauseReceivingAsync(existing).ConfigureAwait(false);
        }

        // Anything past the last recorded point may not have reached the disk, so it is cut off and sent again.
        var saved = _store.GetTransfer(transfer.PeerId, MessageDirection.Incoming, transferId)?.ReceivedBytes ?? 0;
        var path = PartialPathOf(transfer);
        Receiving receiving;
        try
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous);
            var offset = Math.Min(saved, stream.Length);
            stream.SetLength(offset);
            stream.Position = offset;
            receiving = new Receiving(transfer.PeerId, session.SessionId, transfer, stream, path) { Received = offset, Checkpointed = offset, ResumePending = true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogSaveFailed(_logger, transferId, ex.Message);
            await FailReceivingAsync(transfer.PeerId, transferId, FileTransferState.Failed, "Roomie couldn't continue saving this file.").ConfigureAwait(false);
            await session.SendAsync(RmpMessageTypes.FileReject, SessionPayloads.ToElement(new FileRejectPayload(transferId, FileRejectReasons.Failed)), cancellationToken).ConfigureAwait(false);
            return;
        }

        lock (_gate)
        {
            _receiving[transferId] = receiving;
        }

        // Checked after registering: whatever ends a transfer records that first and then removes what's registered.
        if (_store.GetTransfer(transfer.PeerId, MessageDirection.Incoming, transferId) is not { State: FileTransferState.Accepted or FileTransferState.Transferring })
        {
            await AbandonReceivingAsync(transfer.PeerId, transferId).ConfigureAwait(false);
            return;
        }

        SetProgress(transfer.PeerId, transferId, receiving.Received);
        RaiseChanged();
        await session.SendAsync(RmpMessageTypes.FileResume, SessionPayloads.ToElement(new FileResumePayload(transferId, receiving.Received)), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Forces what arrived to disk, then records how far it got. Call while holding the transfer's lock.</summary>
    private async Task CheckpointAsync(Receiving receiving)
    {
        await receiving.Stream.FlushAsync().ConfigureAwait(false);
        await Task.Run(() => receiving.Stream.Flush(flushToDisk: true)).ConfigureAwait(false);
        try
        {
            _store.SaveReceivedBytes(receiving.PeerId, receiving.Offer.TransferId, receiving.Received);
            receiving.Checkpointed = receiving.Received;
        }
        catch (MessageStoreException)
        {
            // Continuing from the previous record only sends a little again. The store logged the failure.
        }
    }

    /// <summary>Closes the temporary file and keeps it, with a record of how far it got.</summary>
    private async Task PauseReceivingAsync(Receiving receiving)
    {
        await receiving.Lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!receiving.IsClosed)
            {
                receiving.IsClosed = true;
                try
                {
                    await CheckpointAsync(receiving).ConfigureAwait(false);
                    await receiving.Stream.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
                {
                    // The last record still stands, and what came after it is sent again.
                    LogSaveFailed(_logger, receiving.Offer.TransferId, ex.Message);
                }
            }
        }
        finally
        {
            receiving.Lock.Release();
        }

        lock (_gate)
        {
            if (_receiving.TryGetValue(receiving.Offer.TransferId, out var current) && ReferenceEquals(current, receiving))
            {
                _receiving.Remove(receiving.Offer.TransferId);
            }
        }
    }

    /// <summary>Ends a transfer and removes what arrived, in that order so nothing can start it up again in between.</summary>
    private async Task FailReceivingAsync(Guid peerId, Guid transferId, FileTransferState state, string? failure)
    {
        FinishIncoming(peerId, transferId, state, failure);
        await AbandonReceivingAsync(peerId, transferId).ConfigureAwait(false);
    }

    /// <summary>Removes the temporary file, including that of a paused transfer, which nothing has open.</summary>
    private async Task AbandonReceivingAsync(Guid peerId, Guid transferId)
    {
        Receiving? receiving;
        lock (_gate)
        {
            _receiving.Remove(transferId, out receiving);
        }

        if (receiving is not null)
        {
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
        }

        var path = receiving?.TemporaryPath;
        if (path is null)
        {
            try
            {
                path = _store.GetTransfer(peerId, MessageDirection.Incoming, transferId) is { } transfer ? PartialPathOf(transfer) : DefaultPartialPath(transferId);
            }
            catch (MessageStoreException)
            {
                path = DefaultPartialPath(transferId);
            }
        }

        DeleteQuietly(path);
        RemovePartialFolderIfEmpty(path);
    }

    /// <summary>Everything open pauses, so it can continue after the next start.</summary>
    private async Task StopEverythingAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);

        List<Receiving> receiving;
        lock (_gate)
        {
            receiving = [.. _receiving.Values];
            foreach (var sending in _sending.Values)
            {
                sending.Cancellation.Cancel();
            }
        }

        foreach (var item in receiving)
        {
            await PauseReceivingAsync(item).ConfigureAwait(false);
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

        ForgetProgress(peerId, transferId);
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

        ForgetProgress(transfer.PeerId, transfer.TransferId);
        RaiseChanged();
    }

    private void ForgetProgress(Guid peerId, Guid transferId)
    {
        lock (_gate)
        {
            _progress.Remove((peerId, transferId));
            _ready.Remove(transferId);
        }
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
        // The folder the unfinished file is in decides, so this stays one rename on one drive. A move across
        // drives is a copy, and a crash part-way would leave a damaged file under its final name.
        var folder = FinalFolderOf(receiving.TemporaryPath);
        Directory.CreateDirectory(folder);
        var name = receiving.Offer.Filename;

        // Before the file gets its real name, and it moves with the file, so it's never there without the mark.
        MarkAsFromElsewhere(receiving.TemporaryPath);
        for (var counter = 0; ; counter++)
        {
            var candidate = Path.Combine(folder, counter == 0 ? name : SafeFilename.WithCounter(name, counter));
            try
            {
                File.Move(receiving.TemporaryPath, candidate, overwrite: false);
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

    private bool CanUseReceiveFolder()
    {
        try
        {
            Directory.CreateDirectory(_receiveFolder);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            LogSaveFailed(_logger, Guid.Empty, ex.Message);
            return false;
        }
    }

    private long? FreeSpace()
    {
        try
        {
            var root = Path.GetPathRoot(_receiveFolder);
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

    /// <summary>
    /// Where a newly accepted file is written: on the same drive as where it will end up, so a large file needs
    /// room on that drive only, and finishing it is a rename.
    /// </summary>
    private string PathForNewFile(Guid peerId, Guid transferId)
    {
        var folder = _receiveFolder;
        if (string.Equals(folder, Path.GetFullPath(_options.DownloadsDirectory), StringComparison.OrdinalIgnoreCase))
        {
            return DefaultPartialPath(transferId);
        }

        var partials = Path.Combine(folder, PartialFolderName);
        Directory.CreateDirectory(partials);
        File.SetAttributes(partials, File.GetAttributes(partials) | FileAttributes.Hidden);
        var path = Path.Combine(partials, $"{transferId:N}.part");
        _store.SavePartialPath(peerId, transferId, path);
        return path;
    }

    private string PartialPathOf(StoredTransfer transfer) => transfer.PartialPath ?? DefaultPartialPath(transfer.TransferId);

    private string DefaultPartialPath(Guid transferId)
    {
        Directory.CreateDirectory(_options.IncomingDirectory);
        return Path.Combine(_options.IncomingDirectory, $"{transferId:N}.part");
    }

    private string FinalFolderOf(string partialPath)
    {
        var folder = Path.GetDirectoryName(partialPath);
        return folder is not null && Path.GetFileName(folder) == PartialFolderName
            ? Path.GetDirectoryName(folder)!
            : _options.DownloadsDirectory;
    }

    private static void RemovePartialFolderIfEmpty(string partialPath)
    {
        var folder = Path.GetDirectoryName(partialPath);
        if (folder is null || Path.GetFileName(folder) != PartialFolderName)
        {
            return;
        }

        try
        {
            Directory.Delete(folder, recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Another file is still arriving, or something has the folder open. It's hidden and harmless.
        }
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

        /// <summary>How much is recorded as safely on disk.</summary>
        public long Checkpointed { get; set; }

        /// <summary>FILE_RESUME went out on this session, and no chunk has answered it yet.</summary>
        public bool ResumePending { get; set; }

        public bool IsVerifying { get; set; }

        public uint NextSequence { get; set; }

        public bool HasStarted { get; set; }

        public bool IsClosed { get; set; }
    }

    private sealed record Sending(Guid PeerId, Guid SessionId, CancellationTokenSource Cancellation, long Offset);

    /// <summary>A write failed: the transfer pauses, unlike when the file itself can't be read.</summary>
    private sealed class ConnectionLostException(string message) : Exception(message);

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
