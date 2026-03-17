using System.Collections.Concurrent;

namespace StormSocket.Session;

/// <summary>
/// Manages named groups (rooms/channels) of sessions for targeted broadcast.
/// Thread-safe. Sessions are automatically cleaned up when they disconnect.
/// </summary>
public sealed class NetworkSessionGroup
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<long, ISession>> _groups = new();

    /// <summary>Adds a session to a named group. Creates the group if it doesn't exist.</summary>
    public void Add(string group, ISession session)
    {
        RegisterSession(group, session);
        session.JoinGroup(group);
    }

    /// <summary>Removes a session from a group. Deletes the group if it becomes empty.</summary>
    public void Remove(string group, ISession session)
    {
        UnregisterSession(group, session);
        session.LeaveGroup(group);
    }

    /// <summary>Removes a session from all groups it belongs to (called on disconnect).</summary>
    public void RemoveFromAll(ISession session)
    {
        // Snapshot group list to avoid modification during iteration
        foreach (string group in session.Groups)
        {
            UnregisterSession(group, session);
        }

        // Clear the session's local set
        if (session is TcpSession tcp)
        {
            tcp.ClearGroups();
        }
        else if (session is WebSocketSession ws)
        {
            ws.ClearGroups();
        }
    }

    /// <summary>
    /// Adds a session to the central dictionary only. Called by session.JoinGroup().
    /// Does not call back to session to avoid circular calls.
    /// </summary>
    internal void RegisterSession(string group, ISession session)
    {
        ConcurrentDictionary<long, ISession> members = _groups.GetOrAdd(group, _ => new ConcurrentDictionary<long, ISession>());
        members.TryAdd(session.Id, session);
    }

    /// <summary>
    /// Removes a session from the central dictionary only. Called by session.LeaveGroup().
    /// Does not call back to session to avoid circular calls.
    /// </summary>
    internal void UnregisterSession(string group, ISession session)
    {
        if (_groups.TryGetValue(group, out ConcurrentDictionary<long, ISession>? members))
        {
            members.TryRemove(session.Id, out _);
            if (members.IsEmpty)
            {
                _groups.TryRemove(group, out _);
            }
        }
    }

    /// <summary>Sends data to all members of a group. Best-effort: individual failures are silently ignored.</summary>
    public async ValueTask BroadcastAsync(string group, ReadOnlyMemory<byte> data, long? excludeId = null, CancellationToken cancellationToken = default)
    {
        if (!_groups.TryGetValue(group, out ConcurrentDictionary<long, ISession>? members))
        {
            return;
        }

        foreach (ISession session in members.Values)
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
    public int MemberCount(string group) => _groups.TryGetValue(group, out ConcurrentDictionary<long, ISession>? members) ? members.Count : 0;

    /// <summary>Enumerates all existing group names.</summary>
    public IEnumerable<string> GroupNames => _groups.Keys;
}
