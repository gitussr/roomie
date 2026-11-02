using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roomie.Core.Configuration;
using Roomie.Core.Connections;
using Roomie.Core.Discovery;
using Roomie.Core.Identity;
using Roomie.Core.Messaging;
using Roomie.Core.Trust;
using Roomie.Infrastructure.Identity;
using Roomie.Infrastructure.Storage;
using Roomie.Network.Connections;
using Roomie.Network.Discovery;
using Roomie.Network.Messaging;
using Roomie.Network.Pairing;

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
    bool CanDisconnect,
    bool CanPair,
    bool CanForget,
    bool CanUnblock,
    bool CanChat = false,
    string ChatLabel = "Chat");

public sealed partial class MainViewModel : ObservableObject
{
    private readonly DeviceIdentityStore _identityStore;
    private readonly PeerRegistry _registry;
    private readonly DiscoveryService _discovery;
    private readonly ConnectionManager _connections;
    private readonly PairingService _pairing;
    private readonly ITrustStore _trust;
    private readonly IMessageStore _messages;
    private readonly SynchronizationContext _ui;
    private int _refreshQueued;

    public MainViewModel(
        AppPaths paths,
        DeviceIdentityStore identityStore,
        PeerRegistry registry,
        DiscoveryService discovery,
        ConnectionManager connections,
        PairingService pairing,
        MessagingService messaging,
        IMessageStore messages,
        ITrustStore trust)
    {
        ArgumentNullException.ThrowIfNull(messaging);
        _messages = messages;
        Chat = new ChatViewModel(messaging, messages, trust);

        _identityStore = identityStore;
        _registry = registry;
        _discovery = discovery;
        _connections = connections;
        _pairing = pairing;
        _trust = trust;
        Pairing = new PairingViewModel(pairing);

        // Created on the UI thread; every service event arrives on a background thread.
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
        pairing.Changed += (_, _) => QueueRefresh();
        pairing.IncomingRequest += (_, _) => _ui.Post(_ => AttentionRequested?.Invoke(this, EventArgs.Empty), null);
        trust.Changed += (_, _) => QueueRefresh();
        messaging.Changed += (_, _) => QueueRefresh();
        Refresh();
    }

    /// <summary>Another device asked to pair: the window should get the user's attention.</summary>
    public event EventHandler? AttentionRequested;

    public PairingViewModel Pairing { get; }

    public ChatViewModel Chat { get; }

    public string Title { get; } = "Roomie";

    /// <summary>Includes the unread count, so new messages show on the taskbar.</summary>
    [ObservableProperty]
    private string _windowTitle = "Roomie";

    [ObservableProperty]
    private bool _isChatVisible;

    [ObservableProperty]
    private bool _isWelcomeVisible = true;

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

    [RelayCommand]
    private async Task PairAsync(Guid deviceId)
    {
        if (_pairing.Current is { IsActive: true })
        {
            return;
        }

        await _pairing.StartAsync(deviceId);
    }

    [RelayCommand]
    private void Forget(Guid deviceId)
    {
        var name = _trust.FindTrusted(deviceId)?.Username ?? DeviceIdFormat.ToDisplayId(deviceId);
        var answer = MessageBox.Show(
            $"Forget {name}?\n\nThis computer will stop trusting it and disconnect. To exchange messages again, you'll need to pair again on both computers.\n\nYour messages with it stay on this computer, and any still waiting are sent if you pair again.",
            "Roomie",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Cancel);

        if (answer == MessageBoxResult.OK)
        {
            _trust.Remove(deviceId);
        }
    }

    [RelayCommand]
    private void Unblock(Guid deviceId) => _trust.Unblock(deviceId);

    [RelayCommand]
    private void OpenChat(Guid deviceId)
    {
        Chat.Open(deviceId);
        Refresh();
    }

    [RelayCommand]
    private void CloseChat()
    {
        Chat.CloseCommand.Execute(null);
        Refresh();
    }

