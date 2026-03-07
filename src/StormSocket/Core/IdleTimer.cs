using Microsoft.Extensions.Logging;

namespace StormSocket.Core;

/// <summary>
/// Monitors a connection for inactivity. If no application-level data is received
/// within the configured timeout, fires <see cref="OnTimeout"/>.
/// Ping/pong frames do NOT reset the timer — only actual data frames count.
/// </summary>
internal sealed class IdleTimer : IAsyncDisposable
{
    private readonly TimeSpan _timeout;
    private readonly ILogger? _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task? _task;
    private bool _disposed;
    private long _lastActivityTicks;

    /// <summary>
    /// Fired when the idle timeout expires without any data activity.
    /// </summary>
    public Func<ValueTask>? OnTimeout { get; set; }

    public IdleTimer(TimeSpan timeout, ILogger? logger = null)
    {
        _timeout = timeout;
        _logger = logger;
        _lastActivityTicks = Environment.TickCount64;
    }

    public void Start()
    {
        _task = RunAsync(_cts.Token);
    }

    /// <summary>
    /// Called when application-level data is received. Resets the idle clock.
    /// </summary>
    public void OnDataReceived()
    {
        Volatile.Write(ref _lastActivityTicks, Environment.TickCount64);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        // Check at half the timeout interval for responsiveness
        TimeSpan checkInterval = _timeout > TimeSpan.FromSeconds(2)
            ? TimeSpan.FromMilliseconds(_timeout.TotalMilliseconds / 2)
            : _timeout;

        using PeriodicTimer timer = new(checkInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                long elapsed = Environment.TickCount64 - Volatile.Read(ref _lastActivityTicks);
                if (elapsed >= _timeout.TotalMilliseconds)
                {
                    _logger?.LogDebug("Idle timeout reached ({Timeout})", _timeout);
                    if (OnTimeout is not null)
                    {
                        await OnTimeout.Invoke().ConfigureAwait(false);
                    }

                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Idle timer error");
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
