using System.IO.Pipelines;

namespace StormSocket.Transport;

/// <summary>
/// Abstracts a bidirectional byte stream (TCP or SSL) as a pair of Pipes.
/// Implementations: <see cref="TcpTransport"/>, <see cref="SslTransport"/>.
/// </summary>
public interface ITransport : IAsyncDisposable
{
    /// <summary>Read data coming from the remote side.</summary>
    PipeReader Input { get; }

    /// <summary>Write data to send to the remote side.</summary>
    PipeWriter Output { get; }

    /// <summary>Performs any required handshake (e.g. TLS negotiation) and starts I/O loops.</summary>
    ValueTask HandshakeAsync(CancellationToken cancellationToken = default);

    /// <summary>Gracefully shuts down the transport and releases resources.</summary>
    ValueTask CloseAsync(CancellationToken cancellationToken = default);
}