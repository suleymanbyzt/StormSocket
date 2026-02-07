using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace StormSocket.Server;

/// <summary>
/// SSL/TLS configuration for server connections.
/// Provide this via <see cref="ServerOptions.Ssl"/> to enable encrypted connections.
/// </summary>
public sealed class SslOptions
{
    /// <summary>The X.509 certificate (with private key) used for TLS handshake.</summary>
    public X509Certificate2 Certificate { get; init; } = null!;

    /// <summary>
    /// Allowed TLS protocol versions. Default: <see cref="SslProtocols.None"/> (let the OS choose the best).
    /// </summary>
    public SslProtocols Protocols { get; init; } = SslProtocols.None;

    /// <summary>Whether to require the client to present a certificate during handshake.</summary>
    public bool ClientCertificateRequired { get; init; }
}
