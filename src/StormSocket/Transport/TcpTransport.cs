using System.Buffers;
using System.IO.Pipelines;
using System.Net.Sockets;

namespace StormSocket.Transport;

public sealed class TcpTransport : ITransport
{
    private readonly Socket _socket;
    private readonly Pipe _receivePipe;
    private readonly Pipe _sendPipe;
    private readonly CancellationTokenSource _cts = new();
    private Task? _receiveTask;
    private Task? _sendTask;
    private bool _disposed;

    public PipeReader Input => _receivePipe.Reader;
    public PipeWriter Output => _sendPipe.Writer;

    /// <summary>
    /// Fired when a socket-level error occurs. Null means the error is silently handled.
    /// </summary>
    public Action<SocketError>? OnSocketError { get; set; }

    public TcpTransport(Socket socket, long maxReceiveBuffer = 0, long maxSendBuffer = 0)
    {
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));

        PipeOptions receiveOptions = maxReceiveBuffer > 0
            ? new PipeOptions(pauseWriterThreshold: maxReceiveBuffer, resumeWriterThreshold: maxReceiveBuffer / 2)
            : PipeOptions.Default;

        PipeOptions sendOptions = maxSendBuffer > 0
            ? new PipeOptions(pauseWriterThreshold: maxSendBuffer, resumeWriterThreshold: maxSendBuffer / 2)
            : PipeOptions.Default;

        _receivePipe = new Pipe(receiveOptions);
        _sendPipe = new Pipe(sendOptions);
    }

    public ValueTask HandshakeAsync(CancellationToken cancellationToken = default)
    {
        _receiveTask = ReceiveLoopAsync(_cts.Token);
        _sendTask = SendLoopAsync(_cts.Token);
        return ValueTask.CompletedTask;
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        PipeWriter writer = _receivePipe.Writer;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Memory<byte> memory = writer.GetMemory(4096);
                int bytesRead = await _socket.ReceiveAsync(memory, SocketFlags.None, ct).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                writer.Advance(bytesRead);
                FlushResult result = await writer.FlushAsync(ct).ConfigureAwait(false);
                if (result.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException ex)
        {
            HandleSocketError(ex.SocketErrorCode);
        }
        catch (ObjectDisposedException) { }
        finally
        {
            await writer.CompleteAsync().ConfigureAwait(false);
        }
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        PipeReader reader = _sendPipe.Reader;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                ReadResult result = await reader.ReadAsync(ct).ConfigureAwait(false);
                ReadOnlySequence<byte> buffer = result.Buffer;

                if (buffer.IsEmpty && result.IsCompleted)
                    break;

                foreach (ReadOnlyMemory<byte> segment in buffer)
                {
                    await _socket.SendAsync(segment, SocketFlags.None, ct).ConfigureAwait(false);
                }

                reader.AdvanceTo(buffer.End);

                if (result.IsCompleted)
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException ex)
        {
            HandleSocketError(ex.SocketErrorCode);
        }
        catch (ObjectDisposedException) { }
        finally
        {
            await reader.CompleteAsync().ConfigureAwait(false);
        }
    }

    private void HandleSocketError(SocketError error)
    {
        // these errors usually indicate an expected or graceful disconnect.
        // skipping them for now. this logic might evolve once the edge cases
        // become annoying enough.
        if (error is SocketError.ConnectionAborted
            or SocketError.ConnectionRefused
            or SocketError.ConnectionReset
            or SocketError.OperationAborted
            or SocketError.Shutdown)
        {
            return;
        }

        OnSocketError?.Invoke(error);
    }

    public async ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        await _sendPipe.Writer.CompleteAsync().ConfigureAwait(false);

#if NET8_0_OR_GREATER
        await _cts.CancelAsync().ConfigureAwait(false);
#else
        _cts.Cancel();
#endif

        if (_receiveTask is not null)
        {
            await _receiveTask.ConfigureAwait(false);
        }

        if (_sendTask is not null)
        {
            await _sendTask.ConfigureAwait(false);
        }

        try
        {
            _socket.Shutdown(SocketShutdown.Both);
        }
        catch
        {
            // ignored
        }
        _socket.Close();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        
        _disposed = true;

        await CloseAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}