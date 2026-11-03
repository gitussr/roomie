using Roomie.App.ViewModels;
using Roomie.Core.Configuration;
using Roomie.Core.Files;
using Roomie.Core.Groups;
using Roomie.Core.Identity;
using Roomie.Core.Messaging;
using Roomie.Core.Trust;
using Roomie.Infrastructure.Storage;
using Roomie.Network.Files;
using Roomie.Network.Groups;
using Roomie.Network.Messaging;
using Roomie.Network.Pairing;

namespace Roomie.App.Services;

/// <summary>Decides what deserves a notification and words it. Events arrive on background threads.</summary>
public sealed class NotificationCenter
{
    /// <summary>A burst of messages from one device gives one notification, because Windows would queue them all.</summary>
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(5);

    private readonly MessagingService _messaging;
    private readonly FileTransferService _files;
    private readonly GroupService _groups;
    private readonly ITrustStore _trust;
    private readonly JsonSettingsStore _settings;
    private readonly MainViewModel _main;
    private readonly TransferWatcher _watcher = new();
    private readonly SynchronizationContext _ui;
    private readonly Dictionary<string, DateTimeOffset> _lastShown = [];
    private readonly HashSet<string> _knownInvites = [];
    private readonly Lock _gate = new();

    public NotificationCenter(
        MessagingService messaging,
        FileTransferService files,
        GroupService groups,
        PairingService pairing,
        ITrustStore trust,
        JsonSettingsStore settings,
        MainViewModel main)
    {
        _messaging = messaging;
        _files = files;
        _groups = groups;
        _trust = trust;
        _settings = settings;
        _main = main;
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();

        try
        {
            _watcher.Start(files.GetOpenTransfers());
            foreach (var invite in groups.GetInvites().Where(IsOpenInvite))
            {
                _knownInvites.Add(InviteKey(invite));
            }
        }
        catch (MessageStoreException)
        {
            // Whatever is already open gets announced later. The store logged the failure.
        }

        messaging.IncomingMessage += (_, message) => OnMessage(message);
        files.Changed += (_, _) => OnTransfersChanged();
        groups.IncomingInvite += (_, _) => OnInvite();
        pairing.IncomingRequest += (_, info) => OnPairing(info);
    }

    /// <summary>Raised on the UI thread when something should be shown.</summary>
    public event EventHandler<(string Title, string Body, NotificationTarget? Target)>? Notify;

    /// <summary>Raised on the UI thread for each new message the user isn't looking at, so the taskbar can flash.</summary>
    public event EventHandler? AttentionNeeded;

    private void OnMessage(StoredMessage message)
    {
        _ui.Post(_ =>
        {
            var target = new NotificationTarget(message.GroupId is null ? message.PeerId : null, message.GroupId);
            if (_main.IsLookingAt(target))
            {
                return;
            }

            AttentionNeeded?.Invoke(this, EventArgs.Empty);
            if (!IsAllowed(message.GroupId ?? message.PeerId.ToString()))
            {
                return;
            }

            var sender = NameOf(message.PeerId);
            var title = message.GroupId is { } groupId ? $"{sender} in {GroupName(groupId)}" : sender;
            Show(title, _settings.Current.ShowMessageText ? message.Content : "New message", target);
        }, null);
    }

    private void OnTransfersChanged()
    {
        IReadOnlyList<(TransferNews News, StoredTransfer Transfer)> news;
        try
        {
            news = _watcher.Update(_files.GetOpenTransfers(), _files.GetTransfer);
        }
        catch (MessageStoreException)
        {
            return;
        }

        foreach (var (kind, transfer) in news)
        {
            var name = NameOf(transfer.PeerId);
            var outgoing = transfer.Direction == MessageDirection.Outgoing;
            (string Title, string Body)? text = kind switch
            {
                TransferNews.Offered => ($"{name} wants to send you a file", transfer.Filename),
                TransferNews.Completed => (outgoing ? $"{name} received your file" : $"File received from {name}", transfer.Filename),
                TransferNews.Failed => ($"File transfer failed with {name}", transfer.Filename),
                TransferNews.Declined => ($"{name} declined your file", transfer.Filename),
                TransferNews.Canceled => ($"File transfer with {name} was canceled", transfer.Filename),
                _ => null,
            };

            if (text is { } shown)
            {
                _ui.Post(_ => Show(shown.Title, shown.Body, new NotificationTarget(ShowTransfers: true)), null);
            }
        }
    }

    private void OnInvite()
    {
        IReadOnlyList<StoredInvite> invites;
        try
        {
            invites = _groups.GetInvites();
        }
        catch (MessageStoreException)
        {
            return;
        }

        foreach (var invite in invites.Where(IsOpenInvite))
        {
            bool isNew;
            lock (_gate)
            {
                isNew = _knownInvites.Add(InviteKey(invite));
            }

            if (isNew && GroupProtocol.TryReadState(invite.SignedDocument!, out var state))
            {
                var title = $"{NameOf(invite.PeerId)} invited you to a group";
                _ui.Post(_ => Show(title, state.Name, new NotificationTarget(GroupId: null)), null);
            }
        }
    }

    private void OnPairing(PairingInfo info)
    {
        // A pairing has no verified name yet: anyone on the network can call themselves anything.
        var name = info.PeerName;
        _ui.Post(_ => Show("A device wants to pair", $"It calls itself {name}. Only pair if you expected this.", null), null);
    }

    private void Show(string title, string body, NotificationTarget? target)
    {
        if (_settings.Current.Notifications && !_main.IsWindowActive)
        {
            Notify?.Invoke(this, (title, body, target));
        }
    }

    private bool IsAllowed(string key)
    {
        var now = DateTimeOffset.UtcNow;
        if (_lastShown.TryGetValue(key, out var last) && now - last < QuietPeriod)
        {
            return false;
        }

        _lastShown[key] = now;
        return true;
    }

    private string NameOf(Guid peerId) => _trust.FindTrusted(peerId)?.Username ?? DeviceIdFormat.ToDisplayId(peerId);

    private string GroupName(string groupId)
    {
        try
        {
            return _groups.GetGroup(groupId)?.State.Name ?? "a group";
        }
        catch (MessageStoreException)
        {
            return "a group";
        }
    }

    private static bool IsOpenInvite(StoredInvite invite) =>
        invite is { Direction: InviteDirection.Incoming, Response: InviteResponse.None, SignedDocument: not null };

    private static string InviteKey(StoredInvite invite) => $"{invite.GroupId}|{invite.PeerId}";
}
