using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roomie.App.Services;
using Roomie.Core.Configuration;
using Roomie.Core.Connections;
using Roomie.Core.Discovery;
using Roomie.Core.Files;
using Roomie.Core.Groups;
using Roomie.Core.Identity;
using Roomie.Core.Messaging;
using Roomie.Core.Notifications;
using Roomie.Core.Trust;
using Roomie.Infrastructure.Identity;
using Roomie.Infrastructure.Storage;
using Roomie.Network.Connections;
using Roomie.Network.Discovery;
using Roomie.Network.Files;
using Roomie.Network.Groups;
using Roomie.Network.Messaging;
using Roomie.Network.Pairing;
using Roomie.Network.Profiles;

namespace Roomie.App.ViewModels;

/// <summary>The screens on the navigation rail.</summary>
public enum AppSection
{
    Chats,
    Devices,
    Transfers,
    Settings,
}

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
    AvatarVisual? Avatar = null)
{
    /// <summary>What a screen reader says for this row. Without it, the record's generated text is read out, IDs and all.</summary>
    public override string ToString() => string.Join(", ", new[] { Username, Connection, Problem }.Where(part => !string.IsNullOrEmpty(part)));
}

/// <summary>A row in the list of conversations: a paired device or a group.</summary>
public sealed record ConversationItem(
    Guid? PeerId,
    string? GroupId,
    string Name,
    string Preview,
    string Time,
    int Unread,
    bool IsOnline,
    bool IsOpen,
    DateTimeOffset LastActivity,
    AvatarVisual Avatar)
{
    public bool IsGroup => GroupId is not null;

    public bool IsDevice => GroupId is null;

    public bool HasUnread => Unread > 0;

    /// <summary>Spelled out because a coloured dot alone tells a screen reader, or someone who can't see the colour, nothing.</summary>
    /// <summary>What a screen reader says for this row. Without it, the record's generated text is read out, IDs and all.</summary>
    public override string ToString() => Description;

    public string Description =>
        $"{Name}, {(IsGroup ? "group" : IsOnline ? "online" : "offline")}{(HasUnread ? $", {Unread} unread" : string.Empty)}";
}

/// <summary>An invitation waiting for this user's answer.</summary>
public sealed record GroupInviteItem(string GroupId, Guid OwnerId, string GroupName, string Details)
{
    /// <summary>What a screen reader says for this row. Without it, the record's generated text is read out, IDs and all.</summary>
    public override string ToString() => $"{GroupName}, {Details}";
}

public sealed partial class MainViewModel : ObservableObject
{
    private const int MaxShownTransfers = 200;
    private const int PreviewLength = 70;

    private readonly DeviceIdentityStore _identityStore;
    private readonly PeerRegistry _registry;
    private readonly DiscoveryService _discovery;
    private readonly ConnectionManager _connections;
    private readonly PairingService _pairing;
    private readonly GroupService _groups;
    private readonly FileTransferService _files;
    private readonly ITrustStore _trust;
    private readonly IMessageStore _messages;
    private readonly AvatarCatalog _avatars;
    private readonly ProfileService _profiles;
    private readonly SynchronizationContext _ui;
    private int _refreshQueued;

