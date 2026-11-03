using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Roomie.Core.Files;
using Roomie.Core.Messaging;
using Roomie.Network.Files;

namespace Roomie.App.ViewModels;

/// <summary>
/// One transfer in a list. Updated in place, so its buttons stay clickable while progress changes.
/// </summary>
public sealed partial class FileTransferItem(Guid transferId, Guid peerId, MessageDirection direction, string name) : ObservableObject
{
    private readonly TransferSpeed _speed = new();

    public Guid TransferId { get; } = transferId;

    public Guid PeerId { get; } = peerId;

    public MessageDirection Direction { get; } = direction;

    public string Name { get; } = name;

    public string? LocalPath { get; private set; }

    public string DirectionGlyph => Direction == MessageDirection.Incoming ? "" : "";

    public string DirectionLabel => Direction == MessageDirection.Incoming ? "Receiving" : "Sending";

    /// <summary>Who it's with, where the list covers more than one device.</summary>
    [ObservableProperty]
    private string? _peerLine;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private string? _warning;

    [ObservableProperty]
    private double _percent;

    [ObservableProperty]
    private bool _isMoving;

    /// <summary>Data really is moving now, as opposed to waiting to continue.</summary>
    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private bool _isProblem;

    [ObservableProperty]
    private bool _canAnswer;

    [ObservableProperty]
    private bool _canCancel;

    [ObservableProperty]
    private bool _canShow;

    [ObservableProperty]
    private bool _canSendAgain;

    /// <summary>Brings a list in line with the store, newest first, keeping the items that are still there.</summary>
    public static void Sync(
        ObservableCollection<FileTransferItem> items,
        IReadOnlyList<StoredTransfer> transfers,
        FileTransferService files,
        Func<Guid, string>? peerName = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(transfers);
        ArgumentNullException.ThrowIfNull(files);

        for (var i = items.Count - 1; i >= 0; i--)
        {
            var item = items[i];
            if (transfers.All(t => t.TransferId != item.TransferId || t.Direction != item.Direction || t.PeerId != item.PeerId))
            {
                items.RemoveAt(i);
            }
        }

        for (var i = 0; i < transfers.Count; i++)
        {
            var transfer = transfers[i];
            var item = items.FirstOrDefault(f => f.TransferId == transfer.TransferId && f.Direction == transfer.Direction && f.PeerId == transfer.PeerId);
            if (item is null)
            {
                item = new FileTransferItem(transfer.TransferId, transfer.PeerId, transfer.Direction, transfer.Filename);
                items.Insert(Math.Min(i, items.Count), item);
            }

            item.PeerLine = peerName is null ? null : $"{(transfer.Direction == MessageDirection.Incoming ? "From" : "To")} {peerName(transfer.PeerId)}";
            item.Update(transfer, files.GetProgress(transfer.PeerId, transfer.TransferId), files.IsPaused(transfer));
        }
    }

    public void Update(StoredTransfer transfer, long moved, bool isPaused)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        var incoming = transfer.Direction == MessageDirection.Incoming;
        var size = FormatSize(transfer.FileSize);

        LocalPath = transfer.LocalPath;
        IsMoving = transfer.State == FileTransferState.Transferring;
        IsActive = IsMoving && !isPaused;
        Percent = transfer.FileSize > 0 ? Math.Min(100, moved * 100d / transfer.FileSize) : 0;
        IsProblem = transfer.State == FileTransferState.Failed;
        CanAnswer = incoming && transfer.State == FileTransferState.Offered;
        CanCancel = !CanAnswer && transfer.State < FileTransferState.Verifying;
        CanShow = incoming && transfer.State == FileTransferState.Completed;
        CanSendAgain = !incoming && transfer.State is FileTransferState.Failed or FileTransferState.Rejected or FileTransferState.Canceled;
        Warning = CanAnswer && SafeFilename.IsRunnable(transfer.Filename)
            ? "This kind of file can run programs. Accept it only if you expected it and trust the sender."
            : null;

        if (IsActive)
        {
            _speed.Update(moved, DateTimeOffset.UtcNow);
        }

        Status = transfer.State switch
        {
            _ when isPaused && moved > 0 => $"Paused at {FormatSize(moved)} of {size} · Continues by itself when this device is connected",
            _ when isPaused => $"{size} · Paused · Continues by itself when this device is connected",
            FileTransferState.Waiting => $"{size} · Will be offered when this device is connected",
            FileTransferState.Offered => incoming ? $"{size} · Offered to you" : $"{size} · Waiting for them to accept",
            FileTransferState.Accepted => $"{size} · Starting",
            FileTransferState.Transferring => $"{FormatSize(moved)} of {size}{SpeedText(moved, transfer.FileSize)}",
            FileTransferState.Verifying => incoming ? $"{size} · Checking the file" : $"{size} · Waiting for them to check the file",
            FileTransferState.Completed => incoming ? $"{size} · Saved and verified" : $"{size} · Sent and verified",
            FileTransferState.Rejected => incoming ? $"{size} · You declined" : $"{size} · They declined",
            FileTransferState.Canceled => $"{size} · Canceled",
            FileTransferState.Failed => transfer.Failure ?? "This transfer failed.",
            _ => size,
        };
    }

    private string SpeedText(long moved, long total)
    {
        if (_speed.BytesPerSecond is not > 0)
        {
            return string.Empty;
        }

        var rate = $" · {FormatSize((long)_speed.BytesPerSecond.Value)}/s";
        return _speed.TimeLeft(moved, total) is { } left ? $"{rate} · {FormatDuration(left)} left" : rate;
    }

    private static string FormatDuration(TimeSpan time) => time.TotalHours >= 1
        ? string.Create(CultureInfo.CurrentCulture, $"{(int)time.TotalHours} h {time.Minutes} min")
        : time.TotalMinutes >= 1
            ? string.Create(CultureInfo.CurrentCulture, $"{(int)time.TotalMinutes} min")
            : string.Create(CultureInfo.CurrentCulture, $"{Math.Max(1, (int)time.TotalSeconds)} s");

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => string.Create(CultureInfo.CurrentCulture, $"{bytes} bytes"),
        < 1024 * 1024 => string.Create(CultureInfo.CurrentCulture, $"{bytes / 1024d:0.#} KB"),
        < 1024 * 1024 * 1024 => string.Create(CultureInfo.CurrentCulture, $"{bytes / (1024d * 1024):0.#} MB"),
        _ => string.Create(CultureInfo.CurrentCulture, $"{bytes / (1024d * 1024 * 1024):0.##} GB"),
    };
}
