using System.Buffers;
using System.IO.Pipelines;
using System.Threading.Tasks;
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

    public async ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        _framer.WriteFrame(data, _transport.Output);
        await _transport.Output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}