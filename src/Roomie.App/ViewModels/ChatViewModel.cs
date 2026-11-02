using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roomie.Core.Identity;
using Roomie.Core.Messaging;
using Roomie.Core.Trust;
using Roomie.Network.Messaging;

namespace Roomie.App.ViewModels;

/// <summary>One message as shown in the conversation.</summary>
public sealed record ChatBubble(Guid MessageId, string Text, bool IsMine, string Time, string Status, bool IsProblem);

/// <summary>The open conversation. The whole list is rebuilt from the store on every change.</summary>
public sealed partial class ChatViewModel(MessagingService messaging, IMessageStore store, ITrustStore trust) : ObservableObject
{
    public ObservableCollection<ChatBubble> Messages { get; } = [];

    public Guid? PeerId { get; private set; }

    /// <summary>The window is in front, so an open conversation is really being read.</summary>
    public bool IsWindowActive { get; set; } = true;

    [ObservableProperty]
    private bool _isOpen;

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
    private bool _canCompose;

    public void Open(Guid peerId)
    {
        if (PeerId != peerId)
        {
            Draft = string.Empty;
            SendError = null;
            Messages.Clear();
        }

        PeerId = peerId;
        IsOpen = true;
    }

    public void Update(string connection)
    {
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

        Sync(store.GetConversation(peerId).Select(ToBubble).ToList());
    }

    private bool CanSend() => CanCompose && !string.IsNullOrWhiteSpace(Draft);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private void Send()
    {
        if (PeerId is not { } peerId)
        {
            return;
        }

        var result = messaging.SendText(peerId, Draft);
        SendError = result.Error;
        if (result.Error is null)
        {
            Draft = string.Empty;
        }
    }

    [RelayCommand]
    private void Close()
    {
        PeerId = null;
        IsOpen = false;
        Messages.Clear();
    }

    // Changes only what differs, so the scroll position and any selected text survive an update.
    private void Sync(List<ChatBubble> wanted)
    {
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

        return new ChatBubble(
            message.MessageId,
            message.Content,
            mine,
            message.Timestamp.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
            status,
            message.State == DeliveryState.Failed);
    }
}
