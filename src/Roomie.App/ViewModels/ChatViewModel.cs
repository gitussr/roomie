using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Roomie.Core.Files;
using Roomie.Core.Groups;
using Roomie.Core.Identity;
using Roomie.Core.Messaging;
using Roomie.Core.Trust;
using Roomie.Network.Files;
using Roomie.Network.Groups;
using Roomie.Network.Messaging;

namespace Roomie.App.ViewModels;

/// <summary>One message as shown in the conversation.</summary>
public sealed record ChatBubble(Guid MessageId, string Text, bool IsMine, string Time, string Status, bool IsProblem, string? Sender = null);

/// <summary>A member of the open group.</summary>
public sealed record GroupMemberItem(Guid DeviceId, string Name, string Status, bool CanRemove);

/// <summary>A paired device the owner of the open group could invite.</summary>
public sealed record GroupInviteCandidate(Guid DeviceId, string Name);

/// <summary>The open conversation, with one device or a group. The whole list is rebuilt from the store on every change.</summary>
public sealed partial class ChatViewModel(MessagingService messaging, GroupService groups, FileTransferService files, IMessageStore store, ITrustStore trust, ILocalIdentityProvider identity) : ObservableObject
{
    /// <summary>The panel creates a control per message, so a long history can't all be shown until it can page.</summary>
    public const int MaxShownMessages = 500;

    private const int MaxShownFiles = 50;

    /// <summary>A file action changed something, so other lists of files should look again.</summary>
    public event EventHandler? FilesChanged;

    /// <summary>Files with this device, newest first.</summary>
    public ObservableCollection<FileTransferItem> Files { get; } = [];

    [ObservableProperty]
    private bool _hasFiles;