    public MainViewModel(
        DeviceIdentityStore identityStore,
        PeerRegistry registry,
        DiscoveryService discovery,
        ConnectionManager connections,
        PairingService pairing,
        MessagingService messaging,
        GroupService groups,
        FileTransferService files,
        IMessageStore messages,
        ITrustStore trust,
        SettingsViewModel settings,
        AvatarCatalog avatars,
        ProfileService profiles,
        JsonSettingsStore appSettings)
    {
        ArgumentNullException.ThrowIfNull(messaging);
        _messages = messages;
        _groups = groups;
        _files = files;
        Settings = settings;
        _avatars = avatars;
        _profiles = profiles;
        Chat = new ChatViewModel(messaging, groups, files, messages, trust, identityStore, avatars);
        Chat.FilesChanged += (_, _) => RefreshTransfers();

        _identityStore = identityStore;
        _registry = registry;
        _discovery = discovery;
        _connections = connections;
        _pairing = pairing;
        _trust = trust;
        Pairing = new PairingViewModel(pairing);

        // Created on the UI thread; every service event arrives on a background thread.
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();

        AppVersion = App.AppVersion;

        var identity = identityStore.Identity;
        DisplayId = identity.DisplayId;
        Fingerprint = CertificateFingerprint.ToDisplayString(identity.CertificateFingerprint);
        _username = identity.Username;
        _usernameDraft = identity.Username;

        Welcome = new WelcomeViewModel(appSettings, TryRename, OpenChat);
        var pairedDevices = trust.TrustedPeers.Count;
        if (appSettings.Current.NeedsWelcome(pairedDevices))
        {
            Welcome.Show(identity.Username, trust.TrustedPeers);
        }
        else if (!appSettings.Current.HasSeenWelcome)
        {
            // Updated from before the welcome existed. Forgetting every device later mustn't bring it up.
            appSettings.Update(s => s with { HasSeenWelcome = true });
        }

        registry.Changed += (_, _) => QueueRefresh();
        discovery.StatusChanged += (_, _) => QueueRefresh();
        connections.Changed += (_, _) => QueueRefresh();
        pairing.Changed += (_, _) => QueueRefresh();
        pairing.IncomingRequest += (_, _) => _ui.Post(_ => AttentionRequested?.Invoke(this, EventArgs.Empty), null);
        trust.Changed += (_, _) => QueueRefresh();
        messaging.Changed += (_, _) => QueueRefresh();
        groups.Changed += (_, _) => QueueRefresh();
        groups.IncomingInvite += (_, _) => _ui.Post(_ => AttentionRequested?.Invoke(this, EventArgs.Empty), null);
        files.Changed += (_, _) => QueueRefresh();
        profiles.Changed += (_, _) => QueueRefresh();
        files.IncomingOffer += (_, _) => _ui.Post(_ => AttentionRequested?.Invoke(this, EventArgs.Empty), null);

        // Only the file lists: rebuilding the device list this often would make its buttons hard to click.
        files.ProgressChanged += (_, _) => _ui.Post(_ =>
        {
            Chat.RefreshFiles();
            RefreshTransfers();
        }, null);
        Refresh();
    }

    /// <summary>Another device asked for something that needs an answer: the window should get the user's attention.</summary>
    public event EventHandler? AttentionRequested;

    public PairingViewModel Pairing { get; }

    public WelcomeViewModel Welcome { get; }

    public ChatViewModel Chat { get; }

    public SettingsViewModel Settings { get; }

    public string Title { get; } = "Roomie";

    /// <summary>Includes the unread count, so new messages show on the taskbar.</summary>
    [ObservableProperty]
    private string _windowTitle = "Roomie";

    [ObservableProperty]
    private AppSection _section = AppSection.Chats;

    [ObservableProperty]
    private bool _isChatVisible;

    [ObservableProperty]
    private bool _isWelcomeVisible = true;

    public string AppVersion { get; }

    public string DisplayId { get; }

    public string Fingerprint { get; }

    public string NetworkSummary { get; } =
        $"Discovery: UDP {NetworkDefaults.DiscoveryUdpPort}. Messaging and files: TCP {NetworkDefaults.MessagingTcpPort}.";

    public ObservableCollection<NearbyDevice> NearbyDevices { get; } = [];

    public ObservableCollection<ConversationItem> Conversations { get; } = [];

    public ObservableCollection<GroupInviteItem> GroupInvites { get; } = [];

