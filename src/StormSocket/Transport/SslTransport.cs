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
    private bool _disposed;

    public PipeReader Input => _receivePipe.Reader;
    public PipeWriter Output => _sendPipe.Writer;

    /// <summary>
    /// Creates a server-mode SSL transport that authenticates as a server using the provided certificate.
    /// </summary>
    public SslTransport(
        Socket socket,
        X509Certificate2 certificate,
        SslProtocols protocols = SslProtocols.None,
        bool clientCertificateRequired = false,
        PipeOptions? receiveOptions = null,
        PipeOptions? sendOptions = null)
    {
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        _certificate = certificate ?? throw new ArgumentNullException(nameof(certificate));
        _protocols = protocols;
        _clientCertificateRequired = clientCertificateRequired;
        _isClientMode = false;
        _receivePipe = new Pipe(receiveOptions ?? PipeOptions.Default);
        _sendPipe = new Pipe(sendOptions ?? PipeOptions.Default);
    }

    /// <summary>
    /// Creates a client-mode SSL transport that authenticates as a client to the specified host.
    /// </summary>
    public SslTransport(
        Socket socket,
        string targetHost,
        SslProtocols protocols = SslProtocols.None,
        RemoteCertificateValidationCallback? remoteCertificateValidation = null,
        X509Certificate2? clientCertificate = null,
        PipeOptions? receiveOptions = null,
        PipeOptions? sendOptions = null)
    {
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        _targetHost = targetHost ?? throw new ArgumentNullException(nameof(targetHost));
        _protocols = protocols;
        _isClientMode = true;
        _remoteCertValidator = remoteCertificateValidation;
        _certificate = clientCertificate;
        _receivePipe = new Pipe(receiveOptions ?? PipeOptions.Default);
        _sendPipe = new Pipe(sendOptions ?? PipeOptions.Default);
    }

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
        catch (IOException) { }
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

        if (_sslStream is not null)
        {
            await _sslStream.DisposeAsync().ConfigureAwait(false);
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