    /// <summary>Called when the window gains or loses focus: messages count as read only while it has focus.</summary>
    public void OnWindowActiveChanged(bool isActive)
    {
        Chat.IsWindowActive = isActive;
        QueueRefresh();
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
        Pairing.Update(_pairing.Current);
        var pairingBusy = _pairing.Current is { IsActive: true };

        NearbyDevices.Clear();
        var connections = _connections.Snapshot().ToDictionary(c => c.DeviceId);
        var peers = _registry.Snapshot();
        var trusted = _trust.TrustedPeers.ToDictionary(p => p.DeviceId);
        var blocked = _trust.BlockedDevices.ToHashSet();

        PeerConnectionInfo ConnectionOf(Guid id) =>
            connections.GetValueOrDefault(id) ?? new PeerConnectionInfo(id, PeerConnectionState.Disconnected);

        // Updated first: an open conversation being read must not count as unread below.
        if (Chat.PeerId is { } chatPeer)
        {
            var info = ConnectionOf(chatPeer);
            Chat.Update(info.IsConnected ? "Connected" : "Not connected. Messages will be sent when this device is back.");
        }

        IsChatVisible = Chat.IsOpen && !Pairing.IsVisible;
        IsWelcomeVisible = !Chat.IsOpen && !Pairing.IsVisible;

        var unread = ReadUnreadCounts();
        var totalUnread = unread.Values.Sum();
        WindowTitle = totalUnread > 0 ? $"({totalUnread}) {Title}" : Title;

        foreach (var peer in peers)
        {
            var address = peer.Addresses.Count > 1
                ? $"{peer.Addresses[0]} (+{peer.Addresses.Count - 1})"
                : peer.Addresses.Count == 1 ? peer.Addresses[0].ToString() : string.Empty;
            NearbyDevices.Add(ToItem(peer.Username, peer.DisplayId, address, $"Roomie {peer.AppVersion}",
                ConnectionOf(peer.DeviceId), trusted.ContainsKey(peer.DeviceId), blocked.Contains(peer.DeviceId), pairingBusy));
        }

        // Connected devices whose announcements stopped reaching us, then paired devices that are offline.
        var listed = peers.Select(p => p.DeviceId).ToHashSet();
        foreach (var connection in connections.Values.Where(c => c.IsConnected && !listed.Contains(c.DeviceId)))
        {
            listed.Add(connection.DeviceId);
            var name = trusted.TryGetValue(connection.DeviceId, out var known) ? known.Username : "(not announcing)";
            NearbyDevices.Add(ToItem(name, DeviceIdFormat.ToDisplayId(connection.DeviceId),
                connection.RemoteEndPoint?.Address.ToString() ?? string.Empty, string.Empty, connection,
                trusted.ContainsKey(connection.DeviceId), isBlocked: false, pairingBusy));
        }

        foreach (var peer in trusted.Values.Where(p => !listed.Contains(p.DeviceId)))
        {
            NearbyDevices.Add(ToItem(peer.Username, DeviceIdFormat.ToDisplayId(peer.DeviceId), "offline", string.Empty,
                ConnectionOf(peer.DeviceId), isTrusted: true, isBlocked: false, pairingBusy));
        }

        for (var i = 0; i < NearbyDevices.Count; i++)
        {
            var count = unread.GetValueOrDefault(NearbyDevices[i].DeviceId);
            NearbyDevices[i] = NearbyDevices[i] with
            {
                CanChat = NearbyDevices[i].CanForget,
                ChatLabel = count > 0 ? $"Chat ({count})" : "Chat",
            };
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

    private Dictionary<Guid, int> ReadUnreadCounts()
    {
        try
        {
            return _messages.GetActiveConversations().ToDictionary(c => c.PeerId, c => c.UnreadCount);
        }
        catch (MessageStoreException)
        {
            // The device list still works without the counts. The store logged the failure.
            return [];
        }
    }

    private static NearbyDevice ToItem(
        string username,
        string displayId,
        string address,
        string version,
        PeerConnectionInfo connection,
        bool isTrusted,
        bool isBlocked,
        bool pairingBusy)
    {
        var paired = isTrusted ? "paired" : "not paired";
        var text = isBlocked ? "Blocked" : connection.State switch
        {
            PeerConnectionState.Connecting => "Connecting…",
            PeerConnectionState.Authenticating => "Securing the connection…",
            PeerConnectionState.ConnectedUntrusted => $"Connected · not paired · {connection.Security}",
            PeerConnectionState.ConnectedTrusted => $"Connected · paired · {connection.Security}",
            PeerConnectionState.Reconnecting => "Paired · reconnecting…",
            PeerConnectionState.Failed => $"Couldn't connect · {paired}",
            PeerConnectionState.IdentityMismatch => "Identity changed: not trusted",
            PeerConnectionState.Blocked => "Blocked",
            _ => $"Not connected · {paired}",
        };

        if (connection.IsConnected && connection.RemoteFingerprint is { } fingerprint)
        {
            // The first groups of the key fingerprint, so both people can see they reached the same key.
            text += $"\nKey {CertificateFingerprint.ToDisplayString(fingerprint)[..19]}…";
        }

        var canConnect = !isBlocked && connection.State is PeerConnectionState.Disconnected or PeerConnectionState.Failed;
        var canDisconnect = connection.IsConnected || connection.State == PeerConnectionState.Reconnecting;
        var canPair = !isTrusted && !isBlocked && !pairingBusy && connection.State != PeerConnectionState.IdentityMismatch;
        var problem = isBlocked || (connection.State == PeerConnectionState.Disconnected && connection.LastError is null)
            ? null
            : connection.LastError;

        return new NearbyDevice(connection.DeviceId, username, displayId, address, version, text, problem,
            canConnect, canDisconnect, canPair, isTrusted, isBlocked);
    }
}
