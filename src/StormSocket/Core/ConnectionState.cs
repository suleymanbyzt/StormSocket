namespace StormSocket.Core;

/// <summary>Lifecycle states of a client connection.</summary>
public enum ConnectionState
{
    /// <summary>TCP connection accepted, handshake (SSL/WS upgrade) in progress.</summary>
    Connecting, // not used yet

    /// <summary>Fully established and ready for data transfer.</summary>
    Connected,

    /// <summary>Graceful shutdown initiated, waiting for close to complete.</summary>
    Closing,

    /// <summary>Connection fully closed and resources released.</summary>
    Closed
}