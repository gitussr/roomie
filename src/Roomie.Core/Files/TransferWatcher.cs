namespace Roomie.Core.Files;

/// <summary>Something about a transfer that the user would want to hear about.</summary>
public enum TransferNews
{
    /// <summary>Another device offered a file.</summary>
    Offered,

    Completed,

    Failed,

    /// <summary>The other side declined.</summary>
    Declined,

    /// <summary>The other side, or this one, stopped it.</summary>
    Canceled,
}

/// <summary>
/// Works out what changed between two looks at the open transfers. Comparing states, rather than adding a call at
/// every place a transfer ends, means no ending can be missed, including ones that happen while nothing is listening.
/// </summary>
public sealed class TransferWatcher
{
    private readonly Dictionary<(Guid Peer, Messaging.MessageDirection Direction, Guid Transfer), FileTransferState> _open = [];
    private readonly Lock _gate = new();

    /// <summary>Records what is open now without reporting it: what was already there at startup isn't news.</summary>
    public void Start(IEnumerable<StoredTransfer> open)
    {
        ArgumentNullException.ThrowIfNull(open);
        lock (_gate)
        {
            _open.Clear();
            foreach (var transfer in open)
            {
                _open[(transfer.PeerId, transfer.Direction, transfer.TransferId)] = transfer.State;
            }
        }
    }

    /// <param name="open">The transfers open now.</param>
    /// <param name="find">Reads a transfer that is no longer open, to see how it ended.</param>
    public IReadOnlyList<(TransferNews News, StoredTransfer Transfer)> Update(
        IReadOnlyCollection<StoredTransfer> open,
        Func<Guid, Messaging.MessageDirection, Guid, StoredTransfer?> find)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(find);

        var news = new List<(TransferNews, StoredTransfer)>();
        lock (_gate)
        {
            var seen = new HashSet<(Guid, Messaging.MessageDirection, Guid)>();
            foreach (var transfer in open)
            {
                var key = (transfer.PeerId, transfer.Direction, transfer.TransferId);
                seen.Add(key);
                var isNew = !_open.ContainsKey(key);
                _open[key] = transfer.State;
                if (isNew && transfer is { Direction: Messaging.MessageDirection.Incoming, State: FileTransferState.Offered })
                {
                    news.Add((TransferNews.Offered, transfer));
                }
            }

            foreach (var key in _open.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                _open.Remove(key);
                if (find(key.Peer, key.Direction, key.Transfer) is not { } ended)
                {
                    continue;
                }

                TransferNews? outcome = ended.State switch
                {
                    FileTransferState.Completed => TransferNews.Completed,
                    FileTransferState.Failed => TransferNews.Failed,
                    FileTransferState.Rejected when ended.Direction == Messaging.MessageDirection.Outgoing => TransferNews.Declined,
                    FileTransferState.Canceled => TransferNews.Canceled,
                    _ => null,
                };
                if (outcome is { } value)
                {
                    news.Add((value, ended));
                }
            }
        }

        return news;
    }
}
