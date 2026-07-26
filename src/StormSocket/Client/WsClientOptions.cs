using Microsoft.Extensions.Logging;
using StormSocket.Core;
using StormSocket.WebSocket;

namespace StormSocket.Client;

/// <summary>
/// Configuration for <see cref="StormWebSocketClient"/>.
/// </summary>
public sealed class WsClientOptions
{
    private int _maxFrameSize = 1024 * 1024;
    private bool _maxFrameSizeAssigned;

    /// <summary>The WebSocket URI to connect to (ws:// or wss://).</summary>
    public Uri Uri { get; set; } = new("ws://localhost:8080");

    /// <summary>
    /// Budget for the whole connect sequence — DNS, the TCP connect, the TLS handshake and waiting
    /// for the server's <c>101 Switching Protocols</c> response. Default: 10 seconds.
    /// </summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a graceful disconnect waits for the server's Close frame before dropping the
    /// transport (RFC 6455 Section 7.1.4). Also bounds writing the Close frame itself, so an
    /// unresponsive peer cannot stall teardown. Zero closes without waiting. Default: 5 seconds.
    /// </summary>
    public TimeSpan CloseTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Maximum allowed frame payload size. Default: 1 MB.</summary>
    public int MaxFrameSize
    {
        get => _maxFrameSize;
        set
        {
            _maxFrameSize = value;
            _maxFrameSizeAssigned = true;
        }
    }

    /// <summary>
    /// Maximum total size of a reassembled WebSocket message across all fragments.
    /// Messages exceeding this limit will trigger a close with status 1009 (MessageTooBig).
    /// Default: 4 MB.
    /// </summary>
    public int MaxMessageSize { get; set; } = 4 * 1024 * 1024;

    /// <summary>Additional HTTP headers to send during the WebSocket upgrade request.</summary>
    public Dictionary<string, string>? Headers { get; set; }

    /// <summary>Subprotocols to request during the WebSocket upgrade handshake (Sec-WebSocket-Protocol). Server selects one.</summary>
    public IReadOnlyList<string>? Subprotocols { get; set; }

    /// <summary>SSL options for wss:// connections. Inferred from scheme if null.</summary>
    public ClientSslOptions? Ssl { get; set; }

    /// <summary>Low-level TCP socket tuning (NoDelay, KeepAlive, backpressure limits).</summary>
    public SocketTuningOptions Socket { get; set; } = new();

    /// <summary>Ping/pong heartbeat and dead connection detection settings.</summary>
    public HeartbeatOptions Heartbeat { get; set; } = new();

    /// <summary>Auto-reconnect settings.</summary>
    public ReconnectOptions Reconnect { get; set; } = new();

    /// <summary>Permessage-deflate compression settings (RFC 7692). Disabled by default.</summary>
    public WsCompressionOptions Compression { get; set; } = new();

    /// <summary>Optional logger factory for structured logging. Null = no logging (zero overhead).</summary>
    public ILoggerFactory? LoggerFactory { get; set; }

    /// <summary>
    /// Checks the configuration for values the client cannot use.
    /// </summary>
    /// <remarks>
    /// <see cref="StormWebSocketClient.ConnectAsync"/> does not call this itself; call it before
    /// connecting (or once, at composition time) to turn a misconfiguration into a clear startup
    /// failure instead of a connect that fails or silently skips TLS.
    /// </remarks>
    /// <exception cref="ArgumentException">A property is set to a value that is unusable in this combination.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A size, count or duration is outside its allowed range.</exception>
    public void Validate()
    {
        if (Uri is null)
        {
            throw new ArgumentException("WsClientOptions.Uri must be set to the WebSocket endpoint to connect to (ws:// or wss://).", nameof(Uri));
        }

        if (!Uri.IsAbsoluteUri)
        {
            throw new ArgumentException($"WsClientOptions.Uri must be an absolute URI, but '{Uri}' is relative. Use a full ws:// or wss:// address.", nameof(Uri));
        }

        // Only "wss" turns on TLS, so an http/https URI would connect in the clear without complaint.
        if (!Uri.Scheme.Equals("ws", StringComparison.OrdinalIgnoreCase) &&
            !Uri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"WsClientOptions.Uri must use the ws or wss scheme, but '{Uri.Scheme}' was given. Only wss enables TLS; any other scheme would connect unencrypted.",
                nameof(Uri));
        }

        if (Socket is null)
        {
            throw new ArgumentException("WsClientOptions.Socket must not be null. Leave it at its default to use the standard socket tuning.", nameof(Socket));
        }

        if (Reconnect is null)
        {
            throw new ArgumentException("WsClientOptions.Reconnect must not be null. Leave it at its default to keep auto-reconnect disabled.", nameof(Reconnect));
        }

        OptionsValidation.RequirePositiveDuration(ConnectTimeout, nameof(WsClientOptions), nameof(ConnectTimeout), allowInfinite: true);
        OptionsValidation.RequireNonNegativeDuration(CloseTimeout, nameof(WsClientOptions), nameof(CloseTimeout));
        OptionsValidation.RequirePositive(MaxFrameSize, nameof(WsClientOptions), nameof(MaxFrameSize));
        OptionsValidation.RequirePositive(MaxMessageSize, nameof(WsClientOptions), nameof(MaxMessageSize));
        OptionsValidation.RequireNonNegativeDuration(Reconnect.Delay, $"{nameof(WsClientOptions)}.{nameof(Reconnect)}", nameof(ReconnectOptions.Delay));
        OptionsValidation.RequireNonNegative(Reconnect.MaxAttempts, $"{nameof(WsClientOptions)}.{nameof(Reconnect)}", nameof(ReconnectOptions.MaxAttempts));
        OptionsValidation.ValidateHeartbeat(Heartbeat, nameof(WsClientOptions));
        Socket.Validate();

        // As on the server: only a frame size the caller chose can contradict the message size.
        if (_maxFrameSizeAssigned && MaxFrameSize > MaxMessageSize)
        {
            throw new ArgumentException(
                $"WsClientOptions.MaxFrameSize ({MaxFrameSize}) must not exceed WsClientOptions.MaxMessageSize ({MaxMessageSize}): a single frame is part of a message, so it can never legitimately be larger than the message it belongs to.",
                nameof(MaxFrameSize));
        }
    }
}
