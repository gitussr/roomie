using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roomie.Core.Configuration;
using Roomie.Core.Identity;
using Roomie.Infrastructure.Identity;
using Roomie.Infrastructure.Storage;

namespace Roomie.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly DeviceIdentityStore _identityStore;

    public MainViewModel(AppPaths paths, DeviceIdentityStore identityStore)
    {
        _identityStore = identityStore;

        DataDirectory = paths.Root;
        AppVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "unknown";

        var identity = identityStore.Identity;
        DisplayId = identity.DisplayId;
        Fingerprint = CertificateFingerprint.ToDisplayString(identity.CertificateFingerprint);
        _username = identity.Username;
        _usernameDraft = identity.Username;
    }

    public string Title { get; } = "Roomie";

    public string AppVersion { get; }

    public string DataDirectory { get; }

    public string DisplayId { get; }

    public string Fingerprint { get; }

    public string NetworkSummary { get; } =
        $"Discovery UDP {NetworkDefaults.DiscoveryUdpPort} · Messaging TCP {NetworkDefaults.MessagingTcpPort}";

    [ObservableProperty]
    private string _status = "Not connected — networking arrives in a later phase";

    [ObservableProperty]
    private string _username;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveUsernameCommand))]
    private string _usernameDraft;

    [ObservableProperty]
    private string? _usernameError;

    private bool CanSaveUsername() => UsernameDraft.Trim() != Username;

    [RelayCommand(CanExecute = nameof(CanSaveUsername))]
    private void SaveUsername()
    {
        if (!Core.Identity.Username.TryNormalize(UsernameDraft, out var normalized, out var error))
        {
            UsernameError = error;
            return;
        }

        Username = _identityStore.ChangeUsername(normalized).Username;
        UsernameDraft = Username;
        UsernameError = null;
        SaveUsernameCommand.NotifyCanExecuteChanged();
    }
}
