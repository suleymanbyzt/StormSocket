using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using StormSocket.Client;
using StormSocket.Core;
using StormSocket.Middleware.RateLimiting;
using StormSocket.Server;
using Xunit;

namespace StormSocket.Tests;

/// <summary>
/// Covers <c>Validate</c> on the option types: every rejection has to name the offending property,
/// and the configurations that are merely unusual have to keep working.
/// </summary>
public class OptionsValidationTests
{
    [Fact]
    public void Validate_RejectsInvalidOptions_WithAMessageNamingTheProperty()
    {
        (string Case, Action Validate, string Expected)[] cases =
        [
            ("TLS enabled without a certificate",
                () => new ServerOptions { Ssl = new SslOptions() }.Validate(),
                "SslOptions.Certificate"),

            ("frame larger than the message it belongs to",
                () => new ServerOptions { WebSocket = new WebSocketOptions { MaxFrameSize = 8192, MaxMessageSize = 1024 } }.Validate(),
                "WebSocketOptions.MaxFrameSize"),

            ("zero frame size",
                () => new ServerOptions { WebSocket = new WebSocketOptions { MaxFrameSize = 0 } }.Validate(),
                "WebSocketOptions.MaxFrameSize"),

            ("negative message size",
                () => new ServerOptions { WebSocket = new WebSocketOptions { MaxMessageSize = -1 } }.Validate(),
                "WebSocketOptions.MaxMessageSize"),

            ("zero request header budget",
                () => new ServerOptions { WebSocket = new WebSocketOptions { MaxRequestHeaderBytes = 0 } }.Validate(),
                "WebSocketOptions.MaxRequestHeaderBytes"),

            ("zero request header count",
                () => new ServerOptions { WebSocket = new WebSocketOptions { MaxRequestHeaderCount = 0 } }.Validate(),
                "WebSocketOptions.MaxRequestHeaderCount"),

            ("negative handshake timeout",
                () => new ServerOptions { WebSocket = new WebSocketOptions { HandshakeTimeout = TimeSpan.FromSeconds(-2) } }.Validate(),
                "WebSocketOptions.HandshakeTimeout"),

            ("negative close timeout",
                () => new ServerOptions { WebSocket = new WebSocketOptions { CloseTimeout = TimeSpan.FromSeconds(-2) } }.Validate(),
                "WebSocketOptions.CloseTimeout"),

            ("heartbeat that declares a peer dead before it can answer",
                () => new ServerOptions
                {
                    WebSocket = new WebSocketOptions
                    {
                        Heartbeat = new HeartbeatOptions { PingInterval = TimeSpan.FromSeconds(5), MaxMissedPongs = 0 },
                    },
                }.Validate(),
                "MaxMissedPongs"),

            ("zero backlog",
                () => new ServerOptions { Backlog = 0 }.Validate(),
                "ServerOptions.Backlog"),

            ("negative connection limit",
                () => new ServerOptions { MaxConnections = -1 }.Validate(),
                "ServerOptions.MaxConnections"),

            ("negative per-IP connection limit",
                () => new ServerOptions { MaxConnectionsPerIp = -1 }.Validate(),
                "ServerOptions.MaxConnectionsPerIp"),

            ("zero receive buffer",
                () => new ServerOptions { ReceiveBufferSize = 0 }.Validate(),
                "ServerOptions.ReceiveBufferSize"),

            ("negative send buffer",
                () => new ServerOptions { SendBufferSize = -1 }.Validate(),
                "ServerOptions.SendBufferSize"),

            ("negative TLS handshake timeout",
                () => new ServerOptions { TlsHandshakeTimeout = TimeSpan.FromSeconds(-1) }.Validate(),
                "ServerOptions.TlsHandshakeTimeout"),

            ("negative idle timeout",
                () => new ServerOptions { IdleTimeout = TimeSpan.FromSeconds(-1) }.Validate(),
                "ServerOptions.IdleTimeout"),

            ("negative shutdown drain timeout",
                () => new ServerOptions { ShutdownDrainTimeout = TimeSpan.FromSeconds(-1) }.Validate(),
                "ServerOptions.ShutdownDrainTimeout"),

            ("dual mode on a non-IP endpoint",
                () => new ServerOptions
                {
                    DualMode = true,
                    EndPoint = new UnixDomainSocketEndPoint(Path.Combine(Path.GetTempPath(), "storm-validate.sock")),
                }.Validate(),
                "ServerOptions.DualMode"),

            ("negative pending send budget",
                () => new ServerOptions { Socket = new SocketTuningOptions { MaxPendingSendBytes = -1 } }.Validate(),
                "SocketTuningOptions.MaxPendingSendBytes"),

            ("negative pending receive budget",
                () => new ServerOptions { Socket = new SocketTuningOptions { MaxPendingReceiveBytes = -1 } }.Validate(),
                "SocketTuningOptions.MaxPendingReceiveBytes"),

            ("zero keep-alive probe count",
                () => new ServerOptions { Socket = new SocketTuningOptions { KeepAliveProbeCount = 0 } }.Validate(),
                "SocketTuningOptions.KeepAliveProbeCount"),

            ("zero connect timeout on the TCP client",
                () => new ClientOptions { ConnectTimeout = TimeSpan.Zero }.Validate(),
                "ClientOptions.ConnectTimeout"),

            ("negative reconnect delay on the TCP client",
                () => new ClientOptions { Reconnect = new ReconnectOptions { Delay = TimeSpan.FromSeconds(-1) } }.Validate(),
                "Delay"),

            ("non-WebSocket scheme on the WebSocket client",
                () => new WsClientOptions { Uri = new Uri("https://localhost:8080") }.Validate(),
                "WsClientOptions.Uri"),

            ("frame larger than the message on the WebSocket client",
                () => new WsClientOptions { MaxFrameSize = 8192, MaxMessageSize = 1024 }.Validate(),
                "WsClientOptions.MaxFrameSize"),

            ("negative close timeout on the WebSocket client",
                () => new WsClientOptions { CloseTimeout = TimeSpan.FromSeconds(-1) }.Validate(),
                "WsClientOptions.CloseTimeout"),

            ("zero rate limit window",
                () => new RateLimitOptions { Window = TimeSpan.Zero }.Validate(),
                "RateLimitOptions.Window"),

            ("zero rate limit allowance",
                () => new RateLimitOptions { MaxMessages = 0 }.Validate(),
                "RateLimitOptions.MaxMessages"),
        ];

        foreach ((string name, Action validate, string expected) in cases)
        {
            ArgumentException ex = Assert.ThrowsAny<ArgumentException>(validate);
            Assert.True(
                ex.Message.Contains(expected, StringComparison.Ordinal),
                $"'{name}': the message does not name '{expected}'. Actual message: {ex.Message}");
        }
    }

