namespace StormSocket.Core;

/// <summary>
/// Backing store for the library's asynchronous events.
/// </summary>
/// <remarks>
/// A plain <c>event</c> whose delegate returns <see cref="ValueTask"/> is a trap with more than one
/// subscriber: invoking a multicast delegate returns only the LAST subscriber's task, so every other
/// subscriber runs unawaited — its ordering guarantee is gone and its exceptions surface as
/// <see cref="TaskScheduler.UnobservedTaskException"/> instead of reaching OnError. Keeping the
/// subscribers in a snapshot array lets every one of them be awaited in registration order without
/// allocating an invocation list on each raise.
/// </remarks>
internal sealed class AsyncEventSource<THandler> where THandler : Delegate
{
    private readonly object _sync = new();
    private volatile THandler[] _handlers = Array.Empty<THandler>();

    /// <summary>Current subscribers. The array is replaced, never mutated, so it is safe to iterate unlocked.</summary>
    public THandler[] Handlers => _handlers;

    public bool HasHandlers => _handlers.Length > 0;

    public void Add(THandler? handler)
    {
        if (handler is null)
        {
            return;
        }

        lock (_sync)
        {
            THandler[] updated = new THandler[_handlers.Length + 1];
            Array.Copy(_handlers, updated, _handlers.Length);
            updated[^1] = handler;
            _handlers = updated;
        }
    }

    public void Remove(THandler? handler)
    {
        if (handler is null)
        {
            return;
        }

        lock (_sync)
        {
            int index = Array.LastIndexOf(_handlers, handler);
            if (index < 0)
            {
                return;
            }

            THandler[] updated = new THandler[_handlers.Length - 1];
            Array.Copy(_handlers, updated, index);
            Array.Copy(_handlers, index + 1, updated, index, _handlers.Length - index - 1);
            _handlers = updated;
        }
    }
}
