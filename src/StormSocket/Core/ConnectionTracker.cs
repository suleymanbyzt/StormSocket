using System.Collections.Concurrent;

namespace StormSocket.Core;

/// <summary>
/// Tracks the detached connection handler tasks a server starts, so shutdown can wait for the
/// in-flight ones instead of returning while they are still running.
/// </summary>
/// <remarks>
/// Entries are removed as handlers complete, so the set stays proportional to the live connection
/// count rather than to the number of connections the server has ever accepted.
/// </remarks>
internal sealed class ConnectionTracker
{
    private readonly ConcurrentDictionary<Task, byte> _inFlight = new();
    private readonly object _sync = new();
    private bool _closed;

    /// <summary>Handler tasks registered and not yet completed.</summary>
    public int Count => _inFlight.Count;

    /// <summary>Registers a connection handler task.</summary>
    public void Track(Task handler)
    {
        lock (_sync)
        {
            if (_closed)
            {
                return;
            }

            _inFlight[handler] = 0;
        }

        _ = handler.ContinueWith(
            static (completed, state) => ((ConcurrentDictionary<Task, byte>)state!).TryRemove(completed, out _),
            _inFlight,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Refuses further registrations. Call it once the accept loop has finished: that is what keeps a
    /// connection accepted in the last moments of the accept loop from being registered behind the
    /// drain's snapshot and then never waited for.
    /// </summary>
    public void Close()
    {
        lock (_sync)
        {
            _closed = true;
        }
    }

    /// <summary>
    /// Accepts registrations again, for a server that is being restarted. Handlers abandoned by the
    /// previous shutdown are dropped rather than carried over: they had their drain window already,
    /// and the next shutdown's budget belongs to the connections this run accepted.
    /// </summary>
    public void Open()
    {
        lock (_sync)
        {
            _inFlight.Clear();
            _closed = false;
        }
    }

    /// <summary>
    /// Waits for the registered handlers to finish, bounded by <paramref name="timeout"/> and by
    /// <paramref name="cancellationToken"/>, whichever comes first. Never throws — both bounds mean
    /// "stop waiting and force the rest", which is a normal outcome rather than a failure.
    /// </summary>
    /// <param name="timeout">
    /// <see cref="TimeSpan.Zero"/> or less does not wait at all; <see cref="Timeout.InfiniteTimeSpan"/>
    /// waits until every handler has finished.
    /// </param>
    /// <returns>How many handlers were still running when the wait ended. Zero means a clean drain.</returns>
    public async Task<int> DrainAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        Task[] pending;
        lock (_sync)
        {
            pending = [.. _inFlight.Keys];
        }

        if (pending.Length == 0)
        {
            return 0;
        }

        bool infinite = timeout == Timeout.InfiniteTimeSpan;
        if (!infinite && timeout <= TimeSpan.Zero)
        {
            return _inFlight.Count;
        }

        // A handler that faulted is no longer running, which is all the drain is waiting for, so the
        // faults are absorbed here instead of surfacing out of Task.WhenAll.
        Task all = Task.WhenAll(Array.ConvertAll(pending, static handler => handler.ContinueWith(
            static _ => { },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default)));

        using CancellationTokenSource drainCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!infinite)
        {
            drainCts.CancelAfter(timeout);
        }

        try
        {
            await all.WaitAsync(drainCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Drain budget spent, or the caller stopped waiting.
        }

        return _inFlight.Count;
    }
}
