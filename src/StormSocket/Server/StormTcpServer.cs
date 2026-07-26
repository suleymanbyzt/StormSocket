using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StormSocket.Core;
using StormSocket.Events;
using StormSocket.Framing;
using StormSocket.Middleware;
using StormSocket.Session;
using StormSocket.Transport;

namespace StormSocket.Server;

/// <summary>
/// High-performance event-based TCP server. Supports optional SSL and message framing.
/// <example>
/// <code>
/// var server = new StormTcpServer(new ServerOptions { EndPoint = new IPEndPoint(IPAddress.Any, 5000) });
/// server.OnDataReceived += async (session, data) => await session.SendAsync(data); // echo
/// await server.StartAsync();
/// </code>
/// </example>
/// </summary>
public class StormTcpServer : IAsyncDisposable
{
    private readonly ServerOptions _options;
    private readonly ILogger _logger;
    private Socket? _listenSocket;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private int _running;
    private readonly ConnectionTracker _connections = new();
    private readonly MiddlewarePipeline _pipeline = new();

    private readonly AsyncEventSource<SessionConnectedHandler> _onConnected = new();
    private readonly AsyncEventSource<SessionDisconnectedHandler> _onDisconnected = new();
    private readonly AsyncEventSource<DataReceivedHandler> _onDataReceived = new();
    private readonly AsyncEventSource<ErrorHandler> _onError = new();
    private readonly ConnectionGate _connectionGate;
    private bool _disposed;

    /// <summary>Server-wide aggregate metrics (connections, messages, bytes, errors).</summary>
    public ServerMetrics Metrics { get; } = new();

    /// <summary>
    /// The endpoint the listener is actually bound to, available once <c>StartAsync</c> has returned.
    /// Bind to port 0 and read this to discover the port the OS assigned.
    /// </summary>
    public EndPoint? LocalEndPoint => _listenSocket?.LocalEndPoint;

    /// <summary>
    /// True between a successful <see cref="StartAsync"/> and <see cref="StopAsync"/>. A start that
    /// fails to bind leaves this false, so a host observes the failure instead of a dead server.
    /// </summary>
    public bool IsRunning => Volatile.Read(ref _running) is 1;

    /// <summary>All currently connected sessions, keyed by ID.</summary>
    public SessionManager Sessions { get; } = new();

    /// <summary>Manages named groups for targeted broadcast.</summary>
    public NetworkSessionGroup Groups { get; } = new();

    /// <summary>
    /// Fired when a new client connects and handshake (SSL if configured) completes.
    /// <para><b>Signature:</b> <c>async (ISession session) => { }</c></para>
    /// <example>
    /// <code>
    /// server.OnConnected += async (session) =>
    /// {
    ///     Console.WriteLine($"#{session.Id} connected from {session.RemoteEndPoint}");
    /// };
    /// </code>
    /// </example>
    /// </summary>
    public event SessionConnectedHandler? OnConnected
    {
        add => _onConnected.Add(value);
        remove => _onConnected.Remove(value);
    }

    /// <summary>
    /// Fired when a client disconnects (gracefully or not).
    /// <para><b>Signature:</b> <c>async (ISession session, DisconnectReason reason) => { }</c></para>
    /// <example>
    /// <code>
    /// server.OnDisconnected += async (session, reason) =>
    /// {
    ///     Console.WriteLine($"#{session.Id} disconnected ({reason}) — sent: {session.Metrics.BytesSent}, recv: {session.Metrics.BytesReceived}");
    /// };
    /// </code>
    /// </example>
    /// </summary>
    public event SessionDisconnectedHandler? OnDisconnected
    {
        add => _onDisconnected.Add(value);
        remove => _onDisconnected.Remove(value);
    }

    /// <summary>
    /// Fired when data (or a framed message) is received from a client.
    /// If a <see cref="ServerOptions.Framer"/> is configured, each invocation contains one complete message.
    /// <para><b>Signature:</b> <c>async (ISession session, ReadOnlyMemory&lt;byte&gt; data) => { }</c></para>
    /// <example>
    /// <code>
    /// server.OnDataReceived += async (session, data) =>
    /// {
    ///     Console.WriteLine($"#{session.Id}: {data.Length} bytes");
    ///     await session.SendAsync(data); // echo
    /// };
    /// </code>
    /// </example>
    /// </summary>
    public event DataReceivedHandler? OnDataReceived
    {
        add => _onDataReceived.Add(value);
        remove => _onDataReceived.Remove(value);
    }