    [Fact]
    public void Validate_AcceptsTheDefaults()
    {
        new ServerOptions().Validate();
        new ClientOptions().Validate();
        new WsClientOptions().Validate();
        new RateLimitOptions().Validate();
        new SocketTuningOptions().Validate();
        new WebSocketOptions().Validate();
    }

    [Fact]
    public void Validate_AcceptsTheDisabledAndInfiniteSentinels()
    {
        new ServerOptions
        {
            TlsHandshakeTimeout = Timeout.InfiniteTimeSpan,
            IdleTimeout = TimeSpan.Zero,
            ShutdownDrainTimeout = Timeout.InfiniteTimeSpan,
            MaxConnections = 0,
            MaxConnectionsPerIp = 0,
            WebSocket = new WebSocketOptions
            {
                HandshakeTimeout = Timeout.InfiniteTimeSpan,
                IdleTimeout = TimeSpan.Zero,
                CloseTimeout = TimeSpan.Zero,
                Heartbeat = new HeartbeatOptions { PingInterval = TimeSpan.Zero, MaxMissedPongs = 0 },
            },
        }.Validate();

        new ServerOptions { ShutdownDrainTimeout = TimeSpan.Zero }.Validate();
    }

    [Fact]
    public void Validate_AcceptsAMessageLimitBelowTheDefaultFrameLimit()
    {
        // Lowering only MaxMessageSize is ordinary configuration: the fragment assembler stops at the
        // message limit, so the untouched default frame size is not a contradiction.
        new ServerOptions { WebSocket = new WebSocketOptions { MaxMessageSize = 100 } }.Validate();
        new WsClientOptions { MaxMessageSize = 100 }.Validate();
    }

    [Fact]
    public async Task Validate_AcceptsTlsOverAUnixDomainSocket_BecauseItWorks()
    {
        string socketPath = Path.Combine(Path.GetTempPath(), $"storm_tls_uds_{Guid.NewGuid():N}.sock");
        using X509Certificate2 certificate = CreateSelfSignedCertificate();

        new ServerOptions
        {
            EndPoint = new UnixDomainSocketEndPoint(socketPath),
            Ssl = new SslOptions { Certificate = certificate },
        }.Validate();

        await using StormTcpServer server = new(new ServerOptions
        {
            EndPoint = new UnixDomainSocketEndPoint(socketPath),
            Ssl = new SslOptions { Certificate = certificate },
        });
        server.OnDataReceived += async (session, data) => await session.SendAsync(data);
        await server.StartAsync();

        try
        {
            using Socket client = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await client.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));

            await using NetworkStream stream = new(client, ownsSocket: false);
            using SslStream ssl = new(stream, leaveInnerStreamOpen: false, (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync("localhost");

            byte[] sent = "tls over uds"u8.ToArray();
            await ssl.WriteAsync(sent);
            await ssl.FlushAsync();

            byte[] buffer = new byte[64];
            int read = await ssl.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(sent, buffer[..read]);
        }
        finally
        {
            await server.StopAsync();
            File.Delete(socketPath);
        }
    }

    [Fact]
    public async Task TcpServer_WarnsThatWebSocketOptionsAreIgnored()
    {
        CapturingLoggerFactory loggerFactory = new();

        await using StormTcpServer server = new(new ServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            WebSocket = new WebSocketOptions(),
            LoggerFactory = loggerFactory,
        });

        await server.StartAsync();
        await server.StopAsync();

        Assert.Contains(loggerFactory.Warnings, warning => warning.Contains("ServerOptions.WebSocket", StringComparison.Ordinal));
    }

    private static X509Certificate2 CreateSelfSignedCertificate()
    {
        using RSA rsa = RSA.Create(2048);
        CertificateRequest request = new("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        SubjectAlternativeNameBuilder san = new();
        san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());

        X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365));

        // The private key has to survive the round-trip for the server-side handshake to use it.
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);
    }

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        private readonly List<string> _warnings = [];

        public IReadOnlyList<string> Warnings
        {
            get
            {
                lock (_warnings)
                {
                    return [.. _warnings];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private void Record(string message)
        {
            lock (_warnings)
            {
                _warnings.Add(message);
            }
        }

        private sealed class CapturingLogger(CapturingLoggerFactory owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel is LogLevel.Warning)
                {
                    owner.Record(formatter(state, exception));
                }
            }
        }
    }
}
