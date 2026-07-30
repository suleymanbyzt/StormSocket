using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    private readonly ILogger _logger;
    private readonly MiddlewarePipeline _pipeline = new();

    private readonly AsyncEventSource<ClientConnectedHandler> _onConnected = new();
    private readonly AsyncEventSource<ClientDisconnectedHandler> _onDisconnected = new();
    private readonly AsyncEventSource<ClientDataReceivedHandler> _onDataReceived = new();
    private readonly AsyncEventSource<ClientErrorHandler> _onError = new();
    private readonly AsyncEventSource<ClientReconnectingHandler> _onReconnecting = new();

    /// <summary>
    /// True for the whole async flow of the receive loop, including the middleware and handlers it
    /// invokes. A disconnect raised from inside that flow must not await the loop it is running on.
    /// </summary>
    private readonly AsyncLocal<bool> _onRunLoop = new();

    private ITransport? _transport;
    private PipeConnection? _connection;
    private ClientSessionAdapter? _session;
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private bool _disposed;
    private volatile ConnectionState _state = ConnectionState.Closed;
    private volatile DisconnectReason _disconnectReason;

    /// <summary>Tracks bytes sent/received and connection uptime.</summary>
    public ConnectionMetrics Metrics { get; private set; } = new();

    /// <summary>Current connection state.</summary>
    public ConnectionState State => _state;

    /// <summary>The reason the last connection was closed.</summary>
    internal DisconnectReason DisconnectReason => _disconnectReason;

    /// <summary>The remote server's endpoint.</summary>
    public EndPoint? RemoteEndPoint => _options.EndPoint;

    /// <summary>Fired when connection to the server is established.</summary>
    public event ClientConnectedHandler? OnConnected
    {
        add => _onConnected.Add(value);
        remove => _onConnected.Remove(value);
    }

    /// <summary>Fired when disconnected from the server.</summary>
    public event ClientDisconnectedHandler? OnDisconnected
    {
        add => _onDisconnected.Add(value);
        remove => _onDisconnected.Remove(value);
    }

    /// <summary>Fired when data (or a framed message) is received from the server.</summary>
    public event ClientDataReceivedHandler? OnDataReceived
    {
        add => _onDataReceived.Add(value);
        remove => _onDataReceived.Remove(value);
    }

    /// <summary>Fired when an error occurs.</summary>
    public event ClientErrorHandler? OnError
    {
        add => _onError.Add(value);
        remove => _onError.Remove(value);
    }

    /// <summary>Fired when attempting to reconnect.</summary>
    public event ClientReconnectingHandler? OnReconnecting
    {
        add => _onReconnecting.Add(value);
        remove => _onReconnecting.Remove(value);
    }

    public StormTcpClient(ClientOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = (options.LoggerFactory ?? NullLoggerFactory.Instance).CreateLogger<StormTcpClient>();
    }

    /// <summary>Registers a middleware that intercepts connection lifecycle and data flow.</summary>
    public void UseMiddleware(IConnectionMiddleware middleware) => _pipeline.Use(middleware);

    /// <summary>
    /// Invokes every OnError subscriber in registration order. Never throws: a handler that fails is
    /// logged, because this also runs on paths that are already tearing the connection down.
    /// </summary>
    private async ValueTask RaiseErrorAsync(Exception exception)
    {
        foreach (ClientErrorHandler handler in _onError.Handlers)
        {
            try
            {
                await handler(exception).ConfigureAwait(false);
            }
            catch (Exception handlerEx)
            {
                _logger.LogError(handlerEx, "Unhandled exception in OnError handler");
            }
        }
    }

    /// <summary>Connects to the server. If auto-reconnect is enabled, reconnects on disconnect.</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        // Checked here rather than deeper in, so a misconfiguration is reported by the call the
        // application made instead of surfacing as a failure inside the handshake.
        _options.Validate();

        // A previous source still holds a registration on the token it was linked to.
        _cts?.Dispose();
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
        _logger.LogInformation("Connecting to {EndPoint}", _options.EndPoint);
        _state = ConnectionState.Connecting;
        _disconnectReason = DisconnectReason.None;
        Metrics = new ConnectionMetrics();

        bool isUnix = _options.EndPoint.AddressFamily == AddressFamily.Unix;
        Socket socket = isUnix
            ? new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
            : new Socket(_options.EndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

        if (!isUnix && _options.Socket.NoDelay)
        {
            socket.NoDelay = true;
        }

        _options.Socket.ApplyKeepAlive(socket);

        // The whole sequence runs on this budget: a server that completes TCP and then stalls in TLS
        // would otherwise keep ConnectAsync pending forever.
        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_options.ConnectTimeout);

        try
        {
            await socket.ConnectAsync(_options.EndPoint, timeoutCts.Token).ConfigureAwait(false);
        }
        catch
        {
            _state = ConnectionState.Closed;
            socket.Dispose();
            throw;
        }

        ITransport transport;
        if (_options.Ssl is not null)
        {
            transport = new SslTransport(
                socket,
                ClientSslOptions.ResolveTargetHost(_options.Ssl, ResolveHost(_options.EndPoint)),
                _options.Ssl.Protocols,
                _options.Ssl.ResolveValidationCallback(),
                _options.Ssl.ClientCertificate,
                _options.Socket.MaxPendingReceiveBytes,
                _options.Socket.MaxPendingSendBytes);
        }
        else
        {
            transport = new TcpTransport(socket, _options.Socket.MaxPendingReceiveBytes, _options.Socket.MaxPendingSendBytes);
        }

        try
        {
            await transport.HandshakeAsync(timeoutCts.Token).ConfigureAwait(false);

            IMessageFramer framer = _options.Framer ?? RawFramer.Instance;

            // One adapter per connection: middleware that stores per-session state in OnConnected
            // must see the same Items dictionary again in OnDataReceived/OnDisconnected/OnError.
            ClientSessionAdapter sessionAdapter = new ClientSessionAdapter(this);
            _session = sessionAdapter;

            _connection = new PipeConnection(
                transport,
                framer,
                async data =>
                {
                    Metrics.AddBytesReceived(data.Length);

                    // An empty result means a middleware suppressed the data; an empty frame is not
                    // the same thing and must still reach the application.
                    ReadOnlyMemory<byte> processed = data;
                    if (_pipeline.HasMiddleware)
                    {
                        processed = await _pipeline.OnDataReceivedAsync(sessionAdapter, data).ConfigureAwait(false);
                        if (processed.IsEmpty && !data.IsEmpty)
                        {
                            return;
                        }
                    }

                    // A throwing handler must not take the read loop down with it.
                    foreach (ClientDataReceivedHandler handler in _onDataReceived.Handlers)
                    {
                        try
                        {
                            await handler(processed).ConfigureAwait(false);
                        }
                        catch (Exception handlerEx)
                        {
                            _logger.LogError(handlerEx, "Unhandled exception in OnDataReceived handler");
                        }
                    }
                },
                async ex => await RaiseErrorAsync(ex).ConfigureAwait(false));

            if (transport is TcpTransport tcp)
            {
                // The transport callback is synchronous, so the handlers run detached.
                // RaiseErrorAsync swallows and logs handler failures, so nothing can fault here.
                tcp.OnSocketError = error => _ = RaiseErrorAsync(new SocketException((int)error)).AsTask();
            }

            _transport = transport;
            _state = ConnectionState.Connected;
            _logger.LogInformation("Connected to {EndPoint}", _options.EndPoint);

            await _pipeline.OnConnectedAsync(sessionAdapter).ConfigureAwait(false);
            foreach (ClientConnectedHandler handler in _onConnected.Handlers)
            {
                try
                {
                    await handler().ConfigureAwait(false);
                }
                catch (Exception handlerEx)
                {
                    _logger.LogError(handlerEx, "Unhandled exception in OnConnected handler");
                }
            }
        }
        catch
        {
            // From construction on the transport owns the socket, two pipes and (after the handshake)
            // two I/O loop tasks. Nothing else ever releases them if a later step fails, so with
            // reconnect enabled every failed attempt would leak one socket and two orphan tasks.
            _state = ConnectionState.Closed;
            _transport = null;
            _session = null;
            _connection = null;
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The SNI/verification host for an endpoint whose SSL options left TargetHost unset.</summary>
    private static string ResolveHost(EndPoint endPoint) => endPoint switch
    {
        DnsEndPoint dns => dns.Host,
        IPEndPoint ip => ip.Address.ToString(),
        _ => endPoint.ToString() ?? string.Empty,
    };

    private async Task RunReceiveLoopAsync(CancellationToken ct)
    {
        _onRunLoop.Value = true;
        ClientSessionAdapter sessionAdapter = _session ??= new ClientSessionAdapter(this);
        try
        {
            await _connection!.RunAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_disconnectReason == DisconnectReason.None)
                _disconnectReason = DisconnectReason.TransportError;
            _logger.LogError(ex, "Transport error");

            try
            {
                await _pipeline.OnErrorAsync(sessionAdapter, ex).ConfigureAwait(false);
            }
            catch (Exception mwEx)
            {
                _logger.LogError(mwEx, "Middleware OnError exception");
            }

            await RaiseErrorAsync(ex).ConfigureAwait(false);
        }
        finally
        {
            // Default: if no specific reason was set, the server closed the connection
            if (_disconnectReason == DisconnectReason.None)
                _disconnectReason = DisconnectReason.ClosedByServer;

            _state = ConnectionState.Closed;

            DisconnectReason reason = _disconnectReason;
            _logger.LogInformation("Disconnected: {Reason}", reason);

            // Every user callback below is wrapped: an exception thrown out of this finally would
            // skip the transport disposal underneath it and leak the socket on every reconnect cycle.
            try
            {
                await _pipeline.OnDisconnectedAsync(sessionAdapter, reason).ConfigureAwait(false);
            }
            catch (Exception mwEx)
            {
                _logger.LogError(mwEx, "Middleware OnDisconnected exception");
            }

            foreach (ClientDisconnectedHandler handler in _onDisconnected.Handlers)
            {
                try
                {
                    await handler(reason).ConfigureAwait(false);
                }
                catch (Exception handlerEx)
                {
                    _logger.LogError(handlerEx, "Unhandled exception in OnDisconnected handler");
                }
            }

            ITransport? transport = _transport;
            _transport = null;
            if (transport is not null)
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }

            _connection = null;
        }
    }

    /// <remarks>
    /// Never throws and never faults its task: nobody observes it, so a fault here would surface as an
    /// unobserved task exception. Failures reach the caller through <paramref name="firstConnect"/>
    /// and every subscriber through OnError.
    /// </remarks>
    private async Task ReconnectLoopAsync(TaskCompletionSource? firstConnect, CancellationToken ct)
    {
        int attempt = 0;
        bool isFirstConnect = true;
        Exception? lastError = null;

        try
        {
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
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    await RaiseErrorAsync(ex).ConfigureAwait(false);
                }

                if (ct.IsCancellationRequested)
                {
                    break;
                }

                attempt++;
                if (_options.Reconnect.MaxAttempts > 0 && attempt > _options.Reconnect.MaxAttempts)
                {
                    _logger.LogWarning("Max reconnect attempts ({MaxAttempts}) reached", _options.Reconnect.MaxAttempts);
                    firstConnect?.TrySetException(new InvalidOperationException(
                        $"Max reconnect attempts ({_options.Reconnect.MaxAttempts}) exceeded.", lastError));
                    break;
                }

                _logger.LogDebug("Reconnect attempt {Attempt} in {Delay}", attempt, _options.Reconnect.Delay);
                foreach (ClientReconnectingHandler handler in _onReconnecting.Handlers)
                {
                    try
                    {
                        await handler(attempt, _options.Reconnect.Delay).ConfigureAwait(false);
                    }
                    catch (Exception handlerEx)
                    {
                        _logger.LogError(handlerEx, "Unhandled exception in OnReconnecting handler");
                    }
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reconnect loop terminated");
            lastError = ex;
        }
        finally
        {
            // ConnectAsync awaits this promise; every exit that never reported a first connect has to
            // resolve it or the caller waits forever. Already-resolved promises ignore these calls.
            if (ct.IsCancellationRequested)
            {
                firstConnect?.TrySetCanceled(ct);
            }
            else if (lastError is not null)
            {
                firstConnect?.TrySetException(lastError);
            }
            else
            {
                firstConnect?.TrySetException(new InvalidOperationException(
                    "The reconnect loop stopped before the first connection was established."));
            }
        }
    }

    /// <summary>Sends data to the server.</summary>
    public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        PipeConnection? connection = _connection;
        if (_state is not ConnectionState.Connected || connection is null)
        {
            throw new InvalidOperationException("Client is not connected.");
        }

        ValueTask sendTask = connection.SendAsync(data, cancellationToken);
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
        if (_state is ConnectionState.Connected)
        {
            _state = ConnectionState.Closing;
        }

        if (_cts is not null)
        {
#if NET8_0_OR_GREATER
            await _cts.CancelAsync().ConfigureAwait(false);
#else
            _cts.Cancel();
#endif
        }

        // Awaited even when the connection was already closing: the run loop owns the transport, so
        // returning before it finishes would report a clean shutdown over a still-live connection.
        // Skipped when this call came out of the run loop itself, which would be a wait on itself.
        if (!_onRunLoop.Value && _runTask is Task runTask)
        {
            try
            {
                await runTask.ConfigureAwait(false);
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

        // A connection that never reached the run loop (connect cancelled mid-handshake) has nobody
        // else to release its transport.
        ITransport? transport = _transport;
        _transport = null;
        if (transport is not null)
        {
            await transport.DisposeAsync().ConfigureAwait(false);
        }

        _cts?.Dispose();
    }
}
