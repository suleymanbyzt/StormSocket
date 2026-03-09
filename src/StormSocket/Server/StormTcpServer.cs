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
    private readonly MiddlewarePipeline _pipeline = new();
    private bool _disposed;

    /// <summary>Server-wide aggregate metrics (connections, messages, bytes, errors).</summary>
    public ServerMetrics Metrics { get; } = new();

    /// <summary>All currently connected sessions, keyed by ID.</summary>
    public SessionManager Sessions { get; } = new();

    /// <summary>Manages named groups for targeted broadcast.</summary>
    public NetworkSessionGroup Groups { get; } = new();

    /// <summary>
    /// Fired when a new client connects and handshake (SSL if configured) completes.
    /// <para><b>Signature:</b> <c>async (IConnectionSession session) => { }</c></para>
    /// <example>
    /// <code>
    /// server.OnConnected += async (session) =>
    /// {
    ///     Console.WriteLine($"#{session.Id} connected from {session.RemoteEndPoint}");
    /// };
    /// </code>
    /// </example>
    /// </summary>
    public event SessionConnectedHandler? OnConnected;

    /// <summary>
    /// Fired when a client disconnects (gracefully or not).
    /// <para><b>Signature:</b> <c>async (IConnectionSession session, DisconnectReason reason) => { }</c></para>
    /// <example>
    /// <code>
    /// server.OnDisconnected += async (session, reason) =>
    /// {
    ///     Console.WriteLine($"#{session.Id} disconnected ({reason}) — sent: {session.Metrics.BytesSent}, recv: {session.Metrics.BytesReceived}");
    /// };
    /// </code>
    /// </example>
    /// </summary>
    public event SessionDisconnectedHandler? OnDisconnected;

    /// <summary>
    /// Fired when data (or a framed message) is received from a client.
    /// If a <see cref="ServerOptions.Framer"/> is configured, each invocation contains one complete message.
    /// <para><b>Signature:</b> <c>async (IConnectionSession session, ReadOnlyMemory&lt;byte&gt; data) => { }</c></para>
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
    public event DataReceivedHandler? OnDataReceived;

    /// <summary>
    /// Fired when an error occurs during connection handling.
    /// Session may be null if the error occurs before session creation.
    /// <para><b>Signature:</b> <c>async (IConnectionSession? session, Exception ex) => { }</c></para>
    /// <example>
    /// <code>
    /// server.OnError += async (session, ex) =>
    /// {
    ///     Console.WriteLine($"Error on #{session?.Id}: {ex.Message}");
    /// };
    /// </code>
    /// </example>
    /// </summary>
    public event ErrorHandler? OnError;

    public StormTcpServer(ServerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = (options.LoggerFactory ?? NullLoggerFactory.Instance).CreateLogger<StormTcpServer>();
    }

    /// <summary>Registers a middleware that intercepts connection lifecycle and data flow.</summary>
    public void UseMiddleware(IConnectionMiddleware middleware) => _pipeline.Use(middleware);

    /// <summary>Binds to the configured endpoint and starts accepting connections.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        bool isUnix = _options.EndPoint is UnixDomainSocketEndPoint;

        if (isUnix)
        {
            _listenSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        }
        else if (_options.DualMode)
        {
            _listenSocket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
            _listenSocket.DualMode = true;
        }
        else
        {
            _listenSocket = new Socket(_options.EndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        }

        if (!isUnix)
        {
            _listenSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

            if (_options.Socket.NoDelay)
            {
                _listenSocket.NoDelay = true;
            }
        }

        EndPoint bindEndPoint = isUnix
            ? _options.EndPoint
            : _options.DualMode
                ? new IPEndPoint(IPAddress.IPv6Any, ((IPEndPoint)_options.EndPoint).Port)
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

        _listenSocket.Bind(bindEndPoint);
        _listenSocket.Listen(_options.Backlog);

        _acceptTask = AcceptLoopAsync(_cts.Token);
        _logger.LogInformation("TCP server listening on {EndPoint}", bindEndPoint);
        return Task.CompletedTask;
    }

    /// <summary>Stops accepting new connections and closes all active sessions.</summary>
    public async Task StopAsync()
    {
        if (_cts is null) return;

#if NET8_0_OR_GREATER
        await _cts.CancelAsync().ConfigureAwait(false);
#else
        _cts.Cancel();
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

        await Sessions.CloseAllAsync().ConfigureAwait(false);
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

            // enforce max connections limit
            if (_options.MaxConnections > 0 && Sessions.Count >= _options.MaxConnections)
            {
                _logger.LogDebug("Connection rejected: max connections ({MaxConnections}) reached", _options.MaxConnections);
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

            _ = HandleConnectionAsync(clientSocket, ct);
        }
    }

    private async Task HandleConnectionAsync(Socket socket, CancellationToken ct)
    {
        long id = ConnectionId.Next();
        ITransport transport;

        if (_options.Ssl is not null)
        {
            transport = new SslTransport(
                socket,
                _options.Ssl.Certificate,
                _options.Ssl.Protocols,
                _options.Ssl.ClientCertificateRequired);
        }
        else
        {
            transport = new TcpTransport(socket, _options.Socket.MaxPendingReceiveBytes, _options.Socket.MaxPendingSendBytes);
        }

        TcpSession? session = null;
        try
        {
            long handshakeStart = Stopwatch.GetTimestamp();
            await transport.HandshakeAsync(ct).ConfigureAwait(false);
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

                    if (OnDataReceived is not null)
                    {
                        try
                        {
                            await OnDataReceived.Invoke(session, processed).ConfigureAwait(false);
                        }
                        catch (Exception handlerEx)
                        {
                            _logger.LogError(handlerEx, "Unhandled exception in OnDataReceived handler for session {SessionId}", session.Id);
                        }
                    }
                },
                async ex =>
                {
                    if (OnError is not null)
                    {
                        try
                        {
                            await OnError.Invoke(session, ex).ConfigureAwait(false);
                        }
                        catch (Exception handlerEx)
                        {
                            _logger.LogError(handlerEx, "Unhandled exception in OnError handler");
                        }
                    }
                });

            session = new TcpSession(id, transport, connection, socket.RemoteEndPoint, _options.SlowConsumerPolicy, Metrics);
            Sessions.TryAdd(session);
            Metrics.RecordConnectionOpened();
            _logger.LogDebug("Session {SessionId} connected from {RemoteEndPoint}", id, socket.RemoteEndPoint);

            // Route socket errors to the server's OnError event
            if (transport is TcpTransport tcp)
            {
                tcp.OnSocketError = error => { OnError?.Invoke(session, new SocketException((int)error)); };
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
            if (OnConnected is not null)
            {
                try
                {
                    await OnConnected.Invoke(session).ConfigureAwait(false);
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

            if (OnError is not null)
            {
                try
                {
                    await OnError.Invoke(session, ex).ConfigureAwait(false);
                }
                catch (Exception handlerEx)
                {
                    _logger.LogError(handlerEx, "Unhandled exception in OnError handler");
                }
            }
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

                if (OnDisconnected is not null)
                {
                    try
                    {
                        await OnDisconnected.Invoke(session, reason).ConfigureAwait(false);
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