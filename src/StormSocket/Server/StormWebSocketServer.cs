using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using StormSocket.Core;
using StormSocket.Events;
using StormSocket.Middleware;
using StormSocket.Session;
using StormSocket.Transport;
using StormSocket.WebSocket;

namespace StormSocket.Server;

/// <summary>
/// Event-based WebSocket server with RFC 6455 framing, automatic ping/pong,
/// dead connection detection, and group broadcast.
/// <example>
/// <code>
/// var ws = new StormWebSocketServer(new ServerOptions {
///     EndPoint = new IPEndPoint(IPAddress.Any, 8080),
///     WebSocket = new WebSocketOptions { PingInterval = TimeSpan.FromSeconds(15) },
/// });
/// ws.OnMessageReceived += async (session, msg) => await ws.BroadcastTextAsync(msg.Text);
/// await ws.StartAsync();
/// </code>
/// </example>
/// </summary>
public class StormWebSocketServer : IAsyncDisposable
{
    private readonly ServerOptions _options;
    private readonly WebSocketOptions _wsOptions;
    private Socket? _listenSocket;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private readonly MiddlewarePipeline _pipeline = new();
    private bool _disposed;

    /// <summary>All currently connected WebSocket sessions.</summary>
    public SessionManager Sessions { get; } = new();

    /// <summary>Manages named groups for targeted broadcast.</summary>
    public SessionGroup Groups { get; } = new();

    /// <summary>
    /// Fired when a WebSocket client completes the upgrade handshake.
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
    public event WsConnectedHandler? OnConnected;

    /// <summary>
    /// Fired when a WebSocket client disconnects (gracefully or not).
    /// <para><b>Signature:</b> <c>async (ISession session) => { }</c></para>
    /// <example>
    /// <code>
    /// server.OnDisconnected += async (session) =>
    /// {
    ///     Console.WriteLine($"#{session.Id} disconnected — sent: {session.Metrics.BytesSent}, recv: {session.Metrics.BytesReceived}");
    /// };
    /// </code>
    /// </example>
    /// </summary>
    public event WsDisconnectedHandler? OnDisconnected;

    /// <summary>
    /// Fired when a complete text or binary WebSocket message is received.
    /// Use <see cref="WsMessage.IsText"/> to check the frame type and <see cref="WsMessage.Text"/> for decoded string content.
    /// <para><b>Signature:</b> <c>async (ISession session, WsMessage msg) => { }</c></para>
    /// <example>
    /// <code>
    /// server.OnMessageReceived += async (session, msg) =>
    /// {
    ///     if (msg.IsText)
    ///         Console.WriteLine($"#{session.Id}: {msg.Text}");
    ///     else
    ///         Console.WriteLine($"#{session.Id}: {msg.Data.Length} bytes");
    /// };
    /// </code>
    /// </example>
    /// </summary>
    public event WsMessageReceivedHandler? OnMessageReceived;

    /// <summary>
    /// Fired when an error occurs during connection handling.
    /// Session may be null if the error occurs before session creation (e.g. during handshake).
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
    public event ErrorHandler? OnError;

    /// <summary>
    /// Fired before accepting a WebSocket upgrade request.
    /// Use this to authenticate clients based on headers, cookies, query params, etc.
    /// Call context.Accept() to proceed or context.Reject(statusCode) to deny.
    /// If no handler is registered, connections are auto-accepted.
    /// <para><b>Signature:</b> <c>async (WsUpgradeContext context) => { }</c></para>
    /// <example>
    /// <code>
    /// server.OnConnecting += async (context) =>
    /// {
    ///     string? token = context.Headers.GetValueOrDefault("Authorization");
    ///     if (!IsValidToken(token))
    ///     {
    ///         context.Reject(401, "Invalid token");
    ///         return;
    ///     }
    ///     context.Accept();
    /// };
    /// </code>
    /// </example>
    /// </summary>
    public event WsConnectingHandler? OnConnecting;

    public StormWebSocketServer(ServerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _wsOptions = options.WebSocket ?? new WebSocketOptions();
    }

    /// <summary>Registers a middleware that intercepts connection lifecycle and data flow.</summary>
    public void UseMiddleware(IConnectionMiddleware middleware) => _pipeline.Use(middleware);

