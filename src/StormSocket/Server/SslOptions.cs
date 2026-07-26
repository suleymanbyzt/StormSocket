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
    public X509Certificate2 Certificate { get; set; } = null!;

    /// <summary>
    /// Allowed TLS protocol versions. Default: <see cref="SslProtocols.None"/> (let the OS choose the best).
    /// </summary>
    public SslProtocols Protocols { get; set; } = SslProtocols.None;

    /// <summary>Whether to require the client to present a certificate during handshake.</summary>
    public bool ClientCertificateRequired { get; set; }

    /// <summary>
    /// Verifies that a usable server certificate is present. Called by
    /// <see cref="ServerOptions.Validate"/> when TLS is enabled.
    /// </summary>
    /// <exception cref="ArgumentException">The certificate is missing or carries no private key.</exception>
    public void Validate()
    {
        if (Certificate is null)
        {
            throw new ArgumentException(
                "SslOptions.Certificate must be set to an X509Certificate2 that holds a private key. Without it the TLS handshake fails on every accepted connection.",
                nameof(Certificate));
        }

        if (!Certificate.HasPrivateKey)
        {
            throw new ArgumentException(
                "SslOptions.Certificate must hold a private key; a public-only certificate cannot complete a server-side TLS handshake. Load it from a PKCS#12 (.pfx) file or from a store entry that carries the key.",
                nameof(Certificate));
        }
    }
}
