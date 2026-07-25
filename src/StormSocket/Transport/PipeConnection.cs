using System.Buffers;
using System.IO.Pipelines;
using StormSocket.Framing;

namespace StormSocket.Transport;

/// <summary>
/// Reads from transport input using a framer, dispatches complete messages via callback.
/// All write operations are serialized via an internal lock to prevent concurrent PipeWriter access.
/// </summary>
public sealed class PipeConnection : IAsyncDisposable
{
    private readonly ITransport _transport;
    private readonly IMessageFramer _framer;
    private readonly Func<ReadOnlyMemory<byte>, ValueTask> _onMessage;
    private readonly Func<Exception, ValueTask>? _onError;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private volatile bool _isBackpressured;

    /// <summary>True when the send pipe is full and FlushAsync is awaiting drain.</summary>
    public bool IsBackpressured => _isBackpressured;

    /// <summary>
    /// Called when backpressure is first detected (FlushAsync blocks).
    /// Used by TcpSession to enforce SlowConsumerPolicy.Disconnect immediately.
    /// </summary>
    internal Action? OnBackpressureDetected { get; set; }

    public PipeConnection(
        ITransport transport,
        IMessageFramer framer,
        Func<ReadOnlyMemory<byte>, ValueTask> onMessage,
        Func<Exception, ValueTask>? onError = null)
    {
        _transport = transport;
        _framer = framer;
        _onMessage = onMessage;
        _onError = onError;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        PipeReader reader = _transport.Input;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                ReadOnlySequence<byte> buffer = result.Buffer;

                while (_framer.TryReadMessage(ref buffer, out ReadOnlyMemory<byte> message))
                {
                    await _onMessage(message).ConfigureAwait(false);
                }

                reader.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_onError is not null)
            {
                await _onError(ex).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Frames and sends a message.
    /// </summary>
    /// <remarks>
    /// Writes are serialized: PipeWriter is not thread-safe, and two senders interleaving their
    /// GetSpan/Advance calls splice the frames into each other, producing a corrupt length prefix
    /// on the wire and losing payload bytes.
    /// </remarks>
    public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        // Fast path: try to acquire the lock synchronously (no contention).
        // A concurrent DisposeAsync can retire the lock at any point; that is a closed connection,
        // not a caller error, so it is reported as a no-op.
        bool acquired;
        try
        {
            acquired = _writeLock.Wait(0);
        }
        catch (ObjectDisposedException)
        {
            return ValueTask.CompletedTask;
        }

        if (acquired)
        {
            try
            {
                _framer.WriteFrame(data, _transport.Output);
            }
            catch
            {
                _writeLock.Release();
                throw;
            }

            ValueTask<FlushResult> flushTask = _transport.Output.FlushAsync(cancellationToken);
            if (flushTask.IsCompletedSuccessfully)
            {
                _writeLock.Release();
                return ValueTask.CompletedTask;
            }

            return SendSlowFlushAsync(flushTask);
        }

        return SendSlowLockAsync(data, cancellationToken);
    }

    private async ValueTask SendSlowFlushAsync(ValueTask<FlushResult> flushTask)
    {
        _isBackpressured = true;
        OnBackpressureDetected?.Invoke();
        try
        {
            await flushTask.ConfigureAwait(false);
        }
        finally
        {
            _isBackpressured = false;
            _writeLock.Release();
        }
    }

    private async ValueTask SendSlowLockAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        try
        {
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            _framer.WriteFrame(data, _transport.Output);

            ValueTask<FlushResult> flushTask = _transport.Output.FlushAsync(cancellationToken);
            if (!flushTask.IsCompletedSuccessfully)
            {
                _isBackpressured = true;
                OnBackpressureDetected?.Invoke();
                try
                {
                    await flushTask.ConfigureAwait(false);
                }
                finally
                {
                    _isBackpressured = false;
                }
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Retires the write lock once any in-flight send has finished.</summary>
    public async ValueTask DisposeAsync()
    {
        // Taking the lock first so a send that is still flushing completes against a live
        // semaphore instead of faulting on a disposed one.
        try
        {
            await _writeLock.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch
        {
            // ignored
        }

        _writeLock.Dispose();
    }
}