    [ObservableProperty]
    private string _filesTitle = "Files";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendFileCommand))]
    private bool _isPreparingFile;

    public ObservableCollection<ChatBubble> Messages { get; } = [];

    public ObservableCollection<GroupMemberItem> Members { get; } = [];

    public ObservableCollection<GroupInviteCandidate> InviteCandidates { get; } = [];

    [ObservableProperty]
    private bool _hasOlderMessages;

    public Guid? PeerId { get; private set; }

    public string? GroupId { get; private set; }

    /// <summary>The window is in front, so an open conversation is really being read.</summary>
    public bool IsWindowActive { get; set; }

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirect))]
    [NotifyCanExecuteChangedFor(nameof(SendFileCommand))]
    private bool _isGroup;

    /// <summary>Files go to one device at a time.</summary>
    public bool IsDirect => !IsGroup;

    [ObservableProperty]
    private string _membersTitle = string.Empty;

    [ObservableProperty]
    private string _peerName = string.Empty;

    [ObservableProperty]
    private string _peerDisplayId = string.Empty;

    [ObservableProperty]
    private string _connection = string.Empty;

    [ObservableProperty]
    private string? _problem;

    [ObservableProperty]
    private string? _sendError;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _draft = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendFileCommand))]
    private bool _canCompose;

    public void Open(Guid peerId)
    {
        if (PeerId != peerId)
        {
            Reset();
        }

        PeerId = peerId;
        GroupId = null;
        IsGroup = false;
        IsOpen = true;
    }

    public void OpenGroup(string groupId)
    {
        if (GroupId != groupId)
        {
            Reset();
        }

        PeerId = null;
        GroupId = groupId;
        IsGroup = true;
        IsOpen = true;
    }

    public void Update(string connection)
    {
        if (GroupId is { } groupId)
        {
            UpdateGroup(groupId);
            return;
        }

        if (PeerId is not { } peerId)
        {
            return;
        }

        if (IsWindowActive)
        {
            messaging.MarkRead(peerId);
        }

        var peer = trust.FindTrusted(peerId);
        PeerName = peer?.Username ?? DeviceIdFormat.ToDisplayId(peerId);
        PeerDisplayId = DeviceIdFormat.ToDisplayId(peerId);
        Connection = connection;
        CanCompose = peer is not null && !trust.IsBlocked(peerId);
        Problem = CanCompose ? messaging.GetDeliveryProblem(peerId) : "This device isn't paired, so you can't send it messages.";

        ReadMessages(() => store.GetConversation(peerId, MaxShownMessages + 1), ToBubble);
        RefreshFiles();
    }

    /// <summary>Called for each stage and a few times a second while data moves, so it does nothing else.</summary>
    public void RefreshFiles()
    {
        if (PeerId is not { } peerId)
        {
            return;
        }

        IReadOnlyList<StoredTransfer> transfers;
        try
        {
            transfers = files.GetTransfers(peerId, MaxShownFiles);
        }
        catch (MessageStoreException)
        {
            return;
        }

        FileTransferItem.Sync(Files, transfers, files);
        HasFiles = Files.Count > 0;
        var waiting = Files.Count(f => f.CanAnswer);
        FilesTitle = waiting > 0 ? $"Files ({waiting} waiting for your answer)" : "Files";
    }

    private bool CanSendFile() => CanCompose && !IsGroup && !IsPreparingFile;

    [RelayCommand(CanExecute = nameof(CanSendFile))]
    private async Task SendFileAsync()
    {
        if (PeerId is not { } peerId)
        {
            return;
        }

        var dialog = new OpenFileDialog { Title = "Choose files to send", CheckFileExists = true, Multiselect = true };
        if (dialog.ShowDialog() == true)
        {
            await SendPathsAsync(peerId, dialog.FileNames);
        }
    }

    /// <summary>Sends files dropped on the conversation or pasted into it.</summary>
    public async Task SendPathsAsync(IReadOnlyCollection<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (IsGroup)
        {
            SendError = "Files go to one device at a time, not to a group. Open a chat with the device instead.";
        }
        else if (PeerId is not { } peerId || !CanCompose)
        {
            SendError = "Pair with this device before sending it files.";
        }
        else
        {
            await SendPathsAsync(peerId, paths);
        }
    }

    private async Task SendPathsAsync(Guid peerId, IReadOnlyCollection<string> paths)
    {
        var folders = paths.Where(Directory.Exists).ToList();
        foreach (var path in paths.Except(folders))
        {
            await StartSendingAsync(peerId, path);
            if (SendError is not null)
            {
                return;
            }
        }

        if (folders.Count > 0)
        {
            SendError = $"Roomie sends files, not folders. Zip \"{Path.GetFileName(folders[0])}\" first, then send the zip.";
        }
    }

    private async Task StartSendingAsync(Guid peerId, string path)
    {
        IsPreparingFile = true;
        try
        {
            SendError = (await files.SendFileAsync(peerId, path)).Error;
            RefreshFiles();
            FilesChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsPreparingFile = false;
        }
    }

    [RelayCommand]
    private async Task AcceptFileAsync(FileTransferItem item)
    {
        SendError = (await files.AcceptAsync(item.PeerId, item.TransferId)).Error;
        RefreshFiles();
        FilesChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task DeclineFileAsync(FileTransferItem item)
    {
        SendError = (await files.DeclineAsync(item.PeerId, item.TransferId)).Error;
        RefreshFiles();
        FilesChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task CancelFileAsync(FileTransferItem item)
    {
        SendError = (await files.CancelAsync(item.PeerId, item.Direction, item.TransferId)).Error;
        RefreshFiles();
        FilesChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task SendFileAgainAsync(FileTransferItem item)
    {
        if (item.LocalPath is { } path)
        {
            await StartSendingAsync(item.PeerId, path);
        }
    }

    /// <summary>Shows the file in its folder. Roomie never opens a received file itself.</summary>
    [RelayCommand]
    private void ShowFile(FileTransferItem item)
    {
        if (item.LocalPath is not { } path || !File.Exists(path))
        {
            SendError = "That file isn't there any more. It may have been moved or deleted.";
            return;
        }

        // A received name can't contain a quote: SafeFilename replaces it.
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false })?.Dispose();
    }

    private void UpdateGroup(string groupId)
    {
        StoredGroup? group;
        IReadOnlyList<StoredInvite> invites;
        try
        {
            group = groups.GetGroup(groupId);
            invites = groups.GetInvites();
        }
        catch (MessageStoreException)
        {
            Problem = "Roomie can't read your saved messages right now.";
            return;
        }

        if (group is null)
        {
            return;
        }

        if (IsWindowActive)
        {
            messaging.MarkGroupRead(groupId);
        }

        var self = identity.Identity.DeviceId;
        var state = group.State;
        var isOwner = state.OwnerId == self && group.IsMember;
        PeerName = state.Name;
        PeerDisplayId = "Group";
        Connection = group.IsMember
            ? $"{state.Members.Count} {(state.Members.Count == 1 ? "member" : "members")} · owner {NameOf(state, state.OwnerId)}"
            : "You're no longer in this group";
        CanCompose = group.IsMember;
        MembersTitle = $"Members ({state.Members.Count})";

        var unpaired = state.Members
            .Where(m => m.DeviceId != self && trust.FindTrusted(m.DeviceId) is null)
            .Select(m => m.Username)
            .ToList();
        Problem = !group.IsMember
            ? "You were removed from this group. Its history stays on this computer, but you can't send to it."
            : unpaired.Count > 0
                ? $"You aren't paired with {string.Join(", ", unpaired)}. They won't get your messages until you pair with them."
                : null;

        var invited = invites
            .Where(i => i.GroupId == groupId && i.Direction == InviteDirection.Outgoing)
            .Select(i => i.PeerId)
            .ToHashSet();

        var members = state.Members
            .Select(m => new GroupMemberItem(m.DeviceId, m.Username, MemberStatus(state, m.DeviceId), isOwner && m.DeviceId != self))
            .Concat(invited.Where(id => !state.HasMember(id)).Select(id => new GroupMemberItem(
                id, trust.FindTrusted(id)?.Username ?? DeviceIdFormat.ToDisplayId(id), "Invited, not answered yet", isOwner)))
            .ToList();
        SyncAll(Members, members);

        var candidates = isOwner && state.Members.Count < GroupProtocol.MaxMembers
            ? trust.TrustedPeers
                .Where(p => !state.HasMember(p.DeviceId) && !invited.Contains(p.DeviceId) && !trust.IsBlocked(p.DeviceId))
                .Select(p => new GroupInviteCandidate(p.DeviceId, p.Username))
                .ToList()
            : [];
        SyncAll(InviteCandidates, candidates);

        ReadMessages(() => store.GetGroupConversation(groupId, MaxShownMessages + 1), m => ToGroupBubble(m, group));
    }

    private bool CanSend() => CanCompose && !string.IsNullOrWhiteSpace(Draft);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private void Send()
    {
        MessageSendResult result;
        if (GroupId is { } groupId)
        {
            result = messaging.SendGroupText(groupId, Draft);
        }
        else if (PeerId is { } peerId)
        {
            result = messaging.SendText(peerId, Draft);
        }
        else
        {
            return;
        }

        SendError = result.Error;
        if (result.Error is null)
        {
            Draft = string.Empty;
        }
    }

    [RelayCommand]
    private void Invite(Guid deviceId)
    {
        if (GroupId is { } groupId)
        {
            SendError = groups.Invite(groupId, deviceId).Error;
        }
    }

    [RelayCommand]
    private void RemoveMember(Guid deviceId)
    {
        if (GroupId is { } groupId)
        {
            SendError = groups.Remove(groupId, deviceId).Error;
        }
    }

    [RelayCommand]
    private void Close()
    {
        Reset();
        PeerId = null;
        GroupId = null;
        IsGroup = false;
        IsOpen = false;
    }

    private void Reset()
    {
        Draft = string.Empty;
        SendError = null;
        Messages.Clear();
        Members.Clear();
        InviteCandidates.Clear();
        Files.Clear();
        HasFiles = false;
    }

    private void ReadMessages(Func<IReadOnlyList<StoredMessage>> read, Func<StoredMessage, ChatBubble> toBubble)
    {
        try
        {
            // One more than is shown, to tell whether there are older messages.
            var messages = read();
            HasOlderMessages = messages.Count > MaxShownMessages;
            Sync(messages.Skip(messages.Count - Math.Min(messages.Count, MaxShownMessages)).Select(toBubble).ToList());
        }
        catch (MessageStoreException)
        {
            // What's on screen stays. The store logged the failure.
            Problem = "Roomie can't read your saved messages right now.";
        }
    }

    private string MemberStatus(GroupState state, Guid deviceId)
    {
        if (deviceId == identity.Identity.DeviceId)
        {
            return deviceId == state.OwnerId ? "You, owner" : "You";
        }

        var role = deviceId == state.OwnerId ? "Owner" : "Member";
        return trust.FindTrusted(deviceId) is null ? $"{role}, not paired with this computer" : role;
    }

    private string NameOf(GroupState state, Guid deviceId) =>
        deviceId == identity.Identity.DeviceId ? "you"
        : state.Members.FirstOrDefault(m => m.DeviceId == deviceId)?.Username
          ?? trust.FindTrusted(deviceId)?.Username
          ?? DeviceIdFormat.ToDisplayId(deviceId);

    // Changes only what differs, so the scroll position and any selected text survive an update.
    private void Sync(List<ChatBubble> wanted)
    {
        // A long conversation drops its oldest message as a new one arrives. Removing it here keeps the
        // new one an addition, which is what scrolls the panel down.
        var wantedIds = wanted.Select(m => m.MessageId).ToHashSet();
        while (Messages.Count > 0 && !wantedIds.Contains(Messages[0].MessageId))
        {
            Messages.RemoveAt(0);
        }

        for (var i = 0; i < wanted.Count; i++)
        {
            if (i >= Messages.Count)
            {
                Messages.Add(wanted[i]);
            }
            else if (Messages[i] != wanted[i])
            {
                Messages[i] = wanted[i];
            }
        }

        while (Messages.Count > wanted.Count)
        {
            Messages.RemoveAt(Messages.Count - 1);
        }
    }

    private static void SyncAll<T>(ObservableCollection<T> items, List<T> wanted)
    {
        if (!items.SequenceEqual(wanted))
        {
            items.Clear();
            foreach (var item in wanted)
            {
                items.Add(item);
            }
        }
    }

    private static string FormatTime(StoredMessage message) =>
        message.Timestamp.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    private ChatBubble ToGroupBubble(StoredMessage message, StoredGroup group)
    {
        if (message.Direction == MessageDirection.Incoming)
        {
            var updating = message.MembershipVersion > group.Version ? "Membership updating" : string.Empty;
            return new ChatBubble(message.MessageId, message.Content, false, FormatTime(message), updating, false, NameOf(group.State, message.PeerId));
        }

        var recipients = message.Recipients ?? [];
        var delivered = recipients.Count(r => r.State is DeliveryState.Delivered or DeliveryState.Read);
        var read = recipients.Count(r => r.State == DeliveryState.Read);
        var failed = recipients.Where(r => r.State == DeliveryState.Failed).ToList();
        var waiting = recipients.Where(r => r.State is DeliveryState.Pending or DeliveryState.Sent).ToList();

        var parts = new List<string>();
        if (recipients.Count == 0)
        {
            parts.Add("No other members yet");
        }
        else
        {
            parts.Add($"Delivered to {delivered} of {recipients.Count}");
        }

        if (read > 0)
        {
            parts.Add($"read by {read}");
        }

        if (waiting.Count > 0)
        {
            parts.Add($"waiting for {string.Join(", ", waiting.Select(r => NameOf(group.State, r.DeviceId)))}");
        }

        parts.AddRange(failed.Select(r => $"not delivered to {NameOf(group.State, r.DeviceId)}: {r.Failure ?? "refused"}"));

        return new ChatBubble(message.MessageId, message.Content, true, FormatTime(message), string.Join(" · ", parts), failed.Count > 0);
    }

    private static ChatBubble ToBubble(StoredMessage message)
    {
        var mine = message.Direction == MessageDirection.Outgoing;
        var status = !mine ? string.Empty : message.State switch
        {
            DeliveryState.Pending => "Waiting to send",
            DeliveryState.Sent => "Sent",
            DeliveryState.Delivered => "Delivered",
            DeliveryState.Read => "Read",
            DeliveryState.Failed => message.Failure ?? "Not delivered",
            _ => string.Empty,
        };

        return new ChatBubble(message.MessageId, message.Content, mine, FormatTime(message), status, message.State == DeliveryState.Failed);
    }
}
