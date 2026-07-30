using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StormSocket.Core;
using StormSocket.Events;
using StormSocket.Middleware;
using StormSocket.Transport;
using StormSocket.WebSocket;

namespace StormSocket.Client;

/// <summary>
/// Event-based WebSocket client with RFC 6455 compliance (client-side masking),
/// automatic ping/pong, dead connection detection, and auto-reconnect.
/// <example>
/// <code>
/// var ws = new StormWebSocketClient(new WsClientOptions {
///     Uri = new Uri("ws://localhost:8080/chat"),
///     Reconnect = new() { Enabled = true },
/// });
/// ws.OnMessageReceived += async msg => Console.WriteLine(msg.Text);
/// await ws.ConnectAsync();
/// await ws.SendTextAsync("Hello!");
/// </code>
/// </example>
/// </summary>
public class StormWebSocketClient : IAsyncDisposable
{
    /// <summary>
    /// Upper bound on the buffered <c>101 Switching Protocols</c> response. Without it a server that
    /// never sends the header terminator grows the client's receive pipe without limit. Mirrors the
    /// server's own request header budget.
    /// </summary>
    private const int MaxUpgradeResponseBytes = 16 * 1024;

    private readonly WsClientOptions _options;
    private readonly ILogger _logger;
    private readonly MiddlewarePipeline _pipeline = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private readonly AsyncEventSource<ClientConnectedHandler> _onConnected = new();
    private readonly AsyncEventSource<ClientDisconnectedHandler> _onDisconnected = new();
    private readonly AsyncEventSource<ClientWsMessageReceivedHandler> _onMessageReceived = new();
    private readonly AsyncEventSource<ClientErrorHandler> _onError = new();
    private readonly AsyncEventSource<ClientReconnectingHandler> _onReconnecting = new();

    /// <summary>
    /// True for the whole async flow of the frame loop, including the middleware and handlers it
    /// invokes. A disconnect raised from inside that flow must not await the loop it is running on.
    /// </summary>
    private readonly AsyncLocal<bool> _onRunLoop = new();

    private ITransport? _transport;
    private ClientSessionAdapter? _session;
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private WsHeartbeat? _heartbeat;
    private WsPerMessageDeflate? _deflate;
    private TaskCompletionSource _closeHandshake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _closeFrameSent;
    private volatile bool _closeReceived;
    private bool _disposed;
    private volatile ConnectionState _state = ConnectionState.Closed;
    private volatile DisconnectReason _disconnectReason;

    /// <summary>Tracks bytes sent/received and connection uptime.</summary>
    public ConnectionMetrics Metrics { get; private set; } = new();

    /// <summary>Current connection state.</summary>
    public ConnectionState State => _state;

    /// <summary>The reason the last connection was closed.</summary>
    internal DisconnectReason DisconnectReason => _disconnectReason;

    /// <summary>The subprotocol negotiated during the WebSocket handshake, or null if none.</summary>
    public string? Subprotocol { get; private set; }

    /// <summary>The remote server's endpoint.</summary>
    public EndPoint? RemoteEndPoint { get; private set; }

    /// <summary>Fired when the WebSocket connection is established.</summary>
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

    /// <summary>Fired when a complete text or binary message is received.</summary>
    public event ClientWsMessageReceivedHandler? OnMessageReceived
    {
        add => _onMessageReceived.Add(value);
        remove => _onMessageReceived.Remove(value);
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

    public StormWebSocketClient(WsClientOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = (options.LoggerFactory ?? NullLoggerFactory.Instance).CreateLogger<StormWebSocketClient>();
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

    /// <summary>Connects to the WebSocket server. If auto-reconnect is enabled, reconnects on disconnect.</summary>
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
            _runTask = RunFrameLoopAsync(_cts.Token);
        }
    }

    private async Task ConnectCoreAsync(CancellationToken ct)
    {
        _logger.LogInformation("Connecting to {Uri}", _options.Uri);
        _state = ConnectionState.Connecting;
        _disconnectReason = DisconnectReason.None;
        Metrics = new ConnectionMetrics();
        _closeFrameSent = 0;
        _closeReceived = false;
        _closeHandshake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Uri uri = _options.Uri;
        bool useSsl = uri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase);
        string host = uri.Host;
        int port = uri.Port > 0 ? uri.Port : (useSsl ? 443 : 80);