    /// <summary>Every recent transfer, with any device, newest first.</summary>
    public ObservableCollection<FileTransferItem> AllTransfers { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoTransfers))]
    private bool _hasTransfers;

    public bool HasNoTransfers => !HasTransfers;

    [ObservableProperty]
    private bool _hasNoConversations;

    [ObservableProperty]
    private int _totalUnread;

    /// <summary>Transfers moving or waiting for this user's answer, for the rail.</summary>
    [ObservableProperty]
    private int _transfersNeedingAttention;

    /// <summary>How far the files now moving have got, from 0 to 1, or null if none are moving.</summary>
    [ObservableProperty]
    private double? _taskbarProgress;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateGroupCommand))]
    private string _newGroupName = string.Empty;

    [ObservableProperty]
    private string? _groupError;

    [ObservableProperty]
    private string _status = "Starting discovery…";

    [ObservableProperty]
    private string _onlineSummary = string.Empty;

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

    /// <summary>The window has focus, so what it shows is really being seen.</summary>
    public bool IsWindowActive => Chat.IsWindowActive;

    private bool CanSaveUsername() => UsernameDraft.Trim() != Username;

    [RelayCommand(CanExecute = nameof(CanSaveUsername))]
    private void SaveUsername() => UsernameError = TryRename(UsernameDraft);

    /// <returns>What's wrong with the name, or null once this computer has it.</returns>
    private string? TryRename(string name)
    {
        if (!Core.Identity.Username.TryNormalize(name, out var normalized, out var error))
        {
            return error;
        }

        Username = _identityStore.ChangeUsername(normalized).Username;
        UsernameDraft = Username;
        UsernameError = null;
        Settings.RefreshOwnAvatar();
        SaveUsernameCommand.NotifyCanExecuteChanged();
        _discovery.AnnounceNow();
        _profiles.AnnounceOwnName();
        _groups.RefreshOwnedNames();
        return null;
    }

    /// <summary>Goes through the welcome again. A conversation left open would count as read behind it, so that closes.</summary>
    [RelayCommand]
    private void ShowWelcome()
    {
        CloseChat();
        Welcome.Show(Username, _trust.TrustedPeers);
        Refresh();
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
        Section = AppSection.Chats;
        Chat.Open(deviceId);
        Refresh();
    }

    [RelayCommand]
    private void OpenConversation(ConversationItem item)
    {
        if (item.PeerId is { } peerId)
        {
            OpenChat(peerId);
        }
        else if (item.GroupId is { } groupId)
        {
            OpenGroup(groupId);
        }
    }

    private bool CanCreateGroup() => !string.IsNullOrWhiteSpace(NewGroupName);

    [RelayCommand(CanExecute = nameof(CanCreateGroup))]
    private void CreateGroup()
    {
        var result = _groups.CreateGroup(NewGroupName);
        GroupError = result.Error;
        if (result.Error is null && result.GroupId is { } groupId)
        {
            NewGroupName = string.Empty;
            OpenGroup(groupId);
        }
    }

    [RelayCommand]
    private void OpenGroup(string groupId)
    {
        Section = AppSection.Chats;
        Chat.OpenGroup(groupId);
        Refresh();
    }

    [RelayCommand]
    private void AcceptInvite(GroupInviteItem invite) => GroupError = _groups.RespondToInvite(invite.GroupId, invite.OwnerId, accept: true).Error;

    [RelayCommand]
    private void DeclineInvite(GroupInviteItem invite) => GroupError = _groups.RespondToInvite(invite.GroupId, invite.OwnerId, accept: false).Error;

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

    /// <summary>Whether the user can already see what a notification would announce.</summary>
    public bool IsLookingAt(NotificationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return IsWindowActive && Section == AppSection.Chats && !Pairing.IsVisible
            && (target.PeerId is { } peerId ? Chat.PeerId == peerId : target.GroupId is { } groupId && Chat.GroupId == groupId);
    }

    /// <summary>Goes to what a notification was about.</summary>
    public void Open(NotificationTarget? target)
    {
        if (target is null)
        {
            return;
        }

        Welcome.Dismiss();
        if (target.ShowTransfers)
        {
            Section = AppSection.Transfers;
        }
        else if (target.PeerId is { } peerId)
        {
            OpenChat(peerId);
        }
        else if (target.GroupId is { } groupId)
        {
            OpenGroup(groupId);
        }
        else
        {
            Section = AppSection.Chats;
        }
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
            Chat.Update(info.IsConnected ? "Connected" : "Offline. Messages will be sent when this device is back.");
        }
        else if (Chat.GroupId is not null)
        {
            Chat.Update(string.Empty);
        }

        IsChatVisible = Chat.IsOpen;
        IsWelcomeVisible = !Chat.IsOpen;

        var unread = ReadUnreadCounts();
        var conversations = new List<ConversationItem>();
        foreach (var peer in trusted.Values)
        {
            conversations.Add(ToConversation(peer, ConnectionOf(peer.DeviceId).IsConnected, unread.GetValueOrDefault(peer.DeviceId)));
        }

        var groupUnread = RefreshGroups(conversations);
        conversations.Sort((a, b) =>
        {
            var byTime = b.LastActivity.CompareTo(a.LastActivity);
            return byTime != 0 ? byTime : StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name);
        });
        SyncInPlace(Conversations, conversations);
        HasNoConversations = conversations.Count == 0;

        TotalUnread = unread.Values.Sum() + groupUnread;
        WindowTitle = TotalUnread > 0 ? $"({TotalUnread}) {Title}" : Title;

        var devices = new List<NearbyDevice>();
        foreach (var peer in peers)
        {
            var address = peer.Addresses.Count > 1
                ? $"{peer.Addresses[0]} (+{peer.Addresses.Count - 1})"
                : peer.Addresses.Count == 1 ? peer.Addresses[0].ToString() : string.Empty;

            // Announcements are unauthenticated, so anyone can send one claiming a paired device's ID and any name.
            var isTrusted = trusted.TryGetValue(peer.DeviceId, out var pinned);
            devices.Add(ToItem(isTrusted ? pinned!.Username : peer.Username, peer.DisplayId, address, $"Roomie {peer.AppVersion}",
                ConnectionOf(peer.DeviceId), isTrusted, blocked.Contains(peer.DeviceId), pairingBusy));
        }

        // Connected devices whose announcements stopped reaching us, then paired devices that are offline.
        var listed = peers.Select(p => p.DeviceId).ToHashSet();
        foreach (var connection in connections.Values.Where(c => c.IsConnected && !listed.Contains(c.DeviceId)))
        {
            listed.Add(connection.DeviceId);
            var name = trusted.TryGetValue(connection.DeviceId, out var known) ? known.Username : "(not announcing)";
            devices.Add(ToItem(name, DeviceIdFormat.ToDisplayId(connection.DeviceId),
                connection.RemoteEndPoint?.Address.ToString() ?? string.Empty, string.Empty, connection,
                trusted.ContainsKey(connection.DeviceId), isBlocked: false, pairingBusy));
        }

        foreach (var peer in trusted.Values.Where(p => !listed.Contains(p.DeviceId)))
        {
            devices.Add(ToItem(peer.Username, DeviceIdFormat.ToDisplayId(peer.DeviceId), "offline", string.Empty,
                ConnectionOf(peer.DeviceId), isTrusted: true, isBlocked: false, pairingBusy));
        }

        // Only a paired device can have sent a picture, and only then is its name more than a claim.
        SyncInPlace(NearbyDevices, devices.Select(d => d with
        {
            CanChat = d.CanForget,
            Avatar = d.CanForget ? _avatars.For(d.DeviceId, d.Username) : AvatarCatalog.Letter(d.Username),
        }).ToList());
        Welcome.Update(NearbyDevices, trusted.Values);

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

        var online = connections.Values.Count(c => c.IsConnected);
        OnlineSummary = online switch
        {
            0 => "No devices connected",
            1 => "1 device connected",
            _ => $"{online} devices connected",
        };

        var problems = status.Problems.Concat(_connections.Problems).ToList();
        DiscoveryProblem = problems.Count == 0 ? null : string.Join("\n", problems);

        EmptyListHint = NearbyDevices.Count > 0 ? string.Empty
            : status.State == DiscoveryState.Running
                ? "No devices found yet. Roomie must be running on the other computer, on the same network. "
                  + "If it is, check that Windows Firewall allows Roomie on private networks and that both "
                  + "networks are set to Private."
                : "No devices found.";

        RefreshTransfers();
    }

    /// <summary>Called a few times a second while data moves, so it reads only what it shows.</summary>
    private void RefreshTransfers()
    {
        IReadOnlyList<StoredTransfer> transfers;
        try
        {
            transfers = _files.GetRecentTransfers(MaxShownTransfers);
        }
        catch (MessageStoreException)
        {
            return;
        }

        FileTransferItem.Sync(AllTransfers, transfers, _files, peerId => _trust.FindTrusted(peerId)?.Username ?? DeviceIdFormat.ToDisplayId(peerId));
        HasTransfers = AllTransfers.Count > 0;
        TransfersNeedingAttention = AllTransfers.Count(t => t.CanAnswer || t.IsActive);

        var moving = AllTransfers.Where(t => t.IsActive).ToList();
        TaskbarProgress = moving.Count > 0 ? moving.Average(t => t.Percent) / 100 : null;
    }

    private ConversationItem ToConversation(TrustedPeer peer, bool isOnline, int unread)
    {
        StoredMessage? last = null;
        try
        {
            last = LastOf(_messages.GetConversation(peer.DeviceId, 1));
        }
        catch (MessageStoreException)
        {
            // The list still works without a preview. The store logged the failure.
        }

        return new ConversationItem(peer.DeviceId, null, peer.Username, PreviewOf(last, null) ?? "No messages yet",
            TimeOf(last), unread, isOnline, Chat.PeerId == peer.DeviceId, last?.Timestamp ?? DateTimeOffset.MinValue, _avatars.For(peer.DeviceId, peer.Username));
    }

    /// <returns>The unread messages across all groups.</returns>
    private int RefreshGroups(List<ConversationItem> conversations)
    {
        IReadOnlyList<StoredGroup> groups;
        IReadOnlyList<StoredInvite> invites;
        IReadOnlyDictionary<string, int> unread;
        try
        {
            groups = _groups.GetGroups();
            invites = _groups.GetInvites();
            unread = _messages.GetGroupUnreadCounts();
        }
        catch (MessageStoreException)
        {
            // The rest of the window still works. The store logged the failure.
            return 0;
        }

        foreach (var group in groups)
        {
            StoredMessage? last = null;
            try
            {
                last = LastOf(_messages.GetGroupConversation(group.State.GroupId, 1));
            }
            catch (MessageStoreException)
            {
                // As above.
            }

            // Once a group has ended, that matters more than its last message.
            var details = group.IsMember
                ? PreviewOf(last, group.State) ?? $"{group.State.Members.Count} {(group.State.Members.Count == 1 ? "member" : "members")}"
                : group.IsDeleted ? "Group deleted"
                : group.HasLeft ? "You left"
                : "You were removed";
            conversations.Add(new ConversationItem(null, group.State.GroupId, group.State.Name, details,
                TimeOf(last), unread.GetValueOrDefault(group.State.GroupId), false, Chat.GroupId == group.State.GroupId,
                last?.Timestamp ?? DateTimeOffset.MinValue, AvatarCatalog.Letter(group.State.Name)));
        }

        var open = new List<GroupInviteItem>();
        foreach (var invite in invites.Where(i => i is { Direction: InviteDirection.Incoming, Response: InviteResponse.None, SignedDocument: not null }))
        {
            if (GroupProtocol.TryReadState(invite.SignedDocument!, out var state))
            {
                var from = _trust.FindTrusted(invite.PeerId)?.Username ?? DeviceIdFormat.ToDisplayId(invite.PeerId);
                open.Add(new GroupInviteItem(invite.GroupId, invite.PeerId, state.Name, $"{from} invited you. {state.Members.Count} in the group."));
            }
        }

        SyncInPlace(GroupInvites, open);
        return unread.Values.Sum();
    }

    private static StoredMessage? LastOf(IReadOnlyList<StoredMessage> messages) => messages.Count > 0 ? messages[^1] : null;

    private string? PreviewOf(StoredMessage? message, GroupState? group)
    {
        if (message is null)
        {
            return null;
        }

        var text = NotificationText.Clean(message.Content, PreviewLength);
        if (message.Direction == MessageDirection.Outgoing)
        {
            return $"You: {text}";
        }

        var sender = group is null ? null : _trust.FindTrusted(message.PeerId)?.Username ?? group.Members.FirstOrDefault(m => m.DeviceId == message.PeerId)?.Username;
        return sender is null ? text : $"{sender}: {text}";
    }

    private static string TimeOf(StoredMessage? message)
    {
        if (message is null)
        {
            return string.Empty;
        }

        var local = message.Timestamp.ToLocalTime();
        return local.Date == DateTimeOffset.Now.Date
            ? local.ToString("t", CultureInfo.CurrentCulture)
            : local.ToString("d", CultureInfo.CurrentCulture);
    }

    private Dictionary<Guid, int> ReadUnreadCounts()
    {
        try
        {
            var counts = _messages.GetActiveConversations().ToDictionary(c => c.PeerId, c => c.UnreadCount);

            // A file waiting for an answer needs the user's attention just as a message does.
            foreach (var offer in _files.GetOpenTransfers().Where(t => t is { Direction: MessageDirection.Incoming, State: FileTransferState.Offered }))
            {
                counts[offer.PeerId] = counts.GetValueOrDefault(offer.PeerId) + 1;
            }

            return counts;
        }
        catch (MessageStoreException)
        {
            // The device list still works without the counts. The store logged the failure.
            return [];
        }
    }

    // Changes only what differs, so buttons not affected stay under the mouse and the scroll position stays put.
    private static void SyncInPlace<T>(ObservableCollection<T> items, List<T> wanted)
    {
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i >= items.Count)
            {
                items.Add(wanted[i]);
            }
            else if (!EqualityComparer<T>.Default.Equals(items[i], wanted[i]))
            {
                items[i] = wanted[i];
            }
        }

        while (items.Count > wanted.Count)
        {
            items.RemoveAt(items.Count - 1);
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