    /// <summary>
    /// Fired when an error occurs during connection handling.
    /// Session may be null if the error occurs before session creation.
    /// <para><b>Signature:</b> <c>async (ISession? session, Exception ex) => { }</c></para>
    /// <example>
    /// <code>
    /// server.OnError += async (session, ex) =>
    /// {
    ///     Console.WriteLine($"Error on #{session?.Id}: {ex.Message}");
    /// };
    /// </code>
    /// </example>
    /// </summary>
    public event ErrorHandler? OnError
    {
        add => _onError.Add(value);
        remove => _onError.Remove(value);
    }

    public StormTcpServer(ServerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = (options.LoggerFactory ?? NullLoggerFactory.Instance).CreateLogger<StormTcpServer>();
        _connectionGate = new ConnectionGate(options.MaxConnections, options.MaxConnectionsPerIp);
    }

    /// <summary>Registers a middleware that intercepts connection lifecycle and data flow.</summary>
    public void UseMiddleware(IConnectionMiddleware middleware) => _pipeline.Use(middleware);

    /// <summary>
    /// Invokes every OnError subscriber in registration order. Never throws: a handler that fails is
    /// logged, because this also runs on paths that are already tearing a connection down.
    /// </summary>
    private async ValueTask RaiseErrorAsync(ISession? session, Exception exception)
    {
        foreach (ErrorHandler handler in _onError.Handlers)
        {
            try
            {
                await handler(session, exception).ConfigureAwait(false);
            }
            catch (Exception handlerEx)
            {
                _logger.LogError(handlerEx, "Unhandled exception in OnError handler");
            }
        }
    }

    /// <summary>Binds to the configured endpoint and starts accepting connections.</summary>
    /// <exception cref="ArgumentException">The options describe a configuration the server cannot use.</exception>
    /// <exception cref="InvalidOperationException">The server is already running.</exception>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _options.Validate();

        if (_options.WebSocket is not null)
        {
            _logger.LogWarning(
                "ServerOptions.WebSocket is set but StormTcpServer ignores it — use StormWebSocketServer to serve WebSocket connections");
        }

        if (Interlocked.CompareExchange(ref _running, 1, 0) is not 0)
        {
            throw new InvalidOperationException("StormTcpServer is already running. Call StopAsync before starting it again.");
        }

        // Retired here rather than in StopAsync: a handler abandoned by a forced shutdown may still
        // hold the previous token, and disposing the source out from under it would fault it.
        _cts?.Dispose();
        _cts = null;

        bool isUnix = _options.EndPoint is UnixDomainSocketEndPoint;
        Socket? listenSocket = null;

