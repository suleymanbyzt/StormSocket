using System.Net;
using StormSocket.Core;
using StormSocket.Framing;

namespace StormSocket.Client;

/// <summary>
/// Configuration for <see cref="StormTcpClient"/>.
/// </summary>
public sealed class ClientOptions
{
    /// <summary>The server endpoint to connect to. Default: 127.0.0.1:5000.</summary>
    public IPEndPoint EndPoint { get; init; } = new(IPAddress.Loopback, 5000);

    /// <summary>Set to enable SSL/TLS encryption. Null = plain TCP.</summary>
    public ClientSslOptions? Ssl { get; init; }

    /// <summary>Message framing strategy. Null = raw bytes (no framing).</summary>
    public IMessageFramer? Framer { get; init; }

    /// <summary>Connection timeout. Default: 10 seconds.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Low-level TCP socket tuning (NoDelay, KeepAlive, backpressure limits).</summary>
    public SocketTuningOptions Socket { get; init; } = new();

    /// <summary>Auto-reconnect settings.</summary>
    public ReconnectOptions Reconnect { get; init; } = new();
}