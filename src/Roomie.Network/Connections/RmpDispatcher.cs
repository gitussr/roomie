using Microsoft.Extensions.Logging;
using Roomie.Core.Protocol;

namespace Roomie.Network.Connections;

/// <summary>
/// Handles one validated message. Called on the session's read loop, one message at a time, so messages from
/// a device arrive in order; while it runs, that session reads nothing else, including heartbeats. A handler
/// may do brief work such as a database write, but must never wait for a user.
/// </summary>
public delegate ValueTask RmpMessageHandler(PeerSession session, RmpEnvelope message, CancellationToken cancellationToken);

/// <summary>
/// Routes validated messages to the service that owns their type (architecture.md §5.1, step 6). Each type
/// has one owner. The dispatcher knows nothing about what the services do.
/// </summary>
public sealed partial class RmpDispatcher(ILogger logger)
{
    // Handled inside PeerSession, because they are the session itself rather than application data.
    private static readonly HashSet<string> SessionTypes =
        [RmpMessageTypes.Hello, RmpMessageTypes.Heartbeat, RmpMessageTypes.HeartbeatAck, RmpMessageTypes.Goodbye];

    private readonly Lock _gate = new();
    private readonly Dictionary<string, RmpMessageHandler> _handlers = new(StringComparer.Ordinal);

    /// <summary>Makes <paramref name="handler"/> the owner of these message types until the result is disposed.</summary>
    /// <exception cref="ArgumentException">A type is unknown, belongs to the session layer, or already has an owner.</exception>
    public IDisposable Register(IReadOnlyCollection<string> types, RmpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(types);
        ArgumentNullException.ThrowIfNull(handler);

        lock (_gate)
        {
            foreach (var type in types)
            {
                if (RmpMessagePolicy.Find(type) is null || SessionTypes.Contains(type))
                {
                    throw new ArgumentException($"{type} can't have a handler.", nameof(types));
                }

                if (_handlers.ContainsKey(type))
                {
                    throw new ArgumentException($"{type} already has a handler.", nameof(types));
                }
            }

            foreach (var type in types)
            {
                _handlers.Add(type, handler);
            }
        }

        return new Registration(this, types, handler);
    }

    /// <summary>
    /// Runs the owner of the message. A failure in a handler is reported as a generic error that reveals
    /// nothing about the cause (architecture.md §9.1), and doesn't take the session down with it.
    /// </summary>
    /// <returns>Why the message couldn't be handled, or null if it was.</returns>
    internal async ValueTask<RmpRejection?> DispatchAsync(PeerSession session, RmpEnvelope message, CancellationToken cancellationToken)
    {
        RmpMessageHandler? handler;
        lock (_gate)
        {
            _handlers.TryGetValue(message.Type, out handler);
        }

        if (handler is null)
        {
            return new RmpRejection(RmpErrorCodes.UnsupportedMessage, "This device can't handle that message right now.");
        }

#pragma warning disable CA1031 // A handler is application code: whatever it throws must not end the read loop.
        try
        {
            await handler(session, message, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RmpProtocolException ex)
        {
            // The handler found the message itself unacceptable, which only it could judge.
            return new RmpRejection(ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            LogHandlerFailed(logger, message.Type, session.RemoteDeviceId, ex);
            return new RmpRejection(RmpErrorCodes.InternalError, "The message couldn't be processed.");
        }
#pragma warning restore CA1031
    }

    /// <summary>Passes an <c>ERROR</c> to its owner, if a service registered one. It is never answered, even if the handler fails.</summary>
    internal async ValueTask DispatchErrorAsync(PeerSession session, RmpEnvelope message, CancellationToken cancellationToken)
    {
        bool hasOwner;
        lock (_gate)
        {
            hasOwner = _handlers.ContainsKey(message.Type);
        }

        if (hasOwner)
        {
            await DispatchAsync(session, message, cancellationToken).ConfigureAwait(false);
        }
    }

    private void Unregister(IReadOnlyCollection<string> types, RmpMessageHandler handler)
    {
        lock (_gate)
        {
            foreach (var type in types)
            {
                if (_handlers.GetValueOrDefault(type) == handler)
                {
                    _handlers.Remove(type);
                }
            }
        }
    }

    private sealed class Registration(RmpDispatcher owner, IReadOnlyCollection<string> types, RmpMessageHandler handler) : IDisposable
    {
        public void Dispose() => owner.Unregister(types, handler);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Handling {Type} from {DeviceId} failed")]
    private static partial void LogHandlerFailed(ILogger logger, string type, Guid deviceId, Exception exception);
}
