using System.Buffers;
using System.IO.Pipelines;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace StormSocket.Transport;

/// <summary>
/// Decorates a raw socket with SSL/TLS using SslStream, then exposes Pipe-based I/O.
/// Supports both server-mode (AuthenticateAsServer) and client-mode (AuthenticateAsClient).
/// </summary>
public sealed class SslTransport : ITransport
{
    private readonly Socket _socket;
    private readonly X509Certificate2? _certificate;
    private readonly SslProtocols _protocols;
    private readonly bool _clientCertificateRequired;
    private readonly bool _isClientMode;
    private readonly string? _targetHost;
    private readonly RemoteCertificateValidationCallback? _remoteCertValidator;
    private NetworkStream? _networkStream;
    private SslStream? _sslStream;
    private readonly Pipe _receivePipe;
    private readonly Pipe _sendPipe;
    private readonly CancellationTokenSource _cts = new();
    private Task? _receiveTask;
    private Task? _sendTask;
    private int _disposed;

    /// <summary>Matches SocketTuningOptions.MaxPendingSendBytes / MaxPendingReceiveBytes.</summary>
    private const long DefaultMaxPendingBytes = 1024 * 1024;

    /// <summary>How long a close waits for queued data to reach the peer before giving up on it.</summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    public PipeReader Input => _receivePipe.Reader;
    public PipeWriter Output => _sendPipe.Writer;

    /// <summary>
    /// Creates a server-mode SSL transport that authenticates as a server using the provided certificate.
    /// </summary>
    /// <param name="maxPendingReceiveBytes">Bytes received but not yet processed before reads pause. 0 uses the pipe default.</param>
    /// <param name="maxPendingSendBytes">Bytes waiting to be sent before backpressure kicks in. 0 uses the pipe default.</param>
    /// <param name="receiveOptions">Overrides <paramref name="maxPendingReceiveBytes"/> when supplied.</param>
    /// <param name="sendOptions">Overrides <paramref name="maxPendingSendBytes"/> when supplied.</param>
    public SslTransport(
        Socket socket,
        X509Certificate2 certificate,
        SslProtocols protocols = SslProtocols.None,
        bool clientCertificateRequired = false,
        long maxPendingReceiveBytes = DefaultMaxPendingBytes,
        long maxPendingSendBytes = DefaultMaxPendingBytes,
        PipeOptions? receiveOptions = null,
        PipeOptions? sendOptions = null)
    {
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        _certificate = certificate ?? throw new ArgumentNullException(nameof(certificate));
        _protocols = protocols;
        _clientCertificateRequired = clientCertificateRequired;
        _isClientMode = false;
        _receivePipe = new Pipe(receiveOptions ?? CreatePipeOptions(maxPendingReceiveBytes));
        _sendPipe = new Pipe(sendOptions ?? CreatePipeOptions(maxPendingSendBytes));
    }

    /// <summary>
    /// Creates a client-mode SSL transport that authenticates as a client to the specified host.
    /// </summary>
    /// <param name="maxPendingReceiveBytes">Bytes received but not yet processed before reads pause. 0 uses the pipe default.</param>
    /// <param name="maxPendingSendBytes">Bytes waiting to be sent before backpressure kicks in. 0 uses the pipe default.</param>
    /// <param name="receiveOptions">Overrides <paramref name="maxPendingReceiveBytes"/> when supplied.</param>
    /// <param name="sendOptions">Overrides <paramref name="maxPendingSendBytes"/> when supplied.</param>
    public SslTransport(
        Socket socket,
        string targetHost,
        SslProtocols protocols = SslProtocols.None,
        RemoteCertificateValidationCallback? remoteCertificateValidation = null,
        X509Certificate2? clientCertificate = null,
        long maxPendingReceiveBytes = DefaultMaxPendingBytes,
        long maxPendingSendBytes = DefaultMaxPendingBytes,
        PipeOptions? receiveOptions = null,
        PipeOptions? sendOptions = null)
    {
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        _targetHost = targetHost ?? throw new ArgumentNullException(nameof(targetHost));
        _protocols = protocols;
        _isClientMode = true;
        _remoteCertValidator = remoteCertificateValidation;
        _certificate = clientCertificate;
        _receivePipe = new Pipe(receiveOptions ?? CreatePipeOptions(maxPendingReceiveBytes));
        _sendPipe = new Pipe(sendOptions ?? CreatePipeOptions(maxPendingSendBytes));
    }

    private static PipeOptions CreatePipeOptions(long maxPendingBytes)
        => maxPendingBytes > 0
            ? new PipeOptions(pauseWriterThreshold: maxPendingBytes, resumeWriterThreshold: maxPendingBytes / 2)
            : PipeOptions.Default;

    public async ValueTask HandshakeAsync(CancellationToken cancellationToken = default)
    {
        _networkStream = new NetworkStream(_socket, ownsSocket: false);
        _sslStream = new SslStream(_networkStream, leaveInnerStreamOpen: false, _remoteCertValidator);

        if (_isClientMode)
        {
            SslClientAuthenticationOptions clientOptions = new SslClientAuthenticationOptions
            {
                TargetHost = _targetHost,
                EnabledSslProtocols = _protocols,
            };
            if (_certificate is not null)
            {
                clientOptions.ClientCertificates = new X509CertificateCollection { _certificate };
            }

            await _sslStream.AuthenticateAsClientAsync(clientOptions, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _sslStream.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = _certificate,
                    EnabledSslProtocols = _protocols,
                    ClientCertificateRequired = _clientCertificateRequired,
                },
                cancellationToken).ConfigureAwait(false);
        }

        _receiveTask = ReceiveLoopAsync(_cts.Token);
        _sendTask = SendLoopAsync(_cts.Token);
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
                int bytesRead = await _sslStream!.ReadAsync(memory, ct).ConfigureAwait(false);
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
        catch (IOException ex)
        {
            // A TLS stream that dies mid-read is a fault the consumer must see; a peer that simply
            // went away is a clean end of stream, as it is on a plain socket.
            if (ex.InnerException is SocketException socketEx && !IsExpectedDisconnect(socketEx.SocketErrorCode))
            {
                error = ex;
            }
        }
        finally
        {
            await writer.CompleteAsync(error).ConfigureAwait(false);
        }
    }

    private static bool IsExpectedDisconnect(SocketError error)
        => error is SocketError.ConnectionAborted
            or SocketError.ConnectionRefused
            or SocketError.ConnectionReset
            or SocketError.OperationAborted
            or SocketError.Shutdown;

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
                {
                    break;
                }

                foreach (ReadOnlyMemory<byte> segment in buffer)
                {
                    await _sslStream!.WriteAsync(segment, ct).ConfigureAwait(false);
                }

                reader.AdvanceTo(buffer.End);

                if (result.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        finally
        {
            await reader.CompleteAsync().ConfigureAwait(false);
        }
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

        // Shut the socket down before waiting for the receive loop, not after: cancelling the token
        // does not interrupt a receive that is already in flight, so waiting first would hang for as
        // long as a connected, silent peer stays quiet.
        try
        {
            _socket.Shutdown(SocketShutdown.Both);
        }
        catch
        {
            // ignored
        }

        if (_receiveTask is not null)
        {
            try
            {
                await _receiveTask.WaitAsync(DrainTimeout).ConfigureAwait(false);
            }
            catch
            {
                // Timed out or faulted — the socket is going away regardless.
            }
        }

        if (_sslStream is not null)
        {
            await _sslStream.DisposeAsync().ConfigureAwait(false);
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