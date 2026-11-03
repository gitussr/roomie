using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Roomie.Core.Identity;
using Roomie.Core.Messaging;
using Roomie.Core.Protocol;
using Roomie.Core.Trust;
using Roomie.Network.Connections;
using Roomie.Network.Groups;

namespace Roomie.Network.Messaging;

public sealed class MessagingOptions
{
    /// <summary>A message sent this long ago without an acknowledgement is sent again on the same session.</summary>
    public TimeSpan AckTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How often to look for messages that are due to be sent again.</summary>
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Unacknowledged messages allowed per device. The rest wait, so a refusing peer can't be flooded.</summary>
    public int MaxInFlight { get; init; } = 16;

    /// <summary>A write that takes longer means the connection is stuck, and the session is closed.</summary>
    public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>Null <see cref="Error"/> means the message was saved and will be delivered when possible.</summary>
public sealed record MessageSendResult(StoredMessage? Message, string? Error);

/// <summary>
/// One-to-one text messaging (architecture.md §6): at-least-once transmission with effectively-once storage.
/// A message is stored before anything is sent. The recipient stores it before acknowledging, and recognizes a
/// retry by its ID. One worker does all the sending, so messages to a device leave in order.
/// </summary>
public sealed partial class MessagingService : BackgroundService
{
    private readonly ConnectionManager _connections;
    private readonly GroupService _groups;
    private readonly IMessageStore _store;
    private readonly ITrustStore _trust;
    private readonly ILocalIdentityProvider _identity;
    private readonly MessagingOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<MessagingService> _logger;
    private readonly IDisposable _registration;
    private readonly Channel<Guid> _work = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });

    private readonly Lock _gate = new();
    private readonly Dictionary<(Guid MessageId, Guid PeerId), Attempt> _attempts = [];
    private readonly Dictionary<Guid, Hold> _holds = [];

    public MessagingService(
        ConnectionManager connections,
        IMessageStore store,
        ITrustStore trust,
        ILocalIdentityProvider identity,
        GroupService groups,
        MessagingOptions options,
        TimeProvider time,
        ILogger<MessagingService> logger)
    {
        _connections = connections;
        _groups = groups;
        _store = store;
        _trust = trust;
        _identity = identity;
        _options = options;
        _time = time;
        _logger = logger;

        _registration = connections.Dispatcher.Register(
            [RmpMessageTypes.ChatMessage, RmpMessageTypes.MessageAck, RmpMessageTypes.MessageRead, RmpMessageTypes.Error],
            HandleMessageAsync);
        connections.SessionOpened += OnSessionOpened;
        trust.Changed += OnTrustChanged;
        groups.WorkQueued += OnGroupWork;
    }

    /// <summary>Raised on a background thread when a conversation changes. The argument is the device.</summary>
    public event EventHandler<Guid>? Changed;

    /// <summary>Raised once for each new message from another device, after it is saved. A repeat doesn't raise it.</summary>
    public event EventHandler<StoredMessage>? IncomingMessage;

    /// <summary>
    /// Saves a message and queues it for delivery. A device that is offline gets it when it reconnects.
    /// </summary>
    public MessageSendResult SendText(Guid peerId, string text)
    {
        if (_trust.FindTrusted(peerId) is null || _trust.IsBlocked(peerId))
        {
            return new MessageSendResult(null, "Pair with this device before sending it messages.");
        }

        var content = ContentPayloads.NormalizeText(text);
        var check = ContentPayloads.CheckChatMessage(ToPayload(peerId, content));
        if (check != RmpPayloadCheck.Valid)
        {
            return new MessageSendResult(null, check == RmpPayloadCheck.TooLarge
                ? $"This message is too long. The limit is {RmpContentLimits.MaxTextBodyBytes / 1024} KB."
                : "Type a message first.");
        }

        // Whole milliseconds, like the wire and the store, so every copy of this message has the same timestamp.
        var now = DateTimeOffset.FromUnixTimeMilliseconds(_time.GetUtcNow().ToUnixTimeMilliseconds());
        var message = new StoredMessage(Guid.CreateVersion7(now), peerId, MessageDirection.Outgoing, content, now, DeliveryState.Pending);
        try
        {
            _store.AddOutgoing(message);
        }
        catch (MessageStoreException)
        {
            return new MessageSendResult(null, "Roomie couldn't save this message, so it wasn't sent. Check that the disk has free space, then try again.");
        }

        RaiseChanged(peerId);
        Enqueue(peerId);
        return new MessageSendResult(message, null);
    }

    /// <summary>
    /// Saves a message to a group and queues one copy per member of the current membership. Members this device
    /// isn't paired with get theirs if it pairs with them later: membership never creates trust.
    /// </summary>
    public MessageSendResult SendGroupText(string groupId, string text)
    {
        StoredMessage message;
        var recipients = new List<Guid>();
        try
        {
            if (_groups.GetGroup(groupId) is not { IsMember: true } group)
            {
                return new MessageSendResult(null, "You're no longer in this group.");
            }

            var content = ContentPayloads.NormalizeText(text);
            var check = ContentPayloads.CheckChatMessage(ToGroupPayload(groupId, group.Version, content));
            if (check != RmpPayloadCheck.Valid)
            {
                return new MessageSendResult(null, check == RmpPayloadCheck.TooLarge
                    ? $"This message is too long. The limit is {RmpContentLimits.MaxTextBodyBytes / 1024} KB."
                    : "Type a message first.");
            }

            var now = DateTimeOffset.FromUnixTimeMilliseconds(_time.GetUtcNow().ToUnixTimeMilliseconds());
            message = new StoredMessage(Guid.CreateVersion7(now), Guid.Empty, MessageDirection.Outgoing, content, now, DeliveryState.Delivered, GroupId: groupId, MembershipVersion: group.Version);
            recipients.AddRange(group.State.Members.Select(m => m.DeviceId).Where(id => id != _identity.Identity.DeviceId));
            _store.AddOutgoingGroup(message, recipients);
        }
        catch (MessageStoreException)
        {
            return new MessageSendResult(null, "Roomie couldn't save this message, so it wasn't sent. Check that the disk has free space, then try again.");
        }

        RaiseChanged(Guid.Empty);
        foreach (var recipient in recipients)
        {
            Enqueue(recipient);
        }

        return new MessageSendResult(message, null);
    }

    /// <summary>The user is looking at this group: its messages count as read, and each sender is told.</summary>
    public void MarkGroupRead(string groupId)
    {
        try
        {
            var senders = _store.MarkGroupRead(groupId);
            if (senders.Count > 0)
            {
                RaiseChanged(Guid.Empty);
            }

            foreach (var sender in senders)
            {
                Enqueue(sender);
            }
        }
        catch (MessageStoreException)
        {
            // Still unread, so the next update tries again.
        }
    }

    /// <summary>The user is looking at this conversation: its messages count as read, and the sender is told.</summary>
    public void MarkRead(Guid peerId)
    {
        try
        {
            if (_store.MarkIncomingRead(peerId))
            {
                RaiseChanged(peerId);
                Enqueue(peerId);
            }
        }
        catch (MessageStoreException)
        {
            // Still unread, so the next update tries again.
        }
    }

    /// <summary>Why messages to this device are stuck even though it is connected, for display.</summary>
    public string? GetDeliveryProblem(Guid peerId)
    {
        if (!_connections.TryGetSession(peerId, out var session))
        {
            return null;
        }

        if (!session.Supports(RmpCapabilities.Text))
        {
            return "The other device runs a version of Roomie without messaging. Messages will wait until it is updated.";
        }

        lock (_gate)
        {
            return _holds.TryGetValue(peerId, out var hold) && hold.SessionId == session.SessionId ? hold.Reason : null;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield first: the host waits for this method's synchronous part before starting anything else.
        await Task.Yield();
        var sweep = SweepAsync(stoppingToken);
        try
        {
            await foreach (var peerId in _work.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await FlushAsync(peerId, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        await sweep.ConfigureAwait(false);
    }

    public override void Dispose()
    {
        _registration.Dispose();
        _connections.SessionOpened -= OnSessionOpened;
        _trust.Changed -= OnTrustChanged;
        _groups.WorkQueued -= OnGroupWork;
        base.Dispose();
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(_options.SweepInterval, _time);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                EnqueueAllWaiting();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void OnSessionOpened(object? sender, PeerSession session) => Enqueue(session.RemoteDeviceId);

    private void OnGroupWork(object? sender, Guid peerId) => Enqueue(peerId);

    private void OnTrustChanged(object? sender, EventArgs e)
    {
        // Pairing again is the one thing that can change a refusal without a new session.
        lock (_gate)
        {
            _holds.Clear();
        }

        EnqueueAllWaiting();
    }

    private void EnqueueAllWaiting()
    {
        try
        {
            foreach (var conversation in _store.GetActiveConversations().Where(c => c.HasWaitingWork))
            {
                Enqueue(conversation.PeerId);
            }
        }
        catch (MessageStoreException)
        {
            // The next sweep looks again.
        }
    }

    private void Enqueue(Guid peerId) => _work.Writer.TryWrite(peerId);

    private async Task FlushAsync(Guid peerId, CancellationToken cancellationToken)
    {
        if (!_connections.TryGetSession(peerId, out var session) || !session.IsTrusted || !session.Supports(RmpCapabilities.Text))
        {
            return;
        }

        try
        {
            // Membership first, so a device knows who is in a group before it gets a message from that group.
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(_options.WriteTimeout);
                await _groups.FlushAsync(session, timeout.Token).ConfigureAwait(false);
            }

            await SendWaitingMessagesAsync(session, cancellationToken).ConfigureAwait(false);
            await SendReadReceiptsAsync(session, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Cancelling a write can leave half a frame on the wire, so this connection can't be used again.
            LogWriteTimedOut(_logger, peerId);
            _ = session.CloseAsync(null, "Sending timed out.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or MessageStoreException)
        {
            // Whatever wasn't sent stays waiting, and goes out when the device is back.
            LogSendFailed(_logger, peerId, ex.Message);
        }
    }

    private async Task SendWaitingMessagesAsync(PeerSession session, CancellationToken cancellationToken)
    {
        var peerId = session.RemoteDeviceId;
        var now = _time.GetUtcNow();

        // A device without groups would refuse a group copy, and that refusal would hold its one-to-one messages too.
        var supportsGroups = session.Supports(RmpCapabilities.Groups);
        var waiting = _store.GetUndelivered(peerId).Where(m => m.GroupId is null || supportsGroups).ToList();
        var due = new List<StoredMessage>();
        lock (_gate)
        {
            if (_holds.TryGetValue(peerId, out var hold) && hold.SessionId == session.SessionId)
            {
                return;
            }

            var inFlight = waiting.Count(m => _attempts.TryGetValue((m.MessageId, peerId), out var attempt)
                && attempt.SessionId == session.SessionId && now - attempt.At < _options.AckTimeout);

            foreach (var message in waiting)
            {
                if (inFlight + due.Count >= _options.MaxInFlight)
                {
                    break;
                }

                var waitingForAck = _attempts.TryGetValue((message.MessageId, peerId), out var attempt)
                    && attempt.SessionId == session.SessionId && now - attempt.At < _options.AckTimeout;
                if (!waitingForAck)
                {
                    due.Add(message);
                }
            }
        }

        foreach (var message in due)
        {
            lock (_gate)
            {
                _attempts[(message.MessageId, peerId)] = new Attempt(session.SessionId, _time.GetUtcNow());
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.WriteTimeout);
            var payload = message.GroupId is { } groupId
                ? ToGroupPayload(groupId, message.MembershipVersion ?? 1, message.Content)
                : ToPayload(peerId, message.Content);
            await session.SendAsync(RmpMessageTypes.ChatMessage, payload, message.MessageId, message.Timestamp, timeout.Token).ConfigureAwait(false);

            if (_store.Advance(peerId, message.MessageId, DeliveryState.Sent))
            {
                RaiseChanged(peerId);
            }
        }
    }

    private async Task SendReadReceiptsAsync(PeerSession session, CancellationToken cancellationToken)
    {
        var peerId = session.RemoteDeviceId;
        foreach (var batch in _store.GetPendingReadReceipts(peerId).Chunk(ContentPayloads.MaxReadReceiptsPerMessage))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.WriteTimeout);
            await session.SendAsync(RmpMessageTypes.MessageRead, SessionPayloads.ToElement(new MessageReadPayload(batch)), timeout.Token).ConfigureAwait(false);
            _store.ClearPendingReadReceipts(peerId, batch);
        }
    }

    private async ValueTask HandleMessageAsync(PeerSession session, RmpEnvelope message, CancellationToken cancellationToken)
    {
        switch (message.Type)
        {
            case RmpMessageTypes.ChatMessage:
                await OnChatMessageAsync(session, message, cancellationToken).ConfigureAwait(false);
                break;

            case RmpMessageTypes.MessageAck when ContentPayloads.TryReadMessageAck(message.Payload, out var ack):
                OnProgress(session.RemoteDeviceId, [ack.MessageId], DeliveryState.Delivered);
                break;

            case RmpMessageTypes.MessageRead when ContentPayloads.TryReadMessageRead(message.Payload, out var read):
                OnProgress(session.RemoteDeviceId, read.MessageIds, DeliveryState.Read);
                break;

            case RmpMessageTypes.Error when SessionPayloads.TryReadError(message.Payload, out var error):
                OnError(session, error);
                break;
        }
    }

    private async Task OnChatMessageAsync(PeerSession session, RmpEnvelope message, CancellationToken cancellationToken)
    {
        var peerId = session.RemoteDeviceId;
        if (!ContentPayloads.TryReadChatMessage(message.Payload, out var chat))
        {
            throw new RmpProtocolException(RmpErrorCodes.InvalidFrame, "The message content is not valid.");
        }

        var isGroup = chat.GroupId is not null;
        if (isGroup && !session.Supports(RmpCapabilities.Groups))
        {
            throw new RmpProtocolException(RmpErrorCodes.UnsupportedMessage, "The 'groups' feature wasn't agreed for this session.");
        }

        if (chat.ConversationId != (chat.GroupId ?? DirectConversation.IdFor(session.LocalDeviceId, peerId)))
        {
            throw new RmpProtocolException(RmpErrorCodes.InvalidFrame, "The message belongs to another conversation.");
        }

        if (isGroup)
        {
            await _groups.CheckIncomingMessageAsync(session, chat.GroupId!, chat.MembershipVersion!.Value, cancellationToken).ConfigureAwait(false);
        }

        // If this throws, no acknowledgement goes out and the sender tries again later.
        var stored = new StoredMessage(message.MessageId, peerId, MessageDirection.Incoming, chat.Content, message.Timestamp, DeliveryState.Delivered, GroupId: chat.GroupId, MembershipVersion: chat.MembershipVersion);
        if (_store.TryAddIncoming(stored))
        {
            RaiseChanged(peerId);
            IncomingMessage?.Invoke(this, stored);
        }

        // Also for a duplicate: its sender is retrying because the first acknowledgement never arrived.
        var ack = SessionPayloads.ToElement(new MessageAckPayload(message.MessageId));
        await session.SendAsync(RmpMessageTypes.MessageAck, ack, cancellationToken).ConfigureAwait(false);
    }

    private void OnProgress(Guid peerId, IReadOnlyList<Guid> messageIds, DeliveryState state)
    {
        // The store looks these up in this device's conversation only, so a device can only
        // confirm messages that were sent to it.
        var changed = false;
        foreach (var id in messageIds)
        {
            changed |= _store.Advance(peerId, id, state);
            lock (_gate)
            {
                _attempts.Remove((id, peerId));
            }
        }

        if (changed)
        {
            RaiseChanged(peerId);
        }

        Enqueue(peerId);
    }

    private void OnError(PeerSession session, ErrorPayload error)
    {
        var peerId = session.RemoteDeviceId;
        if (error.RequestId is not { } messageId || _store.GetUndelivered(peerId).All(m => m.MessageId != messageId))
        {
            return;
        }

        switch (error.Code)
        {
            case RmpErrorCodes.MessageTooLarge or RmpErrorCodes.InvalidFrame:
                if (_store.Advance(peerId, messageId, DeliveryState.Failed, "The other device refused this message."))
                {
                    lock (_gate)
                    {
                        _attempts.Remove((messageId, peerId));
                    }

                    RaiseChanged(peerId);
                }

                break;

            case RmpErrorCodes.NotAMember:
                if (_store.Advance(peerId, messageId, DeliveryState.Failed, "Refused: one of you isn't in this group any more."))
                {
                    lock (_gate)
                    {
                        _attempts.Remove((messageId, peerId));
                    }

                    RaiseChanged(peerId);
                }

                break;

            case RmpErrorCodes.PeerNotTrusted:
                HoldSession(session, "The other device no longer trusts this computer. Messages will wait until you pair again.");
                break;

            case RmpErrorCodes.UnsupportedMessage:
                HoldSession(session, "The other device can't receive messages right now. They will wait until it reconnects.");
                break;

            // Anything else may be temporary: the message stays unacknowledged and is sent again later.
        }
    }

    /// <summary>
    /// Stops sending on a session the peer has said it will refuse: each retry would count against the limit
    /// on refused messages and end up closing the session.
    /// </summary>
    private void HoldSession(PeerSession session, string reason)
    {
        lock (_gate)
        {
            _holds[session.RemoteDeviceId] = new Hold(session.SessionId, reason);
        }

        LogHeld(_logger, session.RemoteDeviceId, reason);
        RaiseChanged(session.RemoteDeviceId);
    }

    private JsonElement ToPayload(Guid peerId, string content) => SessionPayloads.ToElement(
        new ChatMessagePayload(DirectConversation.IdFor(_identity.Identity.DeviceId, peerId), RmpContentTypes.Text, content));

    private static JsonElement ToGroupPayload(string groupId, int version, string content) => SessionPayloads.ToElement(
        new ChatMessagePayload(groupId, RmpContentTypes.Text, content, groupId, version));

    private void RaiseChanged(Guid peerId) => Changed?.Invoke(this, peerId);

    private readonly record struct Attempt(Guid SessionId, DateTimeOffset At);

    private readonly record struct Hold(Guid SessionId, string Reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Couldn't send messages to {DeviceId}: {Reason}")]
    private static partial void LogSendFailed(ILogger logger, Guid deviceId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sending to {DeviceId} timed out; closing the session")]
    private static partial void LogWriteTimedOut(ILogger logger, Guid deviceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Holding messages to {DeviceId}: {Reason}")]
    private static partial void LogHeld(ILogger logger, Guid deviceId, string reason);
}
