using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roomie.Core.Configuration;
using Roomie.Core.Connections;
using Roomie.Core.Discovery;
using Roomie.Core.Identity;
using Roomie.Infrastructure.Identity;
using Roomie.Infrastructure.Storage;
using Roomie.Network.Connections;
using Roomie.Network.Discovery;

namespace Roomie.App.ViewModels;

/// <summary>A device as shown in the list. Discovery, connection and trust are shown separately.</summary>
public sealed record NearbyDevice(
    Guid DeviceId,
    string Username,
    string DisplayId,
    string Address,
    string Details,
    string Connection,
    string? Problem,
    bool CanConnect,
    bool CanDisconnect);

public sealed partial class MainViewModel : ObservableObject
{
    private readonly DeviceIdentityStore _identityStore;
    private readonly PeerRegistry _registry;
    private readonly DiscoveryService _discovery;
    private readonly ConnectionManager _connections;
    private readonly SynchronizationContext _ui;
    private int _refreshQueued;

    public MainViewModel(AppPaths paths, DeviceIdentityStore identityStore, PeerRegistry registry, DiscoveryService discovery, ConnectionManager connections)
    {
        _identityStore = identityStore;
        _registry = registry;
        _discovery = discovery;
        _connections = connections;

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
        connections.Changed += (_, _) => QueueRefresh();
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

    [RelayCommand]
    private async Task ConnectAsync(Guid deviceId) => await _connections.ConnectAsync(deviceId);

    [RelayCommand]
    private async Task DisconnectAsync(Guid deviceId) => await _connections.DisconnectAsync(deviceId);

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
        var connections = _connections.Snapshot().ToDictionary(c => c.DeviceId);
        var peers = _registry.Snapshot();

        foreach (var peer in peers)
        {
            var address = peer.Addresses.Count > 1
                ? $"{peer.Addresses[0]} (+{peer.Addresses.Count - 1})"
                : peer.Addresses.Count == 1 ? peer.Addresses[0].ToString() : string.Empty;
            var connection = connections.GetValueOrDefault(peer.DeviceId) ?? new PeerConnectionInfo(peer.DeviceId, PeerConnectionState.Disconnected);
            NearbyDevices.Add(ToItem(peer.Username, peer.DisplayId, address, $"Roomie {peer.AppVersion}", connection));
        }

        // A device can stay connected for a while after its announcements stop reaching us.
        foreach (var connection in connections.Values.Where(c => c.IsConnected && peers.All(p => p.DeviceId != c.DeviceId)))
        {
            NearbyDevices.Add(ToItem("(not announcing)", DeviceIdFormat.ToDisplayId(connection.DeviceId),
                connection.RemoteEndPoint?.Address.ToString() ?? string.Empty, string.Empty, connection));
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
        var problems = status.Problems.Concat(_connections.Problems).ToList();
        DiscoveryProblem = problems.Count == 0 ? null : string.Join("\n", problems);

        EmptyListHint = NearbyDevices.Count > 0 ? string.Empty
            : status.State == DiscoveryState.Running
                ? "No devices found yet. Roomie must be running on the other computer, on the same network. "
                  + "If it is, check that Windows Firewall allows Roomie on private networks and that both "
                  + "networks are set to Private."
                : "No devices found.";
    }

    private static NearbyDevice ToItem(string username, string displayId, string address, string version, PeerConnectionInfo connection)
    {
        var text = connection.State switch
        {
            PeerConnectionState.Connecting => "Connecting…",
            PeerConnectionState.Authenticating => "Securing the connection…",
            PeerConnectionState.ConnectedUntrusted => $"Connected · not paired · {connection.Security}",
            PeerConnectionState.ConnectedTrusted => $"Connected · paired · {connection.Security}",
            PeerConnectionState.Reconnecting => "Reconnecting…",
            PeerConnectionState.Failed => "Couldn't connect",
            PeerConnectionState.IdentityMismatch => "Identity changed: not trusted",
            PeerConnectionState.Blocked => "Blocked",
            _ => "Not connected · not paired",
        };

        if (connection.IsConnected && connection.RemoteFingerprint is { } fingerprint)
        {
            // The first groups of the key fingerprint, so both people can see they reached the same key.
            text += $"\nKey {CertificateFingerprint.ToDisplayString(fingerprint)[..19]}…";
        }

        var canConnect = connection.State is PeerConnectionState.Disconnected or PeerConnectionState.Failed;
        var canDisconnect = connection.IsConnected || connection.State == PeerConnectionState.Reconnecting;
        return new NearbyDevice(connection.DeviceId, username, displayId, address, version, text,
            connection.State == PeerConnectionState.Disconnected && connection.LastError is null ? null : connection.LastError,
            canConnect, canDisconnect);
    }
}
