using System.Buffers;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using StormSocket.Client;
using StormSocket.Core;
using StormSocket.Server;
using StormSocket.Session;
using StormSocket.Transport;
using StormSocket.WebSocket;
using Xunit;

namespace StormSocket.Tests;

public class SslTransportTests
{
    private static int _nextPort = 18000;
    private static int GetPort() => Interlocked.Increment(ref _nextPort);

    private static X509Certificate2 CreateSelfSignedCert()
    {
        using RSA rsa = RSA.Create(2048);
        CertificateRequest request = new("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));

        SubjectAlternativeNameBuilder san = new();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());

        X509Certificate2 cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365));

        // On macOS/Linux we need to export and re-import to make the private key exportable
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    }

    [Fact]
    public async Task SslTransport_ServerClientHandshake_Echo()
    {
        int port = GetPort();
        X509Certificate2 cert = CreateSelfSignedCert();

        // Start SSL server
        TcpListener listener = new(IPAddress.Loopback, port);
        listener.Start();

        Task<byte[]> serverTask = Task.Run(async () =>
        {
            Socket serverSocket = await listener.AcceptSocketAsync();
            SslTransport serverTransport = new(serverSocket, cert, SslProtocols.None, false);
            await serverTransport.HandshakeAsync();

            // Read data
            var result = await serverTransport.Input.ReadAsync();
            byte[] data = result.Buffer.ToArray();
            serverTransport.Input.AdvanceTo(result.Buffer.End);

            // Echo back
            var span = serverTransport.Output.GetSpan(data.Length);
            data.CopyTo(span);
            serverTransport.Output.Advance(data.Length);
            await serverTransport.Output.FlushAsync();

            await Task.Delay(100); // let data flow
            await serverTransport.DisposeAsync();
            return data;
        });

        // Connect as SSL client
        Socket clientSocket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await clientSocket.ConnectAsync(IPAddress.Loopback, port);

        SslTransport clientTransport = new(
            clientSocket,
            "localhost",
            SslProtocols.None,
            (sender, certificate, chain, errors) => true); // trust self-signed

        await clientTransport.HandshakeAsync();

        // Send data
        byte[] sendData = "Hello SSL"u8.ToArray();
        var clientSpan = clientTransport.Output.GetSpan(sendData.Length);
        sendData.CopyTo(clientSpan);
        clientTransport.Output.Advance(sendData.Length);
        await clientTransport.Output.FlushAsync();

        // Read echo
        var readResult = await clientTransport.Input.ReadAsync();
        byte[] received = readResult.Buffer.ToArray();
        clientTransport.Input.AdvanceTo(readResult.Buffer.End);

        Assert.Equal(sendData, received);

        byte[] serverReceived = await serverTask;
        Assert.Equal(sendData, serverReceived);

        await clientTransport.DisposeAsync();
        listener.Stop();
        cert.Dispose();
    }

    [Fact]
    public async Task TcpServer_WithSsl_Echo()
    {
        int port = GetPort();
        X509Certificate2 cert = CreateSelfSignedCert();
        TaskCompletionSource<byte[]> received = new();

        await using StormTcpServer server = new(new ServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, port),
            Ssl = new SslOptions { Certificate = cert },
        });

        server.OnDataReceived += async (session, data) =>
        {
            await session.SendAsync(data);
        };

        await server.StartAsync();

        // Connect with raw SSL client
        using TcpClient raw = new();
        await raw.ConnectAsync(IPAddress.Loopback, port);
        SslStream ssl = new(raw.GetStream(), false, (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync("localhost");

        byte[] sendData = "SSL echo test"u8.ToArray();
        await ssl.WriteAsync(sendData);
        await ssl.FlushAsync();

        byte[] buffer = new byte[1024];
        int read = await ssl.ReadAsync(buffer);

        Assert.Equal(sendData.Length, read);
        Assert.Equal(sendData, buffer[..read]);

        ssl.Dispose();
        cert.Dispose();
    }

    [Fact]
    public async Task TcpClient_WithSsl_ConnectAndEcho()
    {
        int port = GetPort();
        X509Certificate2 cert = CreateSelfSignedCert();
        TaskCompletionSource<byte[]> clientReceived = new();

        await using StormTcpServer server = new(new ServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, port),
            Ssl = new SslOptions { Certificate = cert },
        });
        server.OnDataReceived += async (session, data) =>
        {
            await session.SendAsync(data);
        };
        await server.StartAsync();

        await using StormTcpClient client = new(new ClientOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, port),
            Ssl = new ClientSslOptions
            {
                TargetHost = "localhost",
                RemoteCertificateValidation = (_, _, _, _) => true,
            },
        });
        client.OnDataReceived += async data =>
        {
            clientReceived.TrySetResult(data.ToArray());
        };

        await client.ConnectAsync();

        byte[] sendData = "TcpClient SSL test"u8.ToArray();
        await client.SendAsync(sendData);

        byte[] result = await clientReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(sendData, result);

        cert.Dispose();
    }

    [Fact]
    public async Task WsServer_WithSsl_TextEcho()
    {
        int port = GetPort();
        X509Certificate2 cert = CreateSelfSignedCert();
        TaskCompletionSource<string> received = new();

        await using StormWebSocketServer server = new(new ServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, port),
            Ssl = new SslOptions { Certificate = cert },
            WebSocket = new WebSocketOptions
            {
                Heartbeat = new HeartbeatOptions { PingInterval = TimeSpan.Zero },
            },
        });
        server.OnMessageReceived += async (session, msg) =>
        {
            if (session is WebSocketSession wss)
                await wss.SendTextAsync(msg.Text);
        };
        await server.StartAsync();

        await using StormWebSocketClient client = new(new WsClientOptions
        {
            Uri = new Uri($"wss://localhost:{port}/"),
            Heartbeat = new HeartbeatOptions { PingInterval = TimeSpan.Zero },
            Ssl = new ClientSslOptions
            {
                TargetHost = "localhost",
                RemoteCertificateValidation = (_, _, _, _) => true,
            },
        });
        client.OnMessageReceived += async msg => received.TrySetResult(msg.Text);
        await client.ConnectAsync();
        await client.SendTextAsync("Hello WSS!");

        string result = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Hello WSS!", result);

        cert.Dispose();
    }

    [Fact]
    public async Task SslTransport_DoubleDispose_NoThrow()
    {
        int port = GetPort();
        X509Certificate2 cert = CreateSelfSignedCert();

        TcpListener listener = new(IPAddress.Loopback, port);
        listener.Start();

        Task serverTask = Task.Run(async () =>
        {
            Socket s = await listener.AcceptSocketAsync();
            SslTransport t = new(s, cert, SslProtocols.None, false);
            await t.HandshakeAsync();
            await t.DisposeAsync();
            await t.DisposeAsync(); // second dispose should not throw
        });

        Socket clientSocket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await clientSocket.ConnectAsync(IPAddress.Loopback, port);
        SslTransport clientTransport = new(clientSocket, "localhost", SslProtocols.None, (_, _, _, _) => true);
        await clientTransport.HandshakeAsync();

        await clientTransport.DisposeAsync();
        await clientTransport.DisposeAsync(); // client double dispose

        await serverTask;
        listener.Stop();
        cert.Dispose();
    }
}
