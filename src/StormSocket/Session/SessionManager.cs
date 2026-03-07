using System.Collections.Concurrent;

namespace StormSocket.Session;

/// <summary>
/// Thread-safe collection of active sessions. Used by servers to track, query, and broadcast to connections.
/// </summary>
public sealed class SessionManager
{
    private readonly ConcurrentDictionary<long, INetworkSession> _networkSessions = new();

    /// <summary>Number of currently connected sessions.</summary>
    public int Count => _networkSessions.Count;

    /// <summary>Enumerates all active sessions (snapshot-safe).</summary>
    public IEnumerable<INetworkSession> All => _networkSessions.Values;

    public bool TryAdd(INetworkSession networkSession) => _networkSessions.TryAdd(networkSession.Id, networkSession);

    public bool TryRemove(long id, out INetworkSession? networkSession)
    {
        bool result = _networkSessions.TryRemove(id, out INetworkSession? s);
        networkSession = s;
        return result;
    }

    public bool TryGet(long id, out INetworkSession? networkSession)
    {
        bool result = _networkSessions.TryGetValue(id, out INetworkSession? s);
        networkSession = s;
        return result;
    }

    /// <summary>
    /// Sends data to all sessions concurrently. Best-effort: individual failures are silently ignored.
    /// Each session applies its own SlowConsumerPolicy (Drop/Disconnect/Wait) automatically.
    /// Concurrent dispatch ensures one slow client cannot block delivery to others.
    /// </summary>
    public async ValueTask BroadcastAsync(ReadOnlyMemory<byte> data, long? excludeId = null, CancellationToken cancellationToken = default)
    {
        List<ValueTask> tasks = [];
        foreach (INetworkSession networkSession in _networkSessions.Values)
        {
            if (networkSession.Id == excludeId)
            {
                continue;
            }

            tasks.Add(networkSession.SendAsync(data, cancellationToken));
        }

        foreach (ValueTask task in tasks)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
                // best-effort broadcast; individual send failures are ignored.
            }
        }
    }

    /// <summary>Gracefully closes all connection-oriented sessions (used during server shutdown).</summary>
    public async ValueTask CloseAllAsync(CancellationToken cancellationToken = default)
    {
        List<ValueTask> tasks = [];
        foreach (INetworkSession networkSession in _networkSessions.Values)
        {
            if (networkSession is ISession connSession)
            {
                tasks.Add(connSession.CloseAsync(cancellationToken));
            }
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

        _networkSessions.Clear();
    }
}