        try
        {
            if (isUnix)
            {
                listenSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            }
            else if (_options.DualMode)
            {
                listenSocket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
                listenSocket.DualMode = true;
            }
            else
            {
                listenSocket = new Socket(_options.EndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            }

            if (!isUnix)
            {
                listenSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

                if (_options.Socket.NoDelay)
                {
                    listenSocket.NoDelay = true;
                }
            }

            EndPoint bindEndPoint = _options.DualMode && _options.EndPoint is IPEndPoint ipEndPoint
                ? new IPEndPoint(IPAddress.IPv6Any, ipEndPoint.Port)
                : _options.EndPoint;

            // Remove stale socket file for Unix domain sockets
            if (isUnix && _options.EndPoint is UnixDomainSocketEndPoint udsEndPoint)
            {
                string? path = udsEndPoint.ToString();
                if (path is not null && File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            listenSocket.Bind(bindEndPoint);
            listenSocket.Listen(_options.Backlog);

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _connections.Open();
            _listenSocket = listenSocket;
            _acceptTask = AcceptLoopAsync(_cts.Token);
            _logger.LogInformation("TCP server listening on {EndPoint}", bindEndPoint);
        }
        catch
        {
            // Nothing is left half-started: the socket and the source go away and IsRunning stays
            // false, so a host sees the failure instead of a server that is up but never accepts.
            listenSocket?.Dispose();
            _cts?.Dispose();
            _listenSocket = null;
            _cts = null;
            Volatile.Write(ref _running, 0);
            throw;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops accepting new connections, closes every active session, and then waits for the in-flight
    /// connection handlers to finish unwinding before returning.
    /// </summary>
    /// <param name="cancellationToken">
    /// Bounds the drain alongside <see cref="ServerOptions.ShutdownDrainTimeout"/>, whichever comes
    /// first. Cancelling it is a normal "stop waiting and force it" signal from a host, so neither a
    /// spent drain budget nor a cancelled token throws.
    /// </param>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _running, 0) is 0)
        {
            return;
        }

#if NET8_0_OR_GREATER
        await _cts!.CancelAsync().ConfigureAwait(false);
#else
        _cts!.Cancel();
#endif

        _listenSocket?.Close();

        if (_acceptTask is not null)
        {
            try
            {
                await _acceptTask.ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }
        }

        // Every handler the accept loop was going to start has been registered by now, so nothing
        // can slip in behind the drain's snapshot.
        _connections.Close();

        // Before the drain, not after: a handler parked in a write that only the peer can unblock
        // ends when its session closes, so waiting first would just spend the whole budget.
        await Sessions.CloseAllAsync().ConfigureAwait(false);

        int stillRunning = await _connections.DrainAsync(_options.ShutdownDrainTimeout, cancellationToken).ConfigureAwait(false);
        if (stillRunning > 0)
        {
            _logger.LogWarning(
                "TCP server drain ended with {Count} connection(s) still active after {Timeout}; they are being abandoned",
                stillRunning,
                _options.ShutdownDrainTimeout);
        }

        _logger.LogInformation("TCP server stopped");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket clientSocket;
            try
            {
                clientSocket = await _listenSocket!.AcceptAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                _logger.LogWarning(ex, "Accept loop terminated");
                break;
            }

            // Claimed before the handshake so half-open connections count against the limits too
            if (!_connectionGate.TryAcquire(clientSocket.RemoteEndPoint, out IPAddress? lease))
            {
                _logger.LogDebug("Connection rejected: connection limit reached");
                clientSocket.Close();
                continue;
            }

            if (clientSocket.AddressFamily != AddressFamily.Unix)
            {
                if (_options.Socket.NoDelay)
                {
                    clientSocket.NoDelay = true;
                }
            }

            _options.Socket.ApplyKeepAlive(clientSocket);

            _connections.Track(HandleConnectionAsync(clientSocket, lease, ct));
        }
    }

    private async Task HandleConnectionAsync(Socket socket, IPAddress? lease, CancellationToken ct)
    {
        long id = ConnectionId.Next();
        ITransport transport;

        if (_options.Ssl is not null)
        {
            transport = new SslTransport(
                socket,
                _options.Ssl.Certificate,
                _options.Ssl.Protocols,
                _options.Ssl.ClientCertificateRequired,
                maxPendingReceiveBytes: _options.Socket.MaxPendingReceiveBytes,
                maxPendingSendBytes: _options.Socket.MaxPendingSendBytes);
        }
        else
        {
            transport = new TcpTransport(socket, _options.Socket.MaxPendingReceiveBytes, _options.Socket.MaxPendingSendBytes);
        }

        TcpSession? session = null;
        try
        {
            long handshakeStart = Stopwatch.GetTimestamp();

            // A peer that completes TCP and then stalls mid-TLS would otherwise hold this socket,
            // its pipes and this task for the lifetime of the process.
            using CancellationTokenSource tlsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (_options.TlsHandshakeTimeout > TimeSpan.Zero && _options.TlsHandshakeTimeout != Timeout.InfiniteTimeSpan)
            {
                tlsCts.CancelAfter(_options.TlsHandshakeTimeout);
            }

            await transport.HandshakeAsync(tlsCts.Token).ConfigureAwait(false);
            Metrics.RecordHandshakeDuration(StopwatchHelper.GetElapsedTime(handshakeStart));

            IMessageFramer framer = _options.Framer ?? RawFramer.Instance;
            PipeConnection? connection = null;
            connection = new PipeConnection(
                transport,
                framer,
                async data =>
                {
                    if (session is null)
                    {
                        return;
                    }

                    session.NotifyDataReceived();
                    session.Metrics.AddBytesReceived(data.Length);
                    Metrics.RecordMessageReceived(data.Length);

                    ReadOnlyMemory<byte> processed = await _pipeline.OnDataReceivedAsync(session, data).ConfigureAwait(false);
                    if (processed.IsEmpty) return;

                    foreach (DataReceivedHandler handler in _onDataReceived.Handlers)
                    {
                        try
                        {
                            await handler(session, processed).ConfigureAwait(false);
                        }
                        catch (Exception handlerEx)
                        {
                            _logger.LogError(handlerEx, "Unhandled exception in OnDataReceived handler for session {SessionId}", session.Id);
                        }
                    }
                },
                async ex =>
                {
                    await RaiseErrorAsync(session, ex).ConfigureAwait(false);
                });

            session = new TcpSession(id, transport, connection, socket.RemoteEndPoint, _options.SlowConsumerPolicy, Metrics);
            session.SetGroupManager(Groups);
            Sessions.TryAdd(session);
            Metrics.RecordConnectionOpened();
            _logger.LogDebug("Session {SessionId} connected from {RemoteEndPoint}", id, socket.RemoteEndPoint);

            // Route socket errors to the server's OnError event
            if (transport is TcpTransport tcp)
            {
                // The transport callback is synchronous, so the handlers run detached.
                // RaiseErrorAsync swallows and logs handler failures, so nothing can fault here.
                tcp.OnSocketError = error => _ = RaiseErrorAsync(session, new SocketException((int)error)).AsTask();
            }

            // Setup idle timeout
            if (_options.IdleTimeout > TimeSpan.Zero)
            {
                IdleTimer idleTimer = new(_options.IdleTimeout, _logger);
                idleTimer.OnTimeout = async () =>
                {
                    _logger.LogWarning("Session {SessionId} idle timeout", session.Id);
                    session.SetDisconnectReason(DisconnectReason.IdleTimeout);
                    await session.CloseAsync(ct).ConfigureAwait(false);
                };
                session.SetIdleTimer(idleTimer);
                idleTimer.Start();
            }

            await _pipeline.OnConnectedAsync(session).ConfigureAwait(false);
            foreach (SessionConnectedHandler handler in _onConnected.Handlers)
            {
                try
                {
                    await handler(session).ConfigureAwait(false);
                }
                catch (Exception handlerEx)
                {
                    _logger.LogError(handlerEx, "Unhandled exception in OnConnected handler for session {SessionId}", session.Id);
                }
            }

            await connection.RunAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Metrics.RecordError();

            if (session is not null)
            {
                session.SetDisconnectReason(DisconnectReason.TransportError);
                _logger.LogError(ex, "Session {SessionId} error", session.Id);

                try
                {
                    await _pipeline.OnErrorAsync(session, ex).ConfigureAwait(false);
                }
                catch (Exception mwEx)
                {
                    _logger.LogError(mwEx, "Middleware OnError exception for session {SessionId}", session.Id);
                }
            }

            await RaiseErrorAsync(session, ex).ConfigureAwait(false);
        }
        finally
        {
            if (session is not null)
            {
                // Default: if no specific reason was set, the client closed the connection
                session.SetDisconnectReason(DisconnectReason.ClosedByClient);

                session.SetState(ConnectionState.Closed);
                Sessions.TryRemove(session.Id, out _);
                Groups.RemoveFromAll(session);
                Metrics.RecordConnectionClosed(session.Metrics.Uptime);

                DisconnectReason reason = session.DisconnectReason;
                _logger.LogDebug("Session {SessionId} disconnected: {Reason}", session.Id, reason);

                try
                {
                    await _pipeline.OnDisconnectedAsync(session, reason).ConfigureAwait(false);
                }
                catch (Exception mwEx)
                {
                    _logger.LogError(mwEx, "Middleware OnDisconnected exception for session {SessionId}", session.Id);
                }

                foreach (SessionDisconnectedHandler handler in _onDisconnected.Handlers)
                {
                    try
                    {
                        await handler(session, reason).ConfigureAwait(false);
                    }
                    catch (Exception handlerEx)
                    {
                        _logger.LogError(handlerEx, "Unhandled exception in OnDisconnected handler for session {SessionId}", session.Id);
                    }
                }

                await session.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }

            _connectionGate.Release(lease);
        }
    }

    /// <summary>Sends data to all connected sessions. Optionally excludes one session by ID.</summary>
    public async ValueTask BroadcastAsync(ReadOnlyMemory<byte> data, long? excludeId = null, CancellationToken cancellationToken = default)
    {
        await Sessions.BroadcastAsync(data, excludeId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        GC.SuppressFinalize(this);

        await StopAsync().ConfigureAwait(false);
        _cts?.Dispose();
    }
}