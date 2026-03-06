using System.Buffers;
using System.Buffers.Binary;
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
    private readonly WsClientOptions _options;
    private readonly ILogger _logger;
    private readonly MiddlewarePipeline _pipeline = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private ITransport? _transport;
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private WsHeartbeat? _heartbeat;
    private WsPerMessageDeflate? _deflate;
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
    public event ClientConnectedHandler? OnConnected;

    /// <summary>Fired when disconnected from the server.</summary>
    public event ClientDisconnectedHandler? OnDisconnected;

    /// <summary>Fired when a complete text or binary message is received.</summary>
    public event ClientWsMessageReceivedHandler? OnMessageReceived;

    /// <summary>Fired when an error occurs.</summary>
    public event ClientErrorHandler? OnError;

    /// <summary>Fired when attempting to reconnect.</summary>
    public event ClientReconnectingHandler? OnReconnecting;

    public StormWebSocketClient(WsClientOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = (options.LoggerFactory ?? NullLoggerFactory.Instance).CreateLogger<StormWebSocketClient>();
    }

    /// <summary>Registers a middleware that intercepts connection lifecycle and data flow.</summary>
    public void UseMiddleware(IConnectionMiddleware middleware) => _pipeline.Use(middleware);

    /// <summary>Connects to the WebSocket server. If auto-reconnect is enabled, reconnects on disconnect.</summary>
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
            _runTask = RunFrameLoopAsync(_cts.Token);
        }
    }

    private async Task ConnectCoreAsync(CancellationToken ct)
    {
        _logger.LogInformation("Connecting to {Uri}", _options.Uri);
        _state = ConnectionState.Connecting;
        _disconnectReason = DisconnectReason.None;
        Metrics = new ConnectionMetrics();

        Uri uri = _options.Uri;
        bool useSsl = uri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase);
        string host = uri.Host;
        int port = uri.Port > 0 ? uri.Port : (useSsl ? 443 : 80);

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
            throw lastEx ?? new SocketException((int)SocketError.HostUnreachable);
        }

        RemoteEndPoint = socket.RemoteEndPoint;

        ITransport transport;
        if (useSsl || _options.Ssl is not null)
        {
            string targetHost = _options.Ssl?.TargetHost ?? host;
            transport = new SslTransport(
                socket,
                targetHost,
                _options.Ssl?.Protocols ?? System.Security.Authentication.SslProtocols.None,
                _options.Ssl?.RemoteCertificateValidation,
                _options.Ssl?.ClientCertificate);
        }
        else
        {
            transport = new TcpTransport(socket, _options.Socket.MaxPendingReceiveBytes, _options.Socket.MaxPendingSendBytes);
        }

        await transport.HandshakeAsync(ct).ConfigureAwait(false);

        // Build extension offer for permessage-deflate if enabled
        string? extensionOffer = _options.Compression.Enabled
            ? WsPerMessageDeflate.BuildOfferHeader(_options.Compression)
            : null;

        (byte[] request, string wsKey) = WsUpgradeHandler.BuildUpgradeRequest(uri, _options.Headers, extensionOffer, _options.Subprotocols);
        Span<byte> requestSpan = transport.Output.GetSpan(request.Length);
        request.CopyTo(requestSpan);
        transport.Output.Advance(request.Length);
        await transport.Output.FlushAsync(ct).ConfigureAwait(false);

        (bool upgraded, string? serverExtensions, string? negotiatedSubprotocol) = await WaitForUpgradeResponseAsync(transport.Input, wsKey, ct).ConfigureAwait(false);
        if (!upgraded)
        {
            await transport.DisposeAsync().ConfigureAwait(false);
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
                    writer => WsFrameEncoder.WriteMaskedPing(writer), cancellationToken: ct2),
                _options.Heartbeat.PingInterval,
                _options.Heartbeat.MaxMissedPongs,
                _logger);
            _heartbeat.OnTimeout = async () =>
            {
                _logger.LogWarning("Heartbeat timeout");
                _disconnectReason = DisconnectReason.HeartbeatTimeout;
                await DisconnectAsync().ConfigureAwait(false);
            };
            _heartbeat.Start();
        }

        if (transport is TcpTransport tcp)
        {
            tcp.OnSocketError = error =>
            {
                OnError?.Invoke(new SocketException((int)error));
            };
        }

        ClientSessionAdapter sessionAdapter = new ClientSessionAdapter(this);
        await _pipeline.OnConnectedAsync(sessionAdapter).ConfigureAwait(false);
        if (OnConnected is not null)
        {
            await OnConnected.Invoke().ConfigureAwait(false);
        }
    }

    private static async Task<(bool Success, string? Extensions, string? Subprotocol)> WaitForUpgradeResponseAsync(PipeReader reader, string wsKey, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            ReadResult result = await reader.ReadAsync(ct).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;

            if (WsUpgradeHandler.TryParseUpgradeResponse(ref buffer, wsKey, out string? extensions, out string? subprotocol))
            {
                reader.AdvanceTo(buffer.Start, buffer.End);
                return (true, extensions, subprotocol);
            }

            reader.AdvanceTo(buffer.Start, buffer.End);

            if (result.IsCompleted)
            {
                return (false, null, null);
            }
        }

        return (false, null, null);
    }

    private async Task RunFrameLoopAsync(CancellationToken ct)
    {
        ClientSessionAdapter sessionAdapter = new ClientSessionAdapter(this);
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
                    while (WsFrameDecoder.TryDecodeFrame(ref buffer, out WsFrame frame, _options.MaxFrameSize, allowCompressedFrames: hasCompression))
                    {
                        if (frame.IsControl)
                        {
                            await HandleFrameAsync(sessionAdapter, frame, ct).ConfigureAwait(false);
                        }
                        else
                        {
                            WsMessage? message = assembler.TryAssemble(in frame);
                            if (message is not null)
                            {
                                WsMessage msg = message.Value;
                                if (msg.Compressed && _deflate is not null)
                                {
                                    byte[] decompressed = _deflate.Decompress(msg.Data.Span);
                                    msg = new WsMessage { Data = decompressed, IsText = msg.IsText };
                                }
                                await HandleClientMessageAsync(sessionAdapter, msg).ConfigureAwait(false);
                            }
                        }
                    }
                }
                catch (WsProtocolException ex)
                {
                    _logger.LogWarning(ex, "Protocol error");
                    _disconnectReason = DisconnectReason.ProtocolError;
                    await WriteFrameAsync(writer => WsFrameEncoder.WriteMaskedClose(writer, ex.CloseStatus), cancellationToken: ct);
                    await _pipeline.OnErrorAsync(sessionAdapter, ex).ConfigureAwait(false);
                    if (OnError is not null)
                    {
                        await OnError.Invoke(ex).ConfigureAwait(false);
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
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_disconnectReason == DisconnectReason.None)
                _disconnectReason = DisconnectReason.TransportError;
            _logger.LogError(ex, "Transport error");
            await _pipeline.OnErrorAsync(sessionAdapter, ex).ConfigureAwait(false);
            if (OnError is not null)
            {
                await OnError.Invoke(ex).ConfigureAwait(false);
            }
        }
        finally
        {
            // Default: if no specific reason was set, the server closed the connection
            if (_disconnectReason == DisconnectReason.None)
                _disconnectReason = DisconnectReason.ClosedByServer;

            _state = ConnectionState.Closed;

            if (_heartbeat is not null)
            {
                await _heartbeat.DisposeAsync().ConfigureAwait(false);
                _heartbeat = null;
            }

            DisconnectReason reason = _disconnectReason;
            _logger.LogInformation("Disconnected: {Reason}", reason);
            await _pipeline.OnDisconnectedAsync(sessionAdapter, reason).ConfigureAwait(false);
            if (OnDisconnected is not null)
            {
                await OnDisconnected.Invoke(reason).ConfigureAwait(false);
            }

            if (_transport is not null)
            {
                await _transport.DisposeAsync().ConfigureAwait(false);
                _transport = null;
            }
        }
    }

    private async ValueTask HandleClientMessageAsync(ClientSessionAdapter sessionAdapter, WsMessage msg)
    {
        Metrics.AddBytesReceived(msg.Data.Length);

        ReadOnlyMemory<byte> processed = await _pipeline.OnDataReceivedAsync(sessionAdapter, msg.Data).ConfigureAwait(false);
        if (processed.IsEmpty)
        {
            return;
        }

        if (OnMessageReceived is not null)
        {
            await OnMessageReceived.Invoke(msg).ConfigureAwait(false);
        }
    }

    private async ValueTask HandleFrameAsync(ClientSessionAdapter sessionAdapter, WsFrame frame, CancellationToken ct)
    {
        switch (frame.OpCode)
        {
            case WsOpCode.Ping when _options.Heartbeat.AutoPong:
                ReadOnlyMemory<byte> pingPayload = frame.Payload;
                await WriteFrameAsync(writer => WsFrameEncoder.WriteMaskedPong(writer, pingPayload.Span), cancellationToken: ct);
                break;

            case WsOpCode.Pong:
                _heartbeat?.OnPongReceived();
                break;

            case WsOpCode.Close:
                _disconnectReason = DisconnectReason.ClosedByServer;

                WsCloseStatus closeStatus = WsCloseStatus.NormalClosure;
                if (frame.Payload.Length >= 2)
                {
                    closeStatus = (WsCloseStatus)BinaryPrimitives.ReadUInt16BigEndian(frame.Payload.Span);
                }

                WsCloseStatus echoStatus = closeStatus;
                await WriteFrameAsync(writer => WsFrameEncoder.WriteMaskedClose(writer, echoStatus), cancellationToken: ct);
                break;
        }
    }

    /// <summary>
    /// Thread-safe frame writing with write lock. All PipeWriter access MUST go through this method.
    /// </summary>
    internal ValueTask WriteFrameAsync(Action<PipeWriter> writeAction, int byteCount = 0, CancellationToken cancellationToken = default)
    {
        if (_state is not ConnectionState.Connected || _transport is null)
        {
            return ValueTask.CompletedTask;
        }

        // Fast path: try to acquire lock synchronously (no contention)
        if (_writeLock.Wait(0))
        {
            writeAction(_transport.Output);
            ValueTask<FlushResult> flushTask = _transport.Output.FlushAsync(cancellationToken);
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
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            writeAction(_transport!.Output);
            await _transport.Output.FlushAsync(cancellationToken).ConfigureAwait(false);
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
                await RunFrameLoopAsync(ct).ConfigureAwait(false);
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
                _logger.LogWarning("Max reconnect attempts ({MaxAttempts}) reached", _options.Reconnect.MaxAttempts);
                firstConnect?.TrySetException(new InvalidOperationException(
                    $"Max reconnect attempts ({_options.Reconnect.MaxAttempts}) exceeded."));
                break;
            }

            _logger.LogDebug("Reconnect attempt {Attempt} in {Delay}", attempt, _options.Reconnect.Delay);
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



    /// <summary>Gracefully disconnects, sending a Close frame.</summary>
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_state is ConnectionState.Closing or ConnectionState.Closed)
        {
            return;
        }

        _state = ConnectionState.Closing;

        try
        {
            await WriteFrameAsync(writer => WsFrameEncoder.WriteMaskedClose(writer), cancellationToken: cancellationToken);
        }
        catch
        {
            // ignored
        }

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
        _deflate?.Dispose();
        _writeLock.Dispose();
        _cts?.Dispose();
    }
}