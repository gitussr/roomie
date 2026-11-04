using Microsoft.Extensions.Logging;
using Roomie.Core.Identity;
using Roomie.Core.Profiles;
using Roomie.Core.Protocol;
using Roomie.Core.Trust;
using Roomie.Network.Connections;

namespace Roomie.Network.Profiles;

/// <summary>
/// Exchanges pictures between paired devices (architecture.md §15). Each side sends its own whenever a trusted
/// session starts or the picture changes, so there's nothing to retry: a device that missed one gets it next time.
/// Never over UDP: discovery is unauthenticated, and anyone could otherwise show any picture under any name.
/// </summary>
public sealed partial class ProfileService : IDisposable
{
    private const int MaxUpdatesPerMinute = 10;
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(30);

    private readonly ConnectionManager _connections;
    private readonly IAvatarStore _store;
    private readonly ILocalIdentityProvider _identity;
    private readonly ITrustStore _trust;
    private readonly TimeProvider _time;
    private readonly ILogger<ProfileService> _logger;
    private readonly IDisposable _registration;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Queue<DateTimeOffset>> _recent = [];

    public ProfileService(ConnectionManager connections, IAvatarStore store, ILocalIdentityProvider identity, ITrustStore trust, TimeProvider time, ILogger<ProfileService> logger)
    {
        _connections = connections;
        _store = store;
        _identity = identity;
        _trust = trust;
        _time = time;
        _logger = logger;

        _registration = connections.Dispatcher.Register([RmpMessageTypes.ProfileUpdate], HandleMessageAsync);
        connections.SessionOpened += OnSessionReady;
        connections.SessionTrusted += OnSessionReady;
        trust.Changed += OnTrustChanged;
        OnTrustChanged(this, EventArgs.Empty);
    }

    /// <summary>A device's picture changed. Raised on any thread.</summary>
    public event EventHandler<Guid>? Changed;

    public AvatarChoice Own => _store.Load(_identity.Identity.DeviceId);

    /// <summary>Saves this device's picture and tells the devices connected now.</summary>
    /// <returns>Whether it could be saved.</returns>
    public bool SetOwn(AvatarChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        var self = _identity.Identity.DeviceId;
        try
        {
            _store.Save(self, choice);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogSaveFailed(_logger, self, ex.Message);
            return false;
        }

        Changed?.Invoke(this, self);
        foreach (var info in _connections.Snapshot().Where(c => c.IsConnected))
        {
            if (_connections.TryGetSession(info.DeviceId, out var session))
            {
                _ = SendOwnAsync(session, choice);
            }
        }

        return true;
    }

    public void Dispose()
    {
        _registration.Dispose();
        _connections.SessionOpened -= OnSessionReady;
        _connections.SessionTrusted -= OnSessionReady;
        _trust.Changed -= OnTrustChanged;
    }

    private void OnSessionReady(object? sender, PeerSession session)
    {
        if (session.IsTrusted)
        {
            _ = SendOwnAsync(session, Own);
        }
    }

    private void OnTrustChanged(object? sender, EventArgs e)
    {
        var keep = _trust.TrustedPeers.Select(p => p.DeviceId).ToHashSet();
        keep.Add(_identity.Identity.DeviceId);
        _store.RemoveAllExcept(keep);
    }

    private async Task SendOwnAsync(PeerSession session, AvatarChoice choice)
    {
        if (!session.IsTrusted || !session.Supports(RmpCapabilities.Profile))
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(SendTimeout);
            await session.SendAsync(RmpMessageTypes.ProfileUpdate, SessionPayloads.ToElement(ProfileProtocol.ToPayload(choice)), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            // The next session sends it again.
            LogSendFailed(_logger, session.RemoteDeviceId, ex.Message);
        }
    }

    private ValueTask HandleMessageAsync(PeerSession session, RmpEnvelope message, CancellationToken cancellationToken)
    {
        var peerId = session.RemoteDeviceId;
        if (SessionPayloads.TryDeserialize<ProfileUpdatePayload>(message.Payload) is not { } update
            || !ProfileProtocol.TryRead(update, out var choice)
            || !IsWithinLimit(peerId)
            || _store.Load(peerId).IsSameAs(choice))
        {
            return ValueTask.CompletedTask;
        }

        try
        {
            _store.Save(peerId, choice);
            Changed?.Invoke(this, peerId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogSaveFailed(_logger, peerId, ex.Message);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Each change writes to disk, so a paired device can't make that happen endlessly.</summary>
    private bool IsWithinLimit(Guid peerId)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (!_recent.TryGetValue(peerId, out var times))
            {
                _recent[peerId] = times = new Queue<DateTimeOffset>();
            }

            while (times.Count > 0 && now - times.Peek() >= TimeSpan.FromMinutes(1))
            {
                times.Dequeue();
            }

            if (times.Count >= MaxUpdatesPerMinute)
            {
                return false;
            }

            times.Enqueue(now);
            return true;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't save the picture of {DeviceId}: {Reason}")]
    private static partial void LogSaveFailed(ILogger logger, Guid deviceId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Couldn't send this device's picture to {DeviceId}: {Reason}")]
    private static partial void LogSendFailed(ILogger logger, Guid deviceId, string reason);
}
