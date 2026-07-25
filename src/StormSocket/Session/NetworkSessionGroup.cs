using System.Collections.Concurrent;

namespace StormSocket.Session;

/// <summary>
/// Manages named groups (rooms/channels) of sessions for targeted broadcast.
/// Thread-safe. Sessions are automatically cleaned up when they disconnect.
/// </summary>
public sealed class NetworkSessionGroup
{
    // A detached session may still be handed to Add/JoinGroup by a disconnect handler, which runs
    // after RemoveFromAll. The latch only has to outlive that handler chain, so entries are kept for
    // a wide margin and then pruned; keeping them forever would grow with the process lifetime.
    private const long DetachRetentionMs = 60_000;
    private const int DetachPruneThreshold = 1024;
    private const long DetachPruneIntervalMs = 1_000;

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<long, ISession>> _groups = new();
    private readonly ConcurrentDictionary<long, long> _detachedSessions = new();
    private long _nextPruneMs;

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

    /// <summary>
    /// Removes a session from all groups it belongs to (called on disconnect).
    /// The session is permanently detached: any later <see cref="Add"/> or <c>JoinGroup</c> for it —
    /// typically from an OnDisconnected handler, which runs after this call — is ignored, because
    /// nothing would ever remove the session again.
    /// </summary>
    public void RemoveFromAll(ISession session)
    {
        long now = Environment.TickCount64;

        // Latch before unregistering: a registration that still observes the session as attached must
        // land in the dictionary before the sweep below reaches it, never after.
        _detachedSessions[session.Id] = now;

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

        PruneDetached(now);
    }

    /// <summary>
    /// Adds a session to the central dictionary only. Called by session.JoinGroup().
    /// Does not call back to session to avoid circular calls.
    /// </summary>
    internal void RegisterSession(string group, ISession session)
    {
        while (true)
        {
            ConcurrentDictionary<long, ISession> members = _groups.GetOrAdd(group, static _ => new ConcurrentDictionary<long, ISession>());
            members.TryAdd(session.Id, session);

            // Re-checked after the add so that a concurrent RemoveFromAll either sees this member and
            // sweeps it, or is seen here and undone — no interleaving can leave the session behind.
            if (_detachedSessions.ContainsKey(session.Id))
            {
                members.TryRemove(session.Id, out _);
                TryRemoveEmptyGroup(group, members);
                return;
            }

            if (_groups.TryGetValue(group, out ConcurrentDictionary<long, ISession>? current) && ReferenceEquals(current, members))
            {
                return;
            }

            // A concurrent removal detached this instance from _groups after observing it empty;
            // the member would be invisible to every reader, so retry against the published one.
            members.TryRemove(session.Id, out _);
        }
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
            TryRemoveEmptyGroup(group, members);
        }
    }

    private void TryRemoveEmptyGroup(string group, ConcurrentDictionary<long, ISession> members)
    {
        if (!members.IsEmpty)
        {
            return;
        }

        // Remove the exact instance observed as empty: a concurrent registration may already have
        // published a different one under this name, and dropping that would lose its members.
        ICollection<KeyValuePair<string, ConcurrentDictionary<long, ISession>>> groups = _groups;
        if (!groups.Remove(new KeyValuePair<string, ConcurrentDictionary<long, ISession>>(group, members)))
        {
            return;
        }

        // A registration that slipped in between the emptiness check and the removal added to the
        // instance just detached, so republish it instead of silently dropping that member.
        if (!members.IsEmpty)
        {
            ConcurrentDictionary<long, ISession> current = _groups.GetOrAdd(group, members);
            if (!ReferenceEquals(current, members))
            {
                foreach (KeyValuePair<long, ISession> member in members)
                {
                    current.TryAdd(member.Key, member.Value);
                }
            }
        }
    }

    private void PruneDetached(long now)
    {
        if (_detachedSessions.Count < DetachPruneThreshold)
        {
            return;
        }

        long nextPrune = Volatile.Read(ref _nextPruneMs);
        if (now < nextPrune || Interlocked.CompareExchange(ref _nextPruneMs, now + DetachPruneIntervalMs, nextPrune) != nextPrune)
        {
            return;
        }

        ICollection<KeyValuePair<long, long>> detached = _detachedSessions;
        foreach (KeyValuePair<long, long> entry in _detachedSessions)
        {
            if (now - entry.Value >= DetachRetentionMs)
            {
                detached.Remove(entry);
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

        // Dispatch to everyone first, then await: awaiting inside the loop would let one member on
        // SlowConsumerPolicy.Wait stall delivery to every member behind it.
        List<ValueTask> tasks = [];
        foreach (ISession session in members.Values)
        {
            if (session.Id == excludeId)
            {
                continue;
            }

            try
            {
                tasks.Add(session.SendAsync(data, cancellationToken));
            }
            catch
            {
                // a synchronous throw must not abandon the ValueTasks already collected.
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
                // ignored
            }
        }
    }

    /// <summary>Returns the number of sessions in a group (0 if the group doesn't exist).</summary>
    public int MemberCount(string group) => _groups.TryGetValue(group, out ConcurrentDictionary<long, ISession>? members) ? members.Count : 0;

    /// <summary>Enumerates all existing group names.</summary>
    public IEnumerable<string> GroupNames => _groups.Keys;
}
