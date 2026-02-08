using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using StormSocket.Server;

// Generate a self-signed certificate for demo purposes
X509Certificate2 cert = GenerateSelfSignedCert();

StormTcpServer server = new StormTcpServer(new ServerOptions
{
    EndPoint = new IPEndPoint(IPAddress.Any, 5001),
    NoDelay = true,
    Ssl = new SslOptions { Certificate = cert },
});

server.OnConnected += async session =>
{
    Console.WriteLine($"[{session.Id}] SSL Connected");
    await ValueTask.CompletedTask;
};

server.OnDisconnected += async session =>
{
    Console.WriteLine($"[{session.Id}] SSL Disconnected");
    await ValueTask.CompletedTask;
};

server.OnDataReceived += async (session, data) =>
{
    Console.WriteLine($"[{session.Id}] SSL Echo {data.Length} bytes");
    await session.SendAsync(data);
};

server.OnError += async (session, ex) =>
{
    Console.WriteLine($"[{session?.Id}] Error: {ex.Message}");
    await ValueTask.CompletedTask;
};

await server.StartAsync();
Console.WriteLine("SSL Echo server listening on port 5001. Press Enter to stop.");
Console.WriteLine("Test with: openssl s_client -connect localhost:5001");
Console.ReadLine();
await server.StopAsync();

static X509Certificate2 GenerateSelfSignedCert()
{
    using RSA rsa = RSA.Create(2048);
    CertificateRequest request = new CertificateRequest(
        "CN=StormSocket-Dev",
        rsa,
        HashAlgorithmName.SHA256,
        RSASignaturePadding.Pkcs1);

    X509Certificate2 cert = request.CreateSelfSigned(
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow.AddYears(1));

    // Export and re-import to ensure private key is available on all platforms
    byte[] pfxBytes = cert.Export(X509ContentType.Pfx);
    return X509CertificateLoader.LoadPkcs12(pfxBytes, null);
}