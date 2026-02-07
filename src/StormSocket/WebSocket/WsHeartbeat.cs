namespace StormSocket.WebSocket;

/// <summary>
/// Sends periodic WebSocket Ping frames and tracks Pong responses.
/// If the remote side misses too many consecutive pongs, fires <see cref="OnTimeout"/>
/// so the server can close the dead connection.
/// </summary>
public sealed class WsHeartbeat : IAsyncDisposable
{
    private readonly Func<CancellationToken, ValueTask> _sendPing;
    private readonly TimeSpan _interval;
    private readonly int _missedPongsAllowed;
    private readonly CancellationTokenSource _cts = new();
    private Task? _task;
    private bool _disposed;
    private int _missedPongs;

    /// <summary>
    /// Fired when the remote side has missed more than the allowed
    /// consecutive pong responses, indicating the connection is likely dead.
    /// </summary>
    public Func<ValueTask>? OnTimeout { get; set; }

    /// <summary>
    /// Number of consecutive pongs missed so far.
    /// </summary>
    public int MissedPongs => Volatile.Read(ref _missedPongs);

    /// <param name="sendPing">
    /// Callback that sends a Ping frame through the session's write lock.
    /// </param>
    public WsHeartbeat(Func<CancellationToken, ValueTask> sendPing, TimeSpan interval, int missedPongsAllowed = 3)
    {
        _sendPing = sendPing;
        _interval = interval;
        _missedPongsAllowed = missedPongsAllowed;
    }

    public void Start()
    {
        _task = RunAsync(_cts.Token);
    }

    /// <summary>
    /// Called when a Pong frame is received from the remote side.
    /// Resets the missed pong counter.
    /// </summary>
    public void OnPongReceived()
    {
        Interlocked.Exchange(ref _missedPongs, 0);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using PeriodicTimer timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                int missed = Interlocked.Increment(ref _missedPongs);

                if (missed > _missedPongsAllowed)
                {
                    if (OnTimeout is not null)
                    {
                        await OnTimeout.Invoke().ConfigureAwait(false);
                    }

                    break;
                }

                await _sendPing(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        
        _disposed = true;

#if NET8_0_OR_GREATER
        await _cts.CancelAsync().ConfigureAwait(false);
#else
        _cts.Cancel();
#endif

        if (_task is not null)
        {
            try
            {
                await _task.ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }
        }

        _cts.Dispose();
    }
}