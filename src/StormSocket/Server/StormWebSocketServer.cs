using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
///     WebSocket = new WebSocketOptions { Heartbeat = new() { PingInterval = TimeSpan.FromSeconds(15) } },
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
    private readonly ILogger _logger;
    private Socket? _listenSocket;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private readonly MiddlewarePipeline _pipeline = new();
    private bool _disposed;

    /// <summary>Server-wide aggregate metrics (connections, messages, bytes, errors).</summary>
    public ServerMetrics Metrics { get; } = new();

    /// <summary>All currently connected WebSocket sessions.</summary>
    public SessionManager Sessions { get; } = new();

    /// <summary>Manages named groups for targeted broadcast.</summary>
    public NetworkSessionGroup Groups { get; } = new();

    /// <summary>
    /// Fired when a WebSocket client completes the upgrade handshake.
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
    public event WsConnectedHandler? OnConnected;

    /// <summary>
    /// Fired when a WebSocket client disconnects (gracefully or not).
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
    public event WsDisconnectedHandler? OnDisconnected;

    /// <summary>
    /// Fired when a complete text or binary WebSocket message is received.
    /// Use <see cref="WsMessage.IsText"/> to check the frame type and <see cref="WsMessage.Text"/> for decoded string content.
    /// <para><b>Signature:</b> <c>async (IConnectionSession session, WsMessage msg) => { }</c></para>
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
        _logger = (options.LoggerFactory ?? NullLoggerFactory.Instance).CreateLogger<StormWebSocketServer>();
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

        if (_options.Socket.NoDelay)
        {
            _listenSocket.NoDelay = true;
        }

        IPEndPoint bindEndPoint = _options.DualMode
            ? new IPEndPoint(IPAddress.IPv6Any, _options.EndPoint.Port)
            : _options.EndPoint;

        _listenSocket.Bind(bindEndPoint);
        _listenSocket.Listen(_options.Backlog);

        _acceptTask = AcceptLoopAsync(_cts.Token);
        _logger.LogInformation("WebSocket server listening on {EndPoint}", bindEndPoint);
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
        foreach (INetworkSession s in Sessions.All)
        {
            if (s is WebSocketSession ws && ws.State == ConnectionState.Connected)
            {
                ws.SetDisconnectReason(DisconnectReason.GoingAway);
                try
                {
                    await ws.WriteFrameAsync(writer => WsFrameEncoder.WriteClose(writer, WsCloseStatus.GoingAway));
                }
                catch
                {
                    // ignored
                }
            }
        }

        await Sessions.CloseAllAsync().ConfigureAwait(false);
        _logger.LogInformation("WebSocket server stopped, {Count} sessions closed", Sessions.Count);
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

            // Enforce max connections limit
            if (_options.MaxConnections > 0 && Sessions.Count >= _options.MaxConnections)
            {
                _logger.LogDebug("Connection rejected: max connections ({MaxConnections}) reached", _options.MaxConnections);
                clientSocket.Close();
                continue;
            }

            if (_options.Socket.NoDelay)
            {
                clientSocket.NoDelay = true;
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

        WebSocketSession? session = null;
        try
        {
            long handshakeStart = Stopwatch.GetTimestamp();
            await transport.HandshakeAsync(ct).ConfigureAwait(false);

            (bool upgradeSuccess, WsPerMessageDeflate? deflate) = await PerformUpgradeAsync(transport, socket.RemoteEndPoint, ct).ConfigureAwait(false);
            Metrics.RecordHandshakeDuration(StopwatchHelper.GetElapsedTime(handshakeStart));
            if (!upgradeSuccess)
            {
                deflate?.Dispose();
                await transport.DisposeAsync().ConfigureAwait(false);
                return;
            }

            session = new WebSocketSession(id, transport, socket.RemoteEndPoint, _options.SlowConsumerPolicy, Metrics);
            if (deflate is not null)
            {
                session.SetCompression(deflate);
            }
            using WsFragmentAssembler assembler = new(_wsOptions.MaxMessageSize);
            Sessions.TryAdd(session);
            Metrics.RecordConnectionOpened();
            _logger.LogDebug("Session {SessionId} connected from {RemoteEndPoint}", id, socket.RemoteEndPoint);

            // Route socket errors to the server's OnError event
            if (transport is TcpTransport tcp)
            {
                tcp.OnSocketError = error => { OnError?.Invoke(session, new SocketException((int)error)); };
            }

            // Setup heartbeat with dead connection detection
            if (_wsOptions.Heartbeat.PingInterval > TimeSpan.Zero)
            {
                WsHeartbeat heartbeat = new WsHeartbeat(
                    sendPing: async ct2 => await session.WriteFrameAsync(
                        writer => WsFrameEncoder.WritePing(writer), cancellationToken: ct2),
                    _wsOptions.Heartbeat.PingInterval,
                    _wsOptions.Heartbeat.MaxMissedPongs,
                    _logger);
                heartbeat.OnTimeout = async () =>
                {
                    _logger.LogWarning("Session {SessionId} heartbeat timeout", session.Id);
                    session.SetDisconnectReason(DisconnectReason.HeartbeatTimeout);
                    await session.CloseAsync(ct).ConfigureAwait(false);
                };
                session.SetHeartbeat(heartbeat);
                heartbeat.Start();
            }

            // Setup idle timeout
            if (_wsOptions.IdleTimeout > TimeSpan.Zero)
            {
                IdleTimer idleTimer = new(_wsOptions.IdleTimeout, _logger);
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

            // Frame read loop
            await ReadFrameLoopAsync(session, transport, assembler, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Metrics.RecordError();

            if (session is not null)
            {
                session.SetDisconnectReason(ex is OperationCanceledException
                    ? DisconnectReason.HandshakeTimeout
                    : DisconnectReason.TransportError);
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

    private async Task<(bool Success, WsPerMessageDeflate? Deflate)> PerformUpgradeAsync(ITransport transport, EndPoint? remoteEndPoint, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        if (_wsOptions.HandshakeTimeout != Timeout.InfiniteTimeSpan)
        {
            cts.CancelAfter(_wsOptions.HandshakeTimeout);
        }

        PipeReader reader = transport.Input;

        while (!cts.Token.IsCancellationRequested)
        {
            ReadResult result = await reader.ReadAsync(cts.Token).ConfigureAwait(false);
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
                            return (false, null);
                        }
                    }

                    // Negotiate permessage-deflate compression (RFC 7692)
                    string? clientExtensions = context!.Headers.GetValueOrDefault("Sec-WebSocket-Extensions");
                    (WsPerMessageDeflate? deflate, string? extensionResponse) =
                        WsPerMessageDeflate.TryNegotiate(clientExtensions, _wsOptions.Compression);

                    byte[] response = WsUpgradeHandler.BuildUpgradeResponse(context.WsKey, extensionResponse, context.SelectedSubprotocol);
                    await WriteResponseAsync(transport, response, ct).ConfigureAwait(false);
                    return (true, deflate);

                case WsUpgradeResult.Incomplete:
                    reader.AdvanceTo(buffer.Start, buffer.End);
                    if (result.IsCompleted)
                    {
                        return (false, null);
                    }
                    continue;

                default:
                    reader.AdvanceTo(buffer.Start, buffer.End);
                    byte[] errorResponse = WsUpgradeHandler.BuildErrorResponse(upgradeResult);
                    await WriteResponseAsync(transport, errorResponse, ct).ConfigureAwait(false);
                    return (false, null);
            }
        }

        return (false, null);
    }

    private static async ValueTask WriteResponseAsync(ITransport transport, byte[] response, CancellationToken ct)
    {
        Span<byte> span = transport.Output.GetSpan(response.Length);
        response.CopyTo(span);
        transport.Output.Advance(response.Length);
        await transport.Output.FlushAsync(ct).ConfigureAwait(false);
    }

    private async Task ReadFrameLoopAsync(WebSocketSession session, ITransport transport, WsFragmentAssembler assembler, CancellationToken ct)
    {
        PipeReader reader = transport.Input;
        bool hasCompression = session.Compression is not null;

        while (!ct.IsCancellationRequested)
        {
            ReadResult result = await reader.ReadAsync(ct).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;

            try
            {
                while (WsFrameDecoder.TryDecodeFrame(ref buffer, out WsFrame frame, _wsOptions.MaxFrameSize, allowCompressedFrames: hasCompression))
                {
                    if (frame.IsControl)
                    {
                        await HandleFrameAsync(session, transport, frame).ConfigureAwait(false);
                    }
                    else
                    {
                        WsMessage? message = assembler.TryAssemble(in frame);
                        if (message is not null)
                        {
                            WsMessage msg = message.Value;
                            if (msg.Compressed && session.Compression is not null)
                            {
                                byte[] decompressed = session.Compression.Decompress(msg.Data.Span);
                                msg = new WsMessage { Data = decompressed, IsText = msg.IsText };
                            }
                            await HandleMessageAsync(session, msg).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (WsProtocolException ex)
            {
                Metrics.RecordError();
                DisconnectReason reason = ex.CloseStatus == WsCloseStatus.MessageTooBig
                    ? DisconnectReason.MessageTooBig
                    : DisconnectReason.ProtocolError;
                _logger.LogWarning("Session {SessionId} {Reason}: {Message}", session.Id, reason, ex.Message);
                session.SetDisconnectReason(reason);
                WsCloseStatus status = ex.CloseStatus;
                await session.WriteFrameAsync(writer => WsFrameEncoder.WriteClose(writer, status), cancellationToken: ct);

                try
                {
                    await _pipeline.OnErrorAsync(session, ex).ConfigureAwait(false);
                }
                catch (Exception mwEx)
                {
                    _logger.LogError(mwEx, "Middleware OnError exception for session {SessionId}", session.Id);
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

                break;
            }

            reader.AdvanceTo(buffer.Start, buffer.End);

            if (result.IsCompleted)
            {
                break;
            }
        }
    }

    private async ValueTask HandleMessageAsync(WebSocketSession session, WsMessage msg)
    {
        session.NotifyDataReceived();
        session.Metrics.AddBytesReceived(msg.Data.Length);
        Metrics.RecordMessageReceived(msg.Data.Length);

        ReadOnlyMemory<byte> processed = await _pipeline.OnDataReceivedAsync(session, msg.Data).ConfigureAwait(false);
        if (processed.IsEmpty)
        {
            return;
        }

        if (OnMessageReceived is not null)
        {
            try
            {
                await OnMessageReceived.Invoke(session, msg).ConfigureAwait(false);
            }
            catch (Exception handlerEx)
            {
                _logger.LogError(handlerEx, "Unhandled exception in OnMessageReceived handler for session {SessionId}", session.Id);
            }
        }
    }

    private async ValueTask HandleFrameAsync(WebSocketSession session, ITransport transport, WsFrame frame)
    {
        switch (frame.OpCode)
        {
            case WsOpCode.Ping when _wsOptions.Heartbeat.AutoPong:
                ReadOnlyMemory<byte> pingPayload = frame.Payload;
                await session.WriteFrameAsync(writer => WsFrameEncoder.WritePong(writer, pingPayload.Span));
                break;

            case WsOpCode.Pong:
                session.NotifyPongReceived();
                break;

            case WsOpCode.Close:
                session.SetDisconnectReason(DisconnectReason.ClosedByClient);

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
        int byteCount = Encoding.UTF8.GetByteCount(text);
        byte[] rented = ArrayPool<byte>.Shared.Rent(byteCount);
        int written = Encoding.UTF8.GetBytes(text, rented);
        try
        {
            ReadOnlyMemory<byte> data = rented.AsMemory(0, written);
            List<ValueTask> tasks = [];
            foreach (INetworkSession s in Sessions.All)
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
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
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