using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roomie.Core.Identity;
using Roomie.Network.Pairing;

namespace Roomie.App.ViewModels;

/// <summary>The pairing panel: the code to compare and the user's choices (architecture.md §3.1, step 5–6).</summary>
public sealed partial class PairingViewModel : ObservableObject
{
    private readonly PairingService _pairing;

    public PairingViewModel(PairingService pairing)
    {
        _pairing = pairing;
    }

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string? _code;

    [ObservableProperty]
    private string _instruction = string.Empty;

    [ObservableProperty]
    private bool _isSuccess;

    [ObservableProperty]
    private bool _isProblem;

    [ObservableProperty]
    private bool _canApprove;

    [ObservableProperty]
    private bool _canCancel;

    [ObservableProperty]
    private string _cancelLabel = "Cancel";

    [ObservableProperty]
    private bool _canBlock;

    [ObservableProperty]
    private bool _canDismiss;

    [ObservableProperty]
    private string _localFingerprint = string.Empty;

    [ObservableProperty]
    private string _remoteFingerprint = string.Empty;

    [ObservableProperty]
    private string _remoteLabel = string.Empty;

    public bool HasCode => Code is not null;

    [RelayCommand]
    private void Approve() => _pairing.Approve();

    [RelayCommand]
    private void Cancel() => _pairing.Cancel();

    [RelayCommand]
    private void Block() => _pairing.Block();

    [RelayCommand]
    private void Dismiss() => _pairing.Dismiss();

    partial void OnCodeChanged(string? value) => OnPropertyChanged(nameof(HasCode));

    public void Update(PairingInfo? info)
    {
        IsVisible = info is not null;
        if (info is null)
        {
            return;
        }

        var name = info.PeerName;
        Code = info.Stage is PairingStage.Comparing or PairingStage.WaitingForPeer ? info.Code : null;
        IsSuccess = info.Stage == PairingStage.Paired;
        IsProblem = info.Stage is PairingStage.Failed or PairingStage.Declined;
        CanApprove = info.Stage == PairingStage.Comparing;
        CanCancel = info.IsActive;
        CanBlock = info.IsActive && info.Role == PairingRole.Responder;
        CanDismiss = !info.IsActive;
        CancelLabel = info.Stage == PairingStage.Comparing ? "The codes don't match" : "Cancel";

        LocalFingerprint = CertificateFingerprint.ToDisplayString(info.LocalFingerprint);
        RemoteFingerprint = info.RemoteFingerprint.Length == 0 ? string.Empty : CertificateFingerprint.ToDisplayString(info.RemoteFingerprint);
        RemoteLabel = $"{name} ({info.DisplayId})";

        (Title, Instruction) = info.Stage switch
        {
            PairingStage.Requesting => ($"Pairing with {name}", $"Waiting for {name} to respond…"),
            PairingStage.AwaitingReveal => ($"{name} wants to pair", "Preparing the verification code…"),
            PairingStage.Comparing => ($"Verify with {name}",
                $"Check that {name} shows exactly the same code. If the codes are different, don't pair: someone may be intercepting the connection."),
            PairingStage.WaitingForPeer => ($"Verify with {name}", $"You confirmed the code. Waiting for {name} to confirm it too…"),
            PairingStage.Paired => ($"Paired with {name}", info.Message ?? string.Empty),
            PairingStage.Declined => ("Not paired", info.Message ?? string.Empty),
            _ => ("Pairing failed", info.Message ?? string.Empty),
        };
    }
}
