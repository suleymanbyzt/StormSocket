using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace StormSocket.Session;

/// <summary>
/// Thread-safe collection of active sessions. Used by servers to track, query, and broadcast to connections.
/// </summary>
public sealed class SessionManager
{
    private readonly ConcurrentDictionary<long, ISession> _sessions = new();

    /// <summary>Number of currently connected sessions.</summary>
    public int Count => _sessions.Count;

    /// <summary>Enumerates all active sessions (snapshot-safe).</summary>
    public IEnumerable<ISession> All => _sessions.Values;

    public bool TryAdd(ISession session) => _sessions.TryAdd(session.Id, session);

    public bool TryRemove(long id, out ISession? session)
    {
        bool result = _sessions.TryRemove(id, out ISession? s);
        session = s;
        return result;
    }

    public bool TryGet(long id, out ISession? session)
    {
        bool result = _sessions.TryGetValue(id, out ISession? s);
        session = s;
        return result;
    }

    /// <summary>Sends data to all sessions. Best-effort: individual failures are silently ignored.</summary>
    public async ValueTask BroadcastAsync(ReadOnlyMemory<byte> data, long? excludeId = null, CancellationToken cancellationToken = default)
    {
        foreach (ISession session in _sessions.Values)
        {
            if (session.Id == excludeId)
            {
                continue;
            }

            try
            {
                await session.SendAsync(data, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // best-effort broadcast; individual send failures are ignored.
            }
        }
    }

    /// <summary>Gracefully closes all sessions (used during server shutdown).</summary>
    public async ValueTask CloseAllAsync(CancellationToken cancellationToken = default)
    {
        List<ValueTask> tasks = [];
        foreach (ISession session in _sessions.Values)
        {
            tasks.Add(session.CloseAsync(cancellationToken));
        }

        foreach (ValueTask task in tasks)
        {
            try
            {
                await task.ConfigureAwait(false);

            }
            catch
            {
                //ignored
            }
        }

        _sessions.Clear();
    }
}