        // The whole sequence runs on this budget: a server that completes TCP and then stalls in TLS
        // or never answers the upgrade would otherwise keep ConnectAsync pending forever.
        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_options.ConnectTimeout);

        IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, timeoutCts.Token).ConfigureAwait(false);

        Socket? socket = null;
        Exception? lastEx = null;

        foreach (IPAddress address in addresses)
        {
            Socket attempt = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            if (_options.Socket.NoDelay)
            {
                attempt.NoDelay = true;
            }

            _options.Socket.ApplyKeepAlive(attempt);

            try
            {
                await attempt.ConnectAsync(new IPEndPoint(address, port), timeoutCts.Token).ConfigureAwait(false);
                socket = attempt;
                break;
            }
            catch (Exception ex)
            {
                lastEx = ex;
                attempt.Dispose();
            }
        }

        if (socket is null)
        {
            _state = ConnectionState.Closed;
            throw lastEx ?? new SocketException((int)SocketError.HostUnreachable);
        }

        RemoteEndPoint = socket.RemoteEndPoint;

        ITransport transport;
        if (useSsl || _options.Ssl is not null)
        {
            transport = new SslTransport(
                socket,
                ClientSslOptions.ResolveTargetHost(_options.Ssl, host),
                _options.Ssl?.Protocols ?? System.Security.Authentication.SslProtocols.None,
                _options.Ssl?.ResolveValidationCallback(),
                _options.Ssl?.ClientCertificate,
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

            // Build extension offer for permessage-deflate if enabled
            string? extensionOffer = _options.Compression.Enabled
                ? WsPerMessageDeflate.BuildOfferHeader(_options.Compression)
                : null;

            (byte[] request, string wsKey) = WsUpgradeHandler.BuildUpgradeRequest(uri, _options.Headers, extensionOffer, _options.Subprotocols);
            Span<byte> requestSpan = transport.Output.GetSpan(request.Length);
            request.CopyTo(requestSpan);
            transport.Output.Advance(request.Length);
            await transport.Output.FlushAsync(timeoutCts.Token).ConfigureAwait(false);

            (bool upgraded, string? serverExtensions, string? negotiatedSubprotocol) =
                await WaitForUpgradeResponseAsync(transport.Input, wsKey, timeoutCts.Token).ConfigureAwait(false);
            if (!upgraded)
            {
                throw new InvalidOperationException("WebSocket upgrade handshake failed.");
            }

            // Parse compression negotiation result
            _deflate?.Dispose();
            _deflate = _options.Compression.Enabled
                ? WsPerMessageDeflate.ParseServerResponse(serverExtensions, _options.Compression)
                : null;

            Subprotocol = negotiatedSubprotocol;
            _transport = transport;
            _state = ConnectionState.Connected;
            _logger.LogInformation("Connected to {Uri}", _options.Uri);

            if (_options.Heartbeat.PingInterval > TimeSpan.Zero)
            {
                _heartbeat = new WsHeartbeat(
                    sendPing: async ct2 => await WriteFrameAsync(
                        writer => WsFrameEncoder.WriteMaskedPing(writer), cancellationToken: ct2).ConfigureAwait(false),
                    _options.Heartbeat.PingInterval,
                    _options.Heartbeat.MaxMissedPongs,
                    _logger);
                _heartbeat.OnTimeout = async () =>
                {
                    _logger.LogWarning("Heartbeat timeout");
                    _disconnectReason = DisconnectReason.HeartbeatTimeout;

                    // Awaiting the frame loop from here would deadlock: its teardown disposes this
                    // heartbeat, and WsHeartbeat.DisposeAsync waits for the very task that is running
                    // this callback. Cancelling is enough — the loop tears the transport down itself.
                    await ShutdownAsync(
                        WsCloseStatus.GoingAway,
                        waitForPeer: false,
                        waitForRunTask: false,
                        CancellationToken.None).ConfigureAwait(false);
                };
                _heartbeat.Start();
            }

            if (transport is TcpTransport tcp)
            {
                // The transport callback is synchronous, so the handlers run detached.
                // RaiseErrorAsync swallows and logs handler failures, so nothing can fault here.
                tcp.OnSocketError = error => _ = RaiseErrorAsync(new SocketException((int)error)).AsTask();
            }

            // One adapter per connection: middleware that stores per-session state in OnConnected
            // must see the same Items dictionary again in OnDataReceived/OnDisconnected/OnError.
            ClientSessionAdapter sessionAdapter = new ClientSessionAdapter(this);
            _session = sessionAdapter;

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

            if (_heartbeat is not null)
            {
                await _heartbeat.DisposeAsync().ConfigureAwait(false);
                _heartbeat = null;
            }

            _deflate?.Dispose();
            _deflate = null;

            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<(bool Success, string? Extensions, string? Subprotocol)> WaitForUpgradeResponseAsync(PipeReader reader, string wsKey, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            ReadResult result = await reader.ReadAsync(ct).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;

            WsUpgradeResponseState state = WsUpgradeHandler.ParseUpgradeResponse(
                ref buffer, wsKey, out string? extensions, out string? subprotocol, out string? statusLine);

            if (state == WsUpgradeResponseState.Accepted)
            {
                // Consumed only, deliberately not examined: a server may put its first frame in the
                // same segment as the 101 response, and marking those bytes examined would leave them
                // buffered until more data arrives — stalling a frame that has already been received.
                reader.AdvanceTo(buffer.Start);
                return (true, extensions, subprotocol);
            }

            // A refusal is final: waiting for more bytes after it would turn a clear rejection into
            // a connect timeout seconds later.
            if (state is WsUpgradeResponseState.Rejected or WsUpgradeResponseState.InvalidAcceptKey)
            {
                reader.AdvanceTo(buffer.Start, buffer.End);
                throw new InvalidOperationException(state == WsUpgradeResponseState.Rejected
                    ? $"WebSocket upgrade rejected by the server: {statusLine}"
                    : "WebSocket upgrade response carried an invalid Sec-WebSocket-Accept value.");
            }

            long buffered = buffer.Length;
            reader.AdvanceTo(buffer.Start, buffer.End);

            if (buffered > MaxUpgradeResponseBytes)
            {
                throw new InvalidOperationException(
                    $"WebSocket upgrade response exceeded {MaxUpgradeResponseBytes} bytes.");
            }

            if (result.IsCompleted)
            {
                return (false, null, null);
            }
        }

        return (false, null, null);
    }

    private async Task RunFrameLoopAsync(CancellationToken ct)
    {
        _onRunLoop.Value = true;
        ClientSessionAdapter sessionAdapter = _session ??= new ClientSessionAdapter(this);
        using WsFragmentAssembler assembler = new(_options.MaxMessageSize);
        bool hasCompression = _deflate is not null;
        try
        {
            PipeReader reader = _transport!.Input;
            while (!ct.IsCancellationRequested)
            {
                ReadResult result = await reader.ReadAsync(ct).ConfigureAwait(false);
                ReadOnlySequence<byte> buffer = result.Buffer;

                try
                {
                    // RFC 6455 Section 5.1: a server frame must never be masked, and a client that
                    // receives one has to fail the connection.
                    while (WsFrameDecoder.TryDecodeFrame(ref buffer, out WsFrame frame, _options.MaxFrameSize, allowCompressedFrames: hasCompression, expectMasked: false))
                    {
                        if (frame.IsControl)
                        {
                            await HandleFrameAsync(frame, ct).ConfigureAwait(false);

                            // RFC 6455 Section 5.5.1: nothing after the peer's Close frame is processed.
                            if (frame.OpCode == WsOpCode.Close)
                            {
                                break;
                            }
                        }
                        else
                        {
                            WsMessage? message = assembler.TryAssemble(in frame);
                            if (message is not null)
                            {
                                WsMessage msg = message.Value;
                                if (msg.Compressed && _deflate is not null)
                                {
                                    byte[] decompressed = _deflate.Decompress(msg.Data.Span, _options.MaxMessageSize);

                                    // The compressed bytes could not be validated before inflating them.
                                    if (msg.IsText && !Utf8Validator.IsValid(decompressed))
                                    {
                                        throw new WsProtocolException(WsCloseStatus.InvalidPayload, "Text message is not valid UTF-8.");
                                    }

                                    msg = new WsMessage { Data = decompressed, IsText = msg.IsText };
                                }

                                await HandleClientMessageAsync(sessionAdapter, msg).ConfigureAwait(false);
                            }
                        }
                    }
                }
                catch (WsProtocolException ex)
                {
                    DisconnectReason reason = ex.CloseStatus == WsCloseStatus.MessageTooBig
                        ? DisconnectReason.MessageTooBig
                        : DisconnectReason.ProtocolError;
                    _logger.LogWarning("Client {Reason}: {Message}", reason, ex.Message);
                    _disconnectReason = reason;
                    await SendCloseFrameAsync(ex.CloseStatus, ct).ConfigureAwait(false);

                    try
                    {
                        await _pipeline.OnErrorAsync(sessionAdapter, ex).ConfigureAwait(false);
                    }
                    catch (Exception mwEx)
                    {
                        _logger.LogError(mwEx, "Middleware OnError exception");
                    }

                    await RaiseErrorAsync(ex).ConfigureAwait(false);

                    break;
                }

                reader.AdvanceTo(buffer.Start, buffer.End);

                if (_closeReceived || result.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
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

            // The connection is gone, so a disconnect waiting for the peer's Close frame is released
            // now instead of burning the whole close timeout on a Close that can no longer arrive.
            _closeHandshake.TrySetResult();

            if (_heartbeat is not null)
            {
                await _heartbeat.DisposeAsync().ConfigureAwait(false);
                _heartbeat = null;
            }

            DisconnectReason reason = _disconnectReason;
            _logger.LogInformation("Disconnected: {Reason}", reason);

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
        }
    }

    private async ValueTask HandleClientMessageAsync(ClientSessionAdapter sessionAdapter, WsMessage msg)
    {
        Metrics.AddBytesReceived(msg.Data.Length);

        // An empty result means a middleware suppressed the message — but a zero-length message is
        // legal in RFC 6455 and must still reach the application, so the two are told apart by
        // whether there was anything to suppress in the first place.
        if (_pipeline.HasMiddleware)
        {
            ReadOnlyMemory<byte> processed = await _pipeline.OnDataReceivedAsync(sessionAdapter, msg.Data).ConfigureAwait(false);
            if (processed.IsEmpty && !msg.Data.IsEmpty)
            {
                return;
            }
        }

        foreach (ClientWsMessageReceivedHandler handler in _onMessageReceived.Handlers)
        {
            try
            {
                await handler(msg).ConfigureAwait(false);
            }
            catch (Exception handlerEx)
            {
                _logger.LogError(handlerEx, "Unhandled exception in OnMessageReceived handler");
            }
        }
    }

    private async ValueTask HandleFrameAsync(WsFrame frame, CancellationToken ct)
    {
        switch (frame.OpCode)
        {
            case WsOpCode.Ping when _options.Heartbeat.AutoPong:
                ReadOnlyMemory<byte> pingPayload = frame.Payload;
                await WriteFrameAsync(writer => WsFrameEncoder.WriteMaskedPong(writer, pingPayload.Span), cancellationToken: ct).ConfigureAwait(false);
                break;

            case WsOpCode.Pong:
                _heartbeat?.OnPongReceived();
                break;

            case WsOpCode.Close:
                _disconnectReason = DisconnectReason.ClosedByServer;
                _closeReceived = true;
                _closeHandshake.TrySetResult();

                // Validates the code, the 1-byte body and the UTF-8 reason (RFC 6455 Sections 5.5.1
                // and 7.4.1). The read loop turns a violation into a 1002/1007 close.
                WsCloseStatus closeStatus = WsCloseFrame.ParseReceived(frame.Payload.Span);

                await SendCloseFrameAsync(WsCloseFrame.EchoFor(closeStatus), ct).ConfigureAwait(false);

                _state = ConnectionState.Closing;

                // RFC 6455 Section 7.1.1: the closing handshake is done, so the TCP connection goes
                // down instead of being left half-open until the reader happens to notice.
                ITransport? transport = _transport;
                if (transport is not null)
                {
                    try
                    {
                        await transport.CloseAsync(ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Transport close after peer Close frame failed");
                    }
                }

                break;
        }
    }

    /// <summary>
    /// Writes a Close frame unless one has already gone out on this connection. RFC 6455 Section 6.1:
    /// nothing may follow the Close frame, so the first status wins.
    /// </summary>
    private ValueTask SendCloseFrameAsync(WsCloseStatus status = WsCloseStatus.NormalClosure, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _closeFrameSent, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        return WriteFrameAsync(writer => WsFrameEncoder.WriteMaskedClose(writer, status), cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Thread-safe frame writing with write lock. All PipeWriter access MUST go through this method.
    /// </summary>
    internal ValueTask WriteFrameAsync(Action<PipeWriter> writeAction, int byteCount = 0, CancellationToken cancellationToken = default)
    {
        ITransport? transport = _transport;
        if (_state is not ConnectionState.Connected || transport is null)
        {
            return ValueTask.CompletedTask;
        }

        // Fast path: try to acquire lock synchronously (no contention).
        // A concurrent DisposeAsync can retire the lock between the state check above and here;
        // that is a closed connection, not a caller error, so it is reported as a no-op.
        bool acquired;
        try
        {
            acquired = _writeLock.Wait(0);
        }
        catch (ObjectDisposedException)
        {
            return ValueTask.CompletedTask;
        }

        if (acquired)
        {
            try
            {
                writeAction(transport.Output);
            }
            catch
            {
                _writeLock.Release();
                throw;
            }

            ValueTask<FlushResult> flushTask = transport.Output.FlushAsync(cancellationToken);
            if (flushTask.IsCompletedSuccessfully)
            {
                _writeLock.Release();
                if (byteCount > 0)
                {
                    Metrics.AddBytesSent(byteCount);
                }

                return ValueTask.CompletedTask;
            }

            return WriteFrameSlowFlushAsync(flushTask, byteCount);
        }

        // Slow path: lock contention — await lock
        return WriteFrameSlowLockAsync(writeAction, byteCount, cancellationToken);
    }

    private async ValueTask WriteFrameSlowFlushAsync(ValueTask<FlushResult> flushTask, int byteCount)
    {
        try
        {
            await flushTask.ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        if (byteCount > 0)
        {
            Metrics.AddBytesSent(byteCount);
        }
    }

    private async ValueTask WriteFrameSlowLockAsync(Action<PipeWriter> writeAction, int byteCount, CancellationToken cancellationToken)
    {
        try
        {
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            // The frame loop's teardown can retire the transport while this write was queued behind
            // the lock, so it is re-read here instead of being dereferenced blind.
            ITransport? transport = _transport;
            if (transport is null)
            {
                return;
            }

            writeAction(transport.Output);
            await transport.Output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        if (byteCount > 0)
        {
            Metrics.AddBytesSent(byteCount);
        }
    }

    /// <summary>Sends a binary WebSocket frame to the server (masked per RFC 6455).</summary>
    public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (_state is not ConnectionState.Connected)
        {
            throw new InvalidOperationException("Client is not connected.");
        }

        if (_deflate is not null && _deflate.ShouldCompress(data.Length))
        {
            byte[] compressed = _deflate.Compress(data.Span);
            return WriteFrameAsync(
                writer => WsFrameEncoder.WriteMaskedFrame(writer, WsOpCode.Binary, compressed, rsv1: true),
                compressed.Length, cancellationToken);
        }

        return WriteFrameAsync(
            writer => WsFrameEncoder.WriteMaskedBinary(writer, data.Span),
            data.Length, cancellationToken);
    }

    /// <summary>Sends a text WebSocket frame to the server (masked per RFC 6455).</summary>
    public ValueTask SendTextAsync(string text, CancellationToken cancellationToken = default)
    {
        if (_state is not ConnectionState.Connected)
        {
            throw new InvalidOperationException("Client is not connected.");
        }

        int byteCount = Encoding.UTF8.GetByteCount(text);
        byte[] rented = ArrayPool<byte>.Shared.Rent(byteCount);
        int written = Encoding.UTF8.GetBytes(text, rented);

        if (_deflate is not null && _deflate.ShouldCompress(written))
        {
            byte[] compressed = _deflate.Compress(rented.AsSpan(0, written));
            ArrayPool<byte>.Shared.Return(rented);
            return WriteFrameAsync(
                writer => WsFrameEncoder.WriteMaskedFrame(writer, WsOpCode.Text, compressed, rsv1: true),
                compressed.Length, cancellationToken);
        }

        ValueTask task = WriteFrameAsync(
            writer => WsFrameEncoder.WriteMaskedText(writer, rented.AsSpan(0, written)),
            written, cancellationToken);

        if (task.IsCompletedSuccessfully)
        {
            ArrayPool<byte>.Shared.Return(rented);
            return ValueTask.CompletedTask;
        }

        return ReturnBufferAfterWriteAsync(task, rented);
    }

    private static async ValueTask ReturnBufferAfterWriteAsync(ValueTask writeTask, byte[] rented)
    {
        try
        {
            await writeTask.ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Sends a text WebSocket frame from pre-encoded UTF-8 bytes.</summary>
    public ValueTask SendTextAsync(ReadOnlyMemory<byte> utf8Data, CancellationToken cancellationToken = default)
    {
        if (_state is not ConnectionState.Connected)
        {
            throw new InvalidOperationException("Client is not connected.");
        }

        if (_deflate is not null && _deflate.ShouldCompress(utf8Data.Length))
        {
            byte[] compressed = _deflate.Compress(utf8Data.Span);
            return WriteFrameAsync(
                writer => WsFrameEncoder.WriteMaskedFrame(writer, WsOpCode.Text, compressed, rsv1: true),
                compressed.Length, cancellationToken);
        }

        return WriteFrameAsync(
            writer => WsFrameEncoder.WriteMaskedText(writer, utf8Data.Span),
            utf8Data.Length, cancellationToken);
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
                    await RunFrameLoopAsync(ct).ConfigureAwait(false);
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

    /// <summary>
    /// Gracefully disconnects: sends a Close frame, waits for the server's Close (bounded by
    /// <see cref="WsClientOptions.CloseTimeout"/>), then tears the connection down.
    /// </summary>
    public Task DisconnectAsync(CancellationToken cancellationToken = default)
        => ShutdownAsync(WsCloseStatus.NormalClosure, waitForPeer: true, waitForRunTask: true, cancellationToken);

    /// <param name="waitForPeer">
    /// False when the peer is already known to be gone (heartbeat timeout): waiting for a Close frame
    /// that will never arrive would just stall teardown for the full close timeout.
    /// </param>
    /// <param name="waitForRunTask">
    /// False when called from a callback the run loop's teardown waits on, which would deadlock.
    /// </param>
    private async Task ShutdownAsync(WsCloseStatus status, bool waitForPeer, bool waitForRunTask, CancellationToken cancellationToken)
    {
        if (_state is ConnectionState.Connected)
        {
            // The Close frame goes out first: WriteFrameAsync refuses to write in any state other than
            // Connected, so flipping the state before sending makes the close a guaranteed no-op.
            using CancellationTokenSource closeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (_options.CloseTimeout > TimeSpan.Zero)
            {
                closeCts.CancelAfter(_options.CloseTimeout);
            }

            try
            {
                await SendCloseFrameAsync(status, closeCts.Token).ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }

            _state = ConnectionState.Closing;

            // RFC 6455 Section 7.1.4: the endpoint that starts the handshake waits for the peer's
            // Close before dropping TCP, otherwise the peer reports an abnormal 1006 closure.
            if (waitForPeer && !_closeReceived && _options.CloseTimeout > TimeSpan.Zero)
            {
                try
                {
                    await _closeHandshake.Task.WaitAsync(_options.CloseTimeout, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // Server never answered — fall through and close anyway.
                }
            }
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
        if (waitForRunTask && !_onRunLoop.Value && _runTask is Task runTask)
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

        if (_heartbeat is not null)
        {
            await _heartbeat.DisposeAsync().ConfigureAwait(false);
            _heartbeat = null;
        }

        _deflate?.Dispose();
        _writeLock.Dispose();
        _cts?.Dispose();
    }
}
