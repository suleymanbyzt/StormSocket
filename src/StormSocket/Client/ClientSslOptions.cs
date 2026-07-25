using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace StormSocket.Client;

/// <summary>
/// SSL/TLS configuration for client connections.
/// </summary>
public sealed class ClientSslOptions
{
    /// <summary>
    /// The server hostname for TLS SNI and certificate validation.
    /// Empty (the default) means "derive it from the connection target": the URI host for
    /// <see cref="StormWebSocketClient"/>, the endpoint host for <see cref="StormTcpClient"/>.
    /// </summary>
    public string TargetHost { get; init; } = "";

    /// <summary>Allowed TLS protocol versions. Default: let the OS choose.</summary>
    public SslProtocols Protocols { get; init; } = SslProtocols.None;

    /// <summary>Optional client certificate for mutual TLS.</summary>
    public X509Certificate2? ClientCertificate { get; init; }

    /// <summary>
    /// Custom certificate validation callback. Null = use default system validation.
    /// </summary>
    public RemoteCertificateValidationCallback? RemoteCertificateValidation { get; init; }

    /// <summary>
    /// Checks the server certificate chain against CRL/OCSP. Default: false, matching the platform
    /// default for the underlying <see cref="SslStream"/>. Ignored when
    /// <see cref="RemoteCertificateValidation"/> is set — a custom validator owns the whole decision.
    /// </summary>
    public bool CheckCertificateRevocation { get; init; }

    /// <summary>
    /// The validation callback to hand to the transport: the caller's own callback when supplied,
    /// otherwise a revocation-checking one when it was asked for, otherwise null for the default
    /// system validation.
    /// </summary>
    internal RemoteCertificateValidationCallback? ResolveValidationCallback()
    {
        if (RemoteCertificateValidation is not null)
        {
            return RemoteCertificateValidation;
        }

        return CheckCertificateRevocation ? ValidateWithRevocation : null;
    }

    /// <summary>
    /// The revocation mode cannot be set through <see cref="SslStream"/> from here, so the chain is
    /// rebuilt with revocation enabled on top of the platform's own verdict.
    /// </summary>
    private static bool ValidateWithRevocation(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors)
    {
        if (sslPolicyErrors is not SslPolicyErrors.None)
        {
            return false;
        }

        X509Certificate2? leaf = chain is { ChainElements.Count: > 0 }
            ? chain.ChainElements[0].Certificate
            : certificate as X509Certificate2;

        if (leaf is null)
        {
            return false;
        }

        using X509Chain revocationChain = new();
        revocationChain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
        revocationChain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
        return revocationChain.Build(leaf);
    }

    /// <summary>Resolves the SNI/verification host, falling back to the connection target when unset.</summary>
    internal static string ResolveTargetHost(ClientSslOptions? ssl, string fallbackHost)
        => string.IsNullOrEmpty(ssl?.TargetHost) ? fallbackHost : ssl!.TargetHost;
}
