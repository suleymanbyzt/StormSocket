using System.Net;
using System.Net.Sockets;
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
    private Socket? _listenSocket;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private readonly MiddlewarePipeline _pipeline = new();
    private bool _disposed;

    /// <summary>All currently connected sessions, keyed by ID.</summary>
    public SessionManager Sessions { get; } = new();

    /// <summary>Manages named groups for targeted broadcast.</summary>
    public SessionGroup Groups { get; } = new();

    /// <summary>Fired when a new client connects and handshake completes.</summary>
    public event SessionConnectedHandler? OnConnected;

    /// <summary>Fired when a client disconnects (gracefully or not).</summary>
    public event SessionDisconnectedHandler? OnDisconnected;

    /// <summary>Fired when data (or a framed message) is received from a client.</summary>
    public event DataReceivedHandler? OnDataReceived;

    /// <summary>Fired when an error occurs during connection handling.</summary>
    public event ErrorHandler? OnError;

    public StormTcpServer(ServerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>Registers a middleware that intercepts connection lifecycle and data flow.</summary>
    public void UseMiddleware(IConnectionMiddleware middleware) => _pipeline.Use(middleware);

    /// <summary>Binds to the configured endpoint and starts accepting connections.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        if (_options.DualMode)
        {
            _listenSocket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
            _listenSocket.DualMode = true;
        }
        else
        {
            _listenSocket = new Socket(_options.EndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        }

        _listenSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        if (_options.NoDelay)
        {
            _listenSocket.NoDelay = true;
        }

        IPEndPoint bindEndPoint = _options.DualMode
            ? new IPEndPoint(IPAddress.IPv6Any, _options.EndPoint.Port)
            : _options.EndPoint;

        _listenSocket.Bind(bindEndPoint);
        _listenSocket.Listen(_options.Backlog);

        _acceptTask = AcceptLoopAsync(_cts.Token);
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
            catch (SocketException)
            {
                break;
            }

            // enforce max connections limit
            if (_options.MaxConnections > 0 && Sessions.Count >= _options.MaxConnections)
            {
                // it might be a good idea to notify the server here
                clientSocket.Close();
                continue;
            }

            if (_options.NoDelay)
            {
                clientSocket.NoDelay = true;
            }

            if (_options.KeepAlive)
            {
                clientSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            }

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
            transport = new TcpTransport(socket, _options.MaxPendingReceiveBytes, _options.MaxPendingSendBytes);
        }

        TcpSession? session = null;
        try
        {
            await transport.HandshakeAsync(ct).ConfigureAwait(false);

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

                    session.Metrics.AddBytesReceived(data.Length);

                    ReadOnlyMemory<byte> processed = await _pipeline.OnDataReceivedAsync(session, data).ConfigureAwait(false);
                    if (processed.IsEmpty) return;

                    if (OnDataReceived is not null)
                    {
                        await OnDataReceived.Invoke(session, processed).ConfigureAwait(false);
                    }
                },
                async ex =>
                {
                    if (OnError is not null)
                    {
                        await OnError.Invoke(session, ex).ConfigureAwait(false);
                    }
                });

            session = new TcpSession(id, transport, connection, socket.RemoteEndPoint, _options.SlowConsumerPolicy);
            Sessions.TryAdd(session);

            // Route socket errors to the server's OnError event
            if (transport is TcpTransport tcp)
            {
                tcp.OnSocketError = error => { OnError?.Invoke(session, new SocketException((int)error)); };
            }

            await _pipeline.OnConnectedAsync(session).ConfigureAwait(false);
            if (OnConnected is not null)
            {
                await OnConnected.Invoke(session).ConfigureAwait(false);
            }

            await connection.RunAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (session is not null)
            {
                await _pipeline.OnErrorAsync(session, ex).ConfigureAwait(false);
            }

            if (OnError is not null)
            {
                await OnError.Invoke(session, ex).ConfigureAwait(false);
            }
        }
        finally
        {
            if (session is not null)
            {
                session.SetState(ConnectionState.Closed);
                Sessions.TryRemove(session.Id, out _);
                Groups.RemoveFromAll(session);

                await _pipeline.OnDisconnectedAsync(session).ConfigureAwait(false);

                if (OnDisconnected is not null)
                {
                    await OnDisconnected.Invoke(session).ConfigureAwait(false);
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