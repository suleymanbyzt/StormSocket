using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace StormSocket.Client;

/// <summary>
/// SSL/TLS configuration for client connections.
/// </summary>
public sealed class ClientSslOptions
{
    /// <summary>The server hostname for TLS SNI and certificate validation.</summary>
    public string TargetHost { get; init; } = "";

    /// <summary>Allowed TLS protocol versions. Default: let the OS choose.</summary>
    public SslProtocols Protocols { get; init; } = SslProtocols.None;

    /// <summary>Optional client certificate for mutual TLS.</summary>
    public X509Certificate2? ClientCertificate { get; init; }

    /// <summary>
    /// Custom certificate validation callback. Null = use default system validation.
    /// </summary>
    public RemoteCertificateValidationCallback? RemoteCertificateValidation { get; init; }
}