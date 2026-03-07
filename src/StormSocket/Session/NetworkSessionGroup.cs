using System.Collections.Concurrent;

namespace StormSocket.Session;

/// <summary>
/// Manages named groups (rooms/channels) of sessions for targeted broadcast.
/// Thread-safe. Sessions are automatically cleaned up when they disconnect.
/// </summary>
public sealed class NetworkSessionGroup
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<long, INetworkSession>> _groups = new();

    /// <summary>Adds a session to a named group. Creates the group if it doesn't exist.</summary>
    public void Add(string group, INetworkSession networkSession)
    {
        ConcurrentDictionary<long, INetworkSession> members = _groups.GetOrAdd(group, _ => new ConcurrentDictionary<long, INetworkSession>());
        members.TryAdd(networkSession.Id, networkSession);
        networkSession.JoinGroup(group);
    }

    /// <summary>Removes a session from a group. Deletes the group if it becomes empty.</summary>
    public void Remove(string group, INetworkSession networkSession)
    {
        if (_groups.TryGetValue(group, out ConcurrentDictionary<long, INetworkSession>? members))
        {
            members.TryRemove(networkSession.Id, out _);
            networkSession.LeaveGroup(group);

            if (members.IsEmpty)
            {
                _groups.TryRemove(group, out _);
            }
        }
    }

    /// <summary>Removes a session from all groups it belongs to (called on disconnect).</summary>
    public void RemoveFromAll(INetworkSession networkSession)
    {
        foreach (string group in networkSession.Groups)
        {
            if (_groups.TryGetValue(group, out ConcurrentDictionary<long, INetworkSession>? members))
            {
                members.TryRemove(networkSession.Id, out _);
                if (members.IsEmpty)
                {
                    _groups.TryRemove(group, out _);
                }
            }
        }
    }

    /// <summary>Sends data to all members of a group. Best-effort: individual failures are silently ignored.</summary>
    public async ValueTask BroadcastAsync(string group, ReadOnlyMemory<byte> data, long? excludeId = null, CancellationToken cancellationToken = default)
    {
        if (!_groups.TryGetValue(group, out ConcurrentDictionary<long, INetworkSession>? members))
        {
            return;
        }

        foreach (INetworkSession session in members.Values)
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
                // ignored
            }
        }
    }

    /// <summary>Returns the number of sessions in a group (0 if the group doesn't exist).</summary>
    public int MemberCount(string group) => _groups.TryGetValue(group, out ConcurrentDictionary<long, INetworkSession>? members) ? members.Count : 0;

    /// <summary>Enumerates all existing group names.</summary>
    public IEnumerable<string> GroupNames => _groups.Keys;
}
