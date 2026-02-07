namespace StormSocket.Core;

/// <summary>
/// Thread-safe counters for a single connection's I/O statistics.
/// </summary>
public sealed class ConnectionMetrics
{
    private long _bytesSent;
    private long _bytesReceived;

    /// <summary>Total bytes sent to the client so far.</summary>
    public long BytesSent => Interlocked.Read(ref _bytesSent);

    /// <summary>Total bytes received from the client so far.</summary>
    public long BytesReceived => Interlocked.Read(ref _bytesReceived);

    /// <summary>UTC timestamp of when the connection was established.</summary>
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>How long the connection has been alive.</summary>
    public TimeSpan Uptime => DateTimeOffset.UtcNow - ConnectedAt;

    internal void AddBytesSent(long count) => Interlocked.Add(ref _bytesSent, count);
    
    internal void AddBytesReceived(long count) => Interlocked.Add(ref _bytesReceived, count);
}