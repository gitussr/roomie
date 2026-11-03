using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Roomie.Core.Files;
using Roomie.Core.Messaging;

namespace Roomie.App.ViewModels;

/// <summary>
/// One transfer in the open conversation. Updated in place, so its buttons stay clickable while progress changes.
/// </summary>
public sealed partial class FileTransferItem(Guid transferId, Guid peerId, MessageDirection direction, string name) : ObservableObject
{
    public Guid TransferId { get; } = transferId;

    public Guid PeerId { get; } = peerId;

    public MessageDirection Direction { get; } = direction;

    public string Name { get; } = name;

    public string? LocalPath { get; private set; }

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private string? _warning;

    [ObservableProperty]
    private double _percent;

    [ObservableProperty]
    private bool _isMoving;

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

    public void Update(StoredTransfer transfer, long moved)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        var incoming = transfer.Direction == MessageDirection.Incoming;
        var size = FormatSize(transfer.FileSize);

        LocalPath = transfer.LocalPath;
        IsMoving = transfer.State == FileTransferState.Transferring;
        Percent = transfer.FileSize > 0 ? Math.Min(100, moved * 100d / transfer.FileSize) : 0;
        IsProblem = transfer.State == FileTransferState.Failed;
        CanAnswer = incoming && transfer.State == FileTransferState.Offered;
        CanCancel = !CanAnswer && transfer.State < FileTransferState.Verifying;
        CanShow = incoming && transfer.State == FileTransferState.Completed;
        CanSendAgain = !incoming && transfer.State is FileTransferState.Failed or FileTransferState.Rejected or FileTransferState.Canceled;
        Warning = CanAnswer && SafeFilename.IsRunnable(transfer.Filename)
            ? "This kind of file can run programs. Accept it only if you expected it and trust the sender."
            : null;

        Status = transfer.State switch
        {
            FileTransferState.Waiting => $"{size} · Will be offered when this device is connected",
            FileTransferState.Offered => incoming ? $"{size} · Offered to you" : $"{size} · Waiting for them to accept",
            FileTransferState.Accepted => $"{size} · Starting",
            FileTransferState.Transferring => $"{FormatSize(moved)} of {size}",
            FileTransferState.Verifying => incoming ? $"{size} · Checking the file" : $"{size} · Waiting for them to check the file",
            FileTransferState.Completed => incoming ? $"{size} · Saved and verified" : $"{size} · Sent and verified",
            FileTransferState.Rejected => incoming ? $"{size} · You declined" : $"{size} · They declined",
            FileTransferState.Canceled => $"{size} · Canceled",
            FileTransferState.Failed => transfer.Failure ?? "This transfer failed.",
            _ => size,
        };
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => string.Create(CultureInfo.CurrentCulture, $"{bytes} bytes"),
        < 1024 * 1024 => string.Create(CultureInfo.CurrentCulture, $"{bytes / 1024d:0.#} KB"),
        _ => string.Create(CultureInfo.CurrentCulture, $"{bytes / (1024d * 1024):0.#} MB"),
    };
}
