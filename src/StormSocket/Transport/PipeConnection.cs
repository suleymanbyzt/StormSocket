using System.Buffers;
using System.IO.Pipelines;
using StormSocket.Framing;

namespace StormSocket.Transport;

/// <summary>
/// Reads from transport input using a framer, dispatches complete messages via callback.
/// </summary>
public sealed class PipeConnection
{
    private readonly ITransport _transport;
    private readonly IMessageFramer _framer;
    private readonly Func<ReadOnlyMemory<byte>, ValueTask> _onMessage;
    private readonly Func<Exception, ValueTask>? _onError;
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

    public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        _framer.WriteFrame(data, _transport.Output);

        ValueTask<FlushResult> flushTask = _transport.Output.FlushAsync(cancellationToken);
        if (flushTask.IsCompletedSuccessfully)
        {
            return ValueTask.CompletedTask;
        }

        return SendAsyncSlow(flushTask);
        
    }

    private async ValueTask SendAsyncSlow(ValueTask<FlushResult> flushTask)
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