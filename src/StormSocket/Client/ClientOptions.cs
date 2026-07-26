using System.Net;
using Microsoft.Extensions.Logging;
using StormSocket.Core;
using StormSocket.Framing;

namespace StormSocket.Client;

/// <summary>
/// Configuration for <see cref="StormTcpClient"/>.
/// </summary>
public sealed class ClientOptions
{
    /// <summary>
    /// The server endpoint to connect to. Accepts <see cref="IPEndPoint"/> for TCP/IP or
    /// <see cref="System.Net.Sockets.UnixDomainSocketEndPoint"/> for Unix domain sockets.
    /// Default: 127.0.0.1:5000.
    /// </summary>
    public EndPoint EndPoint { get; set; } = new IPEndPoint(IPAddress.Loopback, 5000);

    /// <summary>Set to enable SSL/TLS encryption. Null = plain TCP.</summary>
    public ClientSslOptions? Ssl { get; set; }

    /// <summary>Message framing strategy. Null = raw bytes (no framing).</summary>
    public IMessageFramer? Framer { get; set; }

    /// <summary>
    /// Budget for the whole connect sequence — the TCP connect and, when configured, the TLS
    /// handshake. Default: 10 seconds.
    /// </summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Low-level TCP socket tuning (NoDelay, KeepAlive, backpressure limits).</summary>
    public SocketTuningOptions Socket { get; set; } = new();

    /// <summary>Auto-reconnect settings.</summary>
    public ReconnectOptions Reconnect { get; set; } = new();

    /// <summary>Optional logger factory for structured logging. Null = no logging (zero overhead).</summary>
    public ILoggerFactory? LoggerFactory { get; set; }

    /// <summary>
    /// Checks the configuration for values the client cannot use.
    /// </summary>
    /// <remarks>
    /// <see cref="StormTcpClient.ConnectAsync"/> does not call this itself; call it before connecting
    /// (or once, at composition time) to turn a misconfiguration into a clear startup failure.
    /// </remarks>
    /// <exception cref="ArgumentException">A property is set to a value that is unusable in this combination.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A duration or limit is outside its allowed range.</exception>
    public void Validate()
    {
        if (EndPoint is null)
        {
            throw new ArgumentException("ClientOptions.EndPoint must be set to the server endpoint to connect to.", nameof(EndPoint));
        }

        if (Socket is null)
        {
            throw new ArgumentException("ClientOptions.Socket must not be null. Leave it at its default to use the standard socket tuning.", nameof(Socket));
        }

        if (Reconnect is null)
        {
            throw new ArgumentException("ClientOptions.Reconnect must not be null. Leave it at its default to keep auto-reconnect disabled.", nameof(Reconnect));
        }

        OptionsValidation.RequirePositiveDuration(ConnectTimeout, nameof(ClientOptions), nameof(ConnectTimeout), allowInfinite: true);
        OptionsValidation.RequireNonNegativeDuration(Reconnect.Delay, $"{nameof(ClientOptions)}.{nameof(Reconnect)}", nameof(ReconnectOptions.Delay));
        OptionsValidation.RequireNonNegative(Reconnect.MaxAttempts, $"{nameof(ClientOptions)}.{nameof(Reconnect)}", nameof(ReconnectOptions.MaxAttempts));
        Socket.Validate();
    }
}
