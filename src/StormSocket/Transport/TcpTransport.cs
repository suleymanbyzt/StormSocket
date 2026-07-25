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
    private int _disposed;

    /// <summary>How long a close waits for queued data to reach the peer before giving up on it.</summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

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
        Exception? error = null;
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
            // A genuine fault is handed to the reader so the consumer sees a broken connection
            // instead of a clean end of stream; an expected disconnect stays a clean end of stream.
            if (HandleSocketError(ex.SocketErrorCode))
            {
                error = ex;
            }
        }
        catch (ObjectDisposedException) { }
        finally
        {
            await writer.CompleteAsync(error).ConfigureAwait(false);
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

    /// <returns>True when the error is a real fault, false when it is an expected disconnect.</returns>
    private bool HandleSocketError(SocketError error)
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
            return false;
        }

        OnSocketError?.Invoke(error);
        return true;
    }

    public async ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        // Closing a transport that is already disposed is a normal race, not a caller error: a
        // session teardown and an application-initiated close can reach here from two directions.
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        await CloseCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Shuts the connection down. Called by <see cref="CloseAsync"/> and by the dispose path, which
    /// has already marked the transport disposed and must not be turned away by that guard.
    /// </summary>
    private async ValueTask CloseCoreAsync(CancellationToken cancellationToken)
    {
        await _sendPipe.Writer.CompleteAsync().ConfigureAwait(false);

        // Completing the writer makes the send loop flush what is still queued and then exit by
        // itself. Cancelling before that would abandon everything the pipe is holding, so the loop
        // gets a bounded window to reach the peer; a peer that stopped reading cannot stall
        // shutdown beyond it.
        if (_sendTask is not null)
        {
            try
            {
                await _sendTask.WaitAsync(DrainTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Timed out, cancelled, or the loop already faulted — nothing left to drain.
            }
        }

        try
        {
#if NET8_0_OR_GREATER
            await _cts.CancelAsync().ConfigureAwait(false);
#else
            _cts.Cancel();
#endif
        }
        catch (ObjectDisposedException)
        {
            // Disposed while draining — the loops are already being torn down.
            return;
        }

        if (_receiveTask is not null)
        {
            await _receiveTask.ConfigureAwait(false);
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await CloseCoreAsync(CancellationToken.None).ConfigureAwait(false);

        // A Pipe only returns its rented segments once both ends are completed, and the receive
        // loop only ever completes the writer. Consumers are done by the time the transport is
        // disposed, so this is the first point where completing the reader cannot pull buffers
        // out from under an in-flight read.
        await _receivePipe.Reader.CompleteAsync().ConfigureAwait(false);
        await _receivePipe.Writer.CompleteAsync().ConfigureAwait(false);

        _cts.Dispose();
    }
}