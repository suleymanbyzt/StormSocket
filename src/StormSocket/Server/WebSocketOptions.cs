using StormSocket.Core;
using StormSocket.WebSocket;

namespace StormSocket.Server;

public sealed class WebSocketOptions
{
    private int _maxFrameSize = 1024 * 1024;
    private bool _maxFrameSizeAssigned;

    /// <summary>
    /// Maximum allowed frame payload size in bytes. Frames larger than this will throw.
    /// Default: 1 MB.
    /// </summary>
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

    /// <summary>
    /// List of allowed origins for CSWSH protection (RFC 6455 10.2).
    /// If empty or null, all origins are allowed (default, for non-browser use cases).
    /// </summary>
    /// <example>
    /// <code>
    /// AllowedOrigins = ["https://myapp.com", "https://staging.myapp.com"]
    /// </code>
    /// </example>
    public IReadOnlyList<string>? AllowedOrigins { get; set; }

    /// <summary>
    /// Maximum time to wait for the client to complete the WebSocket upgrade handshake
    /// after the TCP connection is accepted. Connections that don't upgrade within this
    /// window are closed. Set to <see cref="Timeout.InfiniteTimeSpan"/> to disable.
    /// Default: 5 seconds.
    /// </summary>
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Ping/pong heartbeat and dead connection detection settings.</summary>
    public HeartbeatOptions Heartbeat { get; set; } = new();

    /// <summary>Permessage-deflate compression settings (RFC 7692). Disabled by default.</summary>
    public WsCompressionOptions Compression { get; set; } = new();

    /// <summary>
    /// Maximum time a connection can remain idle (no application-level messages received)
    /// before being automatically closed. Ping/pong frames do NOT reset the timer.
    /// Set to <see cref="TimeSpan.Zero"/> to disable. Default: disabled.
    /// </summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Maximum total size of the HTTP upgrade request (request line plus all headers). Default: 16 KB.
    /// </summary>
    /// <remarks>
    /// Without this cap a single client can stream headers forever and force the server to buffer and
    /// rescan them, which costs far more memory and CPU on the server than it costs the attacker.
    /// Connections that exceed it are answered with 431 and closed.
    /// </remarks>
    public int MaxRequestHeaderBytes { get; set; } = 16 * 1024;

    /// <summary>Maximum number of headers accepted in the upgrade request. Default: 100.</summary>
    public int MaxRequestHeaderCount { get; set; } = 100;

    /// <summary>
    /// How long to wait for the peer's Close frame after this endpoint starts the closing handshake
    /// (RFC 6455 Section 7.1.4). Set to <see cref="TimeSpan.Zero"/> to drop TCP immediately. Default: 5 seconds.
    /// </summary>
    public TimeSpan CloseTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Verifies the WebSocket limits and timeouts. Called by <see cref="ServerOptions.Validate"/>.
    /// </summary>
    /// <exception cref="ArgumentException">A nested options object is missing.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A size, count or duration is outside its allowed range.</exception>
    public void Validate()
    {
        OptionsValidation.RequirePositive(MaxFrameSize, nameof(WebSocketOptions), nameof(MaxFrameSize));
        OptionsValidation.RequirePositive(MaxMessageSize, nameof(WebSocketOptions), nameof(MaxMessageSize));
        OptionsValidation.RequirePositive(MaxRequestHeaderBytes, nameof(WebSocketOptions), nameof(MaxRequestHeaderBytes));
        OptionsValidation.RequirePositive(MaxRequestHeaderCount, nameof(WebSocketOptions), nameof(MaxRequestHeaderCount));
        OptionsValidation.RequirePositiveDuration(HandshakeTimeout, nameof(WebSocketOptions), nameof(HandshakeTimeout), allowInfinite: true);
        OptionsValidation.RequireNonNegativeDuration(IdleTimeout, nameof(WebSocketOptions), nameof(IdleTimeout));
        OptionsValidation.RequireNonNegativeDuration(CloseTimeout, nameof(WebSocketOptions), nameof(CloseTimeout));
        OptionsValidation.ValidateHeartbeat(Heartbeat, nameof(WebSocketOptions));

        // Only a frame size the caller chose can contradict the message size. Leaving the default in
        // place while lowering MaxMessageSize is an ordinary configuration — the fragment assembler
        // already stops at the message limit, so the effective frame cap is the smaller of the two.
        if (_maxFrameSizeAssigned && MaxFrameSize > MaxMessageSize)
        {
            throw new ArgumentException(
                $"WebSocketOptions.MaxFrameSize ({MaxFrameSize}) must not exceed WebSocketOptions.MaxMessageSize ({MaxMessageSize}): a single frame is part of a message, so it can never legitimately be larger than the message it belongs to.",
                nameof(MaxFrameSize));
        }
    }
}
