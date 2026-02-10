using System.Net;
using System.Net.Sockets;
using StormSocket.Core;
using StormSocket.Events;
using StormSocket.Framing;
using StormSocket.Middleware;
using StormSocket.Transport;

namespace StormSocket.Client;

/// <summary>
/// High-performance event-based TCP client. Supports optional SSL, message framing, and auto-reconnect.
/// <example>
/// <code>
/// var client = new StormTcpClient(new ClientOptions {
///     EndPoint = new IPEndPoint(IPAddress.Loopback, 5000),
///     Reconnect = new() { Enabled = true },
/// });
/// client.OnDataReceived += async data => Console.WriteLine($"Got {data.Length} bytes");
/// await client.ConnectAsync();
/// await client.SendAsync(Encoding.UTF8.GetBytes("Hello"));
/// </code>
/// </example>
/// </summary>
public class StormTcpClient : IAsyncDisposable
{
    private readonly ClientOptions _options;
    private readonly MiddlewarePipeline _pipeline = new();
    private ITransport? _transport;
    private PipeConnection? _connection;
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private bool _disposed;
    private volatile ConnectionState _state = ConnectionState.Closed;

    /// <summary>Tracks bytes sent/received and connection uptime.</summary>
    public ConnectionMetrics Metrics { get; private set; } = new();

    /// <summary>Current connection state.</summary>
    public ConnectionState State => _state;

    /// <summary>The remote server's endpoint.</summary>
    public EndPoint? RemoteEndPoint => _options.EndPoint;

    /// <summary>Fired when connection to the server is established.</summary>
    public event ClientConnectedHandler? OnConnected;

    /// <summary>Fired when disconnected from the server.</summary>
    public event ClientDisconnectedHandler? OnDisconnected;

    /// <summary>Fired when data (or a framed message) is received from the server.</summary>
    public event ClientDataReceivedHandler? OnDataReceived;

    /// <summary>Fired when an error occurs.</summary>
    public event ClientErrorHandler? OnError;

    /// <summary>Fired when attempting to reconnect.</summary>
    public event ClientReconnectingHandler? OnReconnecting;

    public StormTcpClient(ClientOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>Registers a middleware that intercepts connection lifecycle and data flow.</summary>
    public void UseMiddleware(IConnectionMiddleware middleware) => _pipeline.Use(middleware);

    /// <summary>Connects to the server. If auto-reconnect is enabled, reconnects on disconnect.</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        if (_options.Reconnect.Enabled)
        {
            TaskCompletionSource firstConnect = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _runTask = ReconnectLoopAsync(firstConnect, _cts.Token);
            await firstConnect.Task.ConfigureAwait(false);
        }
        else
        {
            await ConnectCoreAsync(_cts.Token).ConfigureAwait(false);
            _runTask = RunReceiveLoopAsync(_cts.Token);
        }
    }

    private async Task ConnectCoreAsync(CancellationToken ct)
    {
        _state = ConnectionState.Connecting;
        Metrics = new ConnectionMetrics();

        Socket socket = new Socket(_options.EndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

        if (_options.Socket.NoDelay)
        {
            socket.NoDelay = true;
        }

        if (_options.Socket.KeepAlive)
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        }

        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_options.ConnectTimeout);

        try
        {
            await socket.ConnectAsync(_options.EndPoint, timeoutCts.Token).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        ITransport transport;
        if (_options.Ssl is not null)
        {
            transport = new SslTransport(
                socket,
                _options.Ssl.TargetHost,
                _options.Ssl.Protocols,
                _options.Ssl.RemoteCertificateValidation,
                _options.Ssl.ClientCertificate);
        }
        else
        {
            transport = new TcpTransport(socket, _options.Socket.MaxPendingReceiveBytes, _options.Socket.MaxPendingSendBytes);
        }

        await transport.HandshakeAsync(ct).ConfigureAwait(false);

        IMessageFramer framer = _options.Framer ?? RawFramer.Instance;
        ClientSessionAdapter sessionAdapter = new ClientSessionAdapter(this);

        _connection = new PipeConnection(
            transport,
            framer,
            async data =>
            {
                Metrics.AddBytesReceived(data.Length);

                ReadOnlyMemory<byte> processed = await _pipeline.OnDataReceivedAsync(sessionAdapter, data).ConfigureAwait(false);
                if (processed.IsEmpty)
                {
                    return;
                }

                if (OnDataReceived is not null)
                {
                    await OnDataReceived.Invoke(processed).ConfigureAwait(false);
                }
            },
            async ex =>
            {
                if (OnError is not null)
                {
                    await OnError.Invoke(ex).ConfigureAwait(false);
                }
            });

        if (transport is TcpTransport tcp)
        {
            tcp.OnSocketError = error =>
            {
                OnError?.Invoke(new SocketException((int)error));
            };
        }

        _transport = transport;
        _state = ConnectionState.Connected;

        await _pipeline.OnConnectedAsync(sessionAdapter).ConfigureAwait(false);
        if (OnConnected is not null)
        {
            await OnConnected.Invoke().ConfigureAwait(false);
        }
    }

