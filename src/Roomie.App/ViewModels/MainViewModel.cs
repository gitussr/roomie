using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roomie.Core.Configuration;
using Roomie.Core.Discovery;
using Roomie.Core.Identity;
using Roomie.Infrastructure.Identity;
using Roomie.Infrastructure.Storage;
using Roomie.Network.Discovery;

namespace Roomie.App.ViewModels;

/// <summary>A discovered device as shown in the list. Discovered only: not connected, not trusted.</summary>
public sealed record NearbyDevice(string Username, string DisplayId, string Address, string Details);

public sealed partial class MainViewModel : ObservableObject
{
    private readonly DeviceIdentityStore _identityStore;
    private readonly PeerRegistry _registry;
    private readonly DiscoveryService _discovery;
    private readonly SynchronizationContext _ui;
    private int _refreshQueued;

    public MainViewModel(AppPaths paths, DeviceIdentityStore identityStore, PeerRegistry registry, DiscoveryService discovery)
    {
        _identityStore = identityStore;
        _registry = registry;
        _discovery = discovery;

        // Created on the UI thread; registry and discovery events arrive on background threads.
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();

        DataDirectory = paths.Root;
        AppVersion = App.AppVersion;

        var identity = identityStore.Identity;
        DisplayId = identity.DisplayId;
        Fingerprint = CertificateFingerprint.ToDisplayString(identity.CertificateFingerprint);
        _username = identity.Username;
        _usernameDraft = identity.Username;

        registry.Changed += (_, _) => QueueRefresh();
        discovery.StatusChanged += (_, _) => QueueRefresh();
        Refresh();
    }

    public string Title { get; } = "Roomie";

    public string AppVersion { get; }

    public string DataDirectory { get; }

    public string DisplayId { get; }

    public string Fingerprint { get; }

    public string NetworkSummary { get; } =
        $"Discovery UDP {NetworkDefaults.DiscoveryUdpPort} · Messaging TCP {NetworkDefaults.MessagingTcpPort}";

    public ObservableCollection<NearbyDevice> NearbyDevices { get; } = [];

    [ObservableProperty]
    private string _status = "Starting discovery…";

    [ObservableProperty]
    private string? _discoveryProblem;

    [ObservableProperty]
    private string _emptyListHint = string.Empty;

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
        _discovery.AnnounceNow();
    }

    // Coalesces bursts of background events into one UI update.
    private void QueueRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 0)
        {
            _ui.Post(_ =>
            {
                Interlocked.Exchange(ref _refreshQueued, 0);
                Refresh();
            }, null);
        }
    }

    private void Refresh()
    {
        NearbyDevices.Clear();
        foreach (var peer in _registry.Snapshot())
        {
            var address = peer.Addresses.Count > 1
                ? $"{peer.Addresses[0]} (+{peer.Addresses.Count - 1})"
                : peer.Addresses.Count == 1 ? peer.Addresses[0].ToString() : string.Empty;
            NearbyDevices.Add(new NearbyDevice(peer.Username, peer.DisplayId, address, $"Not paired · Roomie {peer.AppVersion}"));
        }

        var status = _discovery.Status;
        Status = status.State switch
        {
            DiscoveryState.Starting => "Starting discovery…",
            DiscoveryState.Running => $"Looking for devices on {string.Join(", ", status.Interfaces.Select(i => i.Name).Distinct())}",
            DiscoveryState.NoNetwork => "No local network: connect to a home or office network",
            DiscoveryState.Failed => "Discovery isn't working",
            DiscoveryState.Stopped => "Discovery stopped",
            _ => string.Empty,
        };
        DiscoveryProblem = status.Problems.Count == 0 ? null : string.Join("\n", status.Problems);

        EmptyListHint = NearbyDevices.Count > 0 ? string.Empty
            : status.State == DiscoveryState.Running
                ? "No devices found yet. Roomie must be running on the other computer, on the same network. "
                  + "If it is, check that Windows Firewall allows Roomie on private networks and that both "
                  + "networks are set to Private."
                : "No devices found.";
    }
}