    /// <summary>Binds to the configured endpoint and starts accepting WebSocket connections.</summary>
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
        if (_cts is null)
        {
            return;
        }

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

        // Send GoingAway close to all connected sessions
        foreach (ISession s in Sessions.All)
        {
            if (s is WebSocketSession ws && ws.State == ConnectionState.Connected)
            {
                try
                {
                    await ws.WriteFrameAsync(writer => WsFrameEncoder.WriteClose(writer, WsCloseStatus.GoingAway));
                }
                catch
                {
                    // ignred
                }
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

            // Enforce max connections limit
            if (_options.MaxConnections > 0 && Sessions.Count >= _options.MaxConnections)
            {
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

        WebSocketSession? session = null;
        try
        {
            await transport.HandshakeAsync(ct).ConfigureAwait(false);

            if (!await PerformUpgradeAsync(transport, socket.RemoteEndPoint, ct).ConfigureAwait(false))
            {
                await transport.DisposeAsync().ConfigureAwait(false);
                return;
            }

            session = new WebSocketSession(id, transport, socket.RemoteEndPoint, _options.SlowConsumerPolicy);
            Sessions.TryAdd(session);

            // Route socket errors to the server's OnError event
            if (transport is TcpTransport tcp)
            {
                tcp.OnSocketError = error => { OnError?.Invoke(session, new SocketException((int)error)); };
            }

            // Setup heartbeat with dead connection detection
            if (_wsOptions.PingInterval > TimeSpan.Zero)
            {
                WsHeartbeat heartbeat = new WsHeartbeat(
                    sendPing: async ct2 => await session.WriteFrameAsync(
                        writer => WsFrameEncoder.WritePing(writer), cancellationToken: ct2),
                    _wsOptions.PingInterval,
                    _wsOptions.MaxMissedPongs);
                heartbeat.OnTimeout = async () => { await session.CloseAsync(ct).ConfigureAwait(false); };
                session.SetHeartbeat(heartbeat);
                heartbeat.Start();
            }

            await _pipeline.OnConnectedAsync(session).ConfigureAwait(false);
            if (OnConnected is not null)
            {
                await OnConnected.Invoke(session).ConfigureAwait(false);
            }

            // Frame read loop
            await ReadFrameLoopAsync(session, transport, ct).ConfigureAwait(false);
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

    private async Task<bool> PerformUpgradeAsync(ITransport transport, EndPoint? remoteEndPoint, CancellationToken ct)
    {
        PipeReader reader = transport.Input;

        while (!ct.IsCancellationRequested)
        {
            ReadResult result = await reader.ReadAsync(ct).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;

            WsUpgradeResult upgradeResult = WsUpgradeHandler.TryParseUpgradeRequest(
                ref buffer,
                out WsUpgradeContext? context,
                remoteEndPoint,
                _wsOptions.AllowedOrigins);

            switch (upgradeResult)
            {
                case WsUpgradeResult.Success:
                    reader.AdvanceTo(buffer.Start, buffer.End);

                    // Fire OnConnecting event if registered
                    if (OnConnecting is not null)
                    {
                        await OnConnecting.Invoke(context!).ConfigureAwait(false);

                        // If handler didn't call Accept() or Reject(), auto-accept
                        if (!context!.IsHandled)
                        {
                            context.Accept();
                        }

                        // Handle rejection
                        if (!context.IsAccepted)
                        {
                            byte[] rejectResponse = WsUpgradeHandler.BuildRejectResponse(
                                context.RejectStatusCode,
                                context.RejectReason);
                            await WriteResponseAsync(transport, rejectResponse, ct).ConfigureAwait(false);
                            return false;
                        }
                    }

                    byte[] response = WsUpgradeHandler.BuildUpgradeResponse(context!.WsKey);
                    await WriteResponseAsync(transport, response, ct).ConfigureAwait(false);
                    return true;

                case WsUpgradeResult.Incomplete:
                    reader.AdvanceTo(buffer.Start, buffer.End);
                    if (result.IsCompleted)
                    {
                        return false;
                    }
                    continue;

                default:
                    reader.AdvanceTo(buffer.Start, buffer.End);
                    byte[] errorResponse = WsUpgradeHandler.BuildErrorResponse(upgradeResult);
                    await WriteResponseAsync(transport, errorResponse, ct).ConfigureAwait(false);
                    return false;
            }
        }

        return false;
    }

    private static async ValueTask WriteResponseAsync(ITransport transport, byte[] response, CancellationToken ct)
    {
        Span<byte> span = transport.Output.GetSpan(response.Length);
        response.CopyTo(span);
        transport.Output.Advance(response.Length);
        await transport.Output.FlushAsync(ct).ConfigureAwait(false);
    }

    private async Task ReadFrameLoopAsync(WebSocketSession session, ITransport transport, CancellationToken ct)
    {
        PipeReader reader = transport.Input;

        while (!ct.IsCancellationRequested)
        {
            ReadResult result = await reader.ReadAsync(ct).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;

            try
            {
                while (WsFrameDecoder.TryDecodeFrame(ref buffer, out WsFrame frame, _wsOptions.MaxFrameSize))
                {
                    await HandleFrameAsync(session, transport, frame).ConfigureAwait(false);
                }
            }
            catch (WsProtocolException ex)
            {
                WsCloseStatus status = ex.CloseStatus;
                await session.WriteFrameAsync(writer => WsFrameEncoder.WriteClose(writer, status), cancellationToken: ct);
                await _pipeline.OnErrorAsync(session, ex).ConfigureAwait(false);
                
                if (OnError is not null)
                {
                    await OnError.Invoke(session, ex).ConfigureAwait(false);
                }
                
                break;
            }

            reader.AdvanceTo(buffer.Start, buffer.End);

            if (result.IsCompleted)
            {
                break;
            }
        }
    }

    private async ValueTask HandleFrameAsync(WebSocketSession session, ITransport transport, WsFrame frame)
    {
        switch (frame.OpCode)
        {
            case WsOpCode.Text:
            case WsOpCode.Binary:
                session.Metrics.AddBytesReceived(frame.Payload.Length);
                WsMessage msg = new WsMessage
                {
                    Data = frame.Payload,
                    IsText = frame.IsText,
                };

                ReadOnlyMemory<byte> processed = await _pipeline.OnDataReceivedAsync(session, frame.Payload).ConfigureAwait(false);
                if (processed.IsEmpty)
                {
                    return;
                }

                if (OnMessageReceived is not null)
                {
                    await OnMessageReceived.Invoke(session, msg).ConfigureAwait(false);
                }

                break;

            case WsOpCode.Ping when _wsOptions.AutoPong:
                ReadOnlyMemory<byte> pingPayload = frame.Payload;
                await session.WriteFrameAsync(writer => WsFrameEncoder.WritePong(writer, pingPayload.Span));
                break;

            case WsOpCode.Pong:
                session.NotifyPongReceived();
                break;

            case WsOpCode.Close:
                // Echo back the client's close status if present, otherwise NormalClosure
                WsCloseStatus closeStatus = WsCloseStatus.NormalClosure;
                if (frame.Payload.Length >= 2)
                {
                    closeStatus = (WsCloseStatus)System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(frame.Payload.Span);
                }

                WsCloseStatus echoStatus = closeStatus;
                await session.WriteFrameAsync(writer => WsFrameEncoder.WriteClose(writer, echoStatus));
                await session.CloseAsync().ConfigureAwait(false);
                break;
        }
    }

    /// <summary>Sends a text message to all connected WebSocket sessions concurrently. Optionally excludes one session.</summary>
    public async ValueTask BroadcastTextAsync(string text, long? excludeId = null, CancellationToken cancellationToken = default)
    {
        byte[] data = Encoding.UTF8.GetBytes(text);
        List<ValueTask> tasks = [];
        foreach (ISession s in Sessions.All)
        {
            if (s.Id == excludeId)
            {
                continue;
            }

            if (s is WebSocketSession wss)
            {
                tasks.Add(wss.SendTextAsync(data, cancellationToken));
            }
        }

        foreach (ValueTask task in tasks)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }
        }
    }

    /// <summary>Sends binary data to all connected WebSocket sessions. Optionally excludes one session.</summary>
    public async ValueTask BroadcastBinaryAsync(ReadOnlyMemory<byte> data, long? excludeId = null, CancellationToken cancellationToken = default)
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