    private async Task RunReceiveLoopAsync(CancellationToken ct)
    {
        ClientSessionAdapter sessionAdapter = new ClientSessionAdapter(this);
        try
        {
            await _connection!.RunAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _pipeline.OnErrorAsync(sessionAdapter, ex).ConfigureAwait(false);
            if (OnError is not null)
            {
                await OnError.Invoke(ex).ConfigureAwait(false);
            }
        }
        finally
        {
            _state = ConnectionState.Closed;

            await _pipeline.OnDisconnectedAsync(sessionAdapter).ConfigureAwait(false);
            if (OnDisconnected is not null)
            {
                await OnDisconnected.Invoke().ConfigureAwait(false);
            }

            if (_transport is not null)
            {
                await _transport.DisposeAsync().ConfigureAwait(false);
                _transport = null;
            }

            _connection = null;
        }
    }

    private async Task ReconnectLoopAsync(TaskCompletionSource? firstConnect, CancellationToken ct)
    {
        int attempt = 0;
        bool isFirstConnect = true;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectCoreAsync(ct).ConfigureAwait(false);

                if (isFirstConnect)
                {
                    isFirstConnect = false;
                    firstConnect?.TrySetResult();
                }

                attempt = 0;
                await RunReceiveLoopAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                if (OnError is not null)
                {
                    await OnError.Invoke(ex).ConfigureAwait(false);
                }
            }

            if (ct.IsCancellationRequested)
            {
                break;
            }

            attempt++;
            if (_options.Reconnect.MaxAttempts > 0 && attempt > _options.Reconnect.MaxAttempts)
            {
                firstConnect?.TrySetException(new InvalidOperationException(
                    $"Max reconnect attempts ({_options.Reconnect.MaxAttempts}) exceeded."));
                break;
            }

            if (OnReconnecting is not null)
            {
                await OnReconnecting.Invoke(attempt, _options.Reconnect.Delay).ConfigureAwait(false);
            }

            try
            {
                await Task.Delay(_options.Reconnect.Delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Sends data to the server.</summary>
    public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (_state is not ConnectionState.Connected || _connection is null)
        {
            throw new InvalidOperationException("Client is not connected.");
        }

        ValueTask sendTask = _connection.SendAsync(data, cancellationToken);
        if (sendTask.IsCompletedSuccessfully)
        {
            Metrics.AddBytesSent(data.Length);
            return ValueTask.CompletedTask;
        }

        return SendAsyncSlow(sendTask, data.Length);
    }

    private async ValueTask SendAsyncSlow(ValueTask sendTask, int byteCount)
    {
        await sendTask.ConfigureAwait(false);
        Metrics.AddBytesSent(byteCount);
    }

    /// <summary>Gracefully disconnects from the server.</summary>
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_state is ConnectionState.Closing or ConnectionState.Closed)
        {
            return;
        }

        _state = ConnectionState.Closing;

        if (_cts is not null)
        {
#if NET8_0_OR_GREATER
            await _cts.CancelAsync().ConfigureAwait(false);
#else
            _cts.Cancel();
#endif
        }

        if (_runTask is not null)
        {
            try
            {
                await _runTask.ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        GC.SuppressFinalize(this);

        await DisconnectAsync().ConfigureAwait(false);
        _cts?.Dispose();
    }
}