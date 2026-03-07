using System.Diagnostics.Metrics;

namespace StormSocket.Core;

/// <summary>
/// Server-wide aggregate metrics using System.Diagnostics.Metrics.
/// Integrates with OpenTelemetry, Prometheus, and dotnet-counters out of the box.
/// Also exposes simple properties for direct access (e.g. server.Metrics.ActiveConnections).
/// </summary>
public sealed class ServerMetrics
{
    private static readonly Meter Meter = new("StormSocket", "1.0.0");

    // Instruments
    private static readonly Counter<long> TotalConnectionsCounter = Meter.CreateCounter<long>(
        "stormsocket.connections.total", "connections", "Total connections accepted since server start");

#if NET7_0_OR_GREATER
    private static readonly UpDownCounter<long> ActiveConnectionsCounter = Meter.CreateUpDownCounter<long>(
        "stormsocket.connections.active", "connections", "Currently active connections");
#endif

    private static readonly Counter<long> MessagesSentCounter = Meter.CreateCounter<long>(
        "stormsocket.messages.sent", "messages", "Total messages sent");

    private static readonly Counter<long> MessagesReceivedCounter = Meter.CreateCounter<long>(
        "stormsocket.messages.received", "messages", "Total messages received");

    private static readonly Counter<long> BytesSentCounter = Meter.CreateCounter<long>(
        "stormsocket.bytes.sent", "bytes", "Total bytes sent");

    private static readonly Counter<long> BytesReceivedCounter = Meter.CreateCounter<long>(
        "stormsocket.bytes.received", "bytes", "Total bytes received");

    private static readonly Counter<long> ErrorsCounter = Meter.CreateCounter<long>(
        "stormsocket.errors", "errors", "Total errors");

    private static readonly Histogram<double> ConnectionDurationHistogram = Meter.CreateHistogram<double>(
        "stormsocket.connection.duration", "ms", "Connection duration in milliseconds");

    private static readonly Histogram<double> HandshakeDurationHistogram = Meter.CreateHistogram<double>(
        "stormsocket.handshake.duration", "ms", "Handshake duration in milliseconds");

    // Backing fields for direct property access
    private long _activeConnections;
    private long _totalConnections;
    private long _messagesSent;
    private long _messagesReceived;
    private long _bytesSentTotal;
    private long _bytesReceivedTotal;
    private long _errorCount;

    /// <summary>Currently active connection count.</summary>
    public long ActiveConnections => Interlocked.Read(ref _activeConnections);

    /// <summary>Total connections accepted since server start.</summary>
    public long TotalConnections => Interlocked.Read(ref _totalConnections);

    /// <summary>Total messages sent across all connections.</summary>
    public long MessagesSent => Interlocked.Read(ref _messagesSent);

    /// <summary>Total messages received across all connections.</summary>
    public long MessagesReceived => Interlocked.Read(ref _messagesReceived);

    /// <summary>Aggregate bytes sent across all connections.</summary>
    public long BytesSentTotal => Interlocked.Read(ref _bytesSentTotal);

    /// <summary>Aggregate bytes received across all connections.</summary>
    public long BytesReceivedTotal => Interlocked.Read(ref _bytesReceivedTotal);

    /// <summary>Total error count (protocol errors, transport errors, failed handshakes).</summary>
    public long ErrorCount => Interlocked.Read(ref _errorCount);

    internal void RecordConnectionOpened()
    {
        Interlocked.Increment(ref _totalConnections);
        Interlocked.Increment(ref _activeConnections);
        TotalConnectionsCounter.Add(1);
#if NET7_0_OR_GREATER
        ActiveConnectionsCounter.Add(1);
#endif
    }

    internal void RecordConnectionClosed(TimeSpan duration)
    {
        Interlocked.Decrement(ref _activeConnections);
#if NET7_0_OR_GREATER
        ActiveConnectionsCounter.Add(-1);
#endif
        ConnectionDurationHistogram.Record(duration.TotalMilliseconds);
    }

    internal void RecordHandshakeDuration(TimeSpan duration)
    {
        HandshakeDurationHistogram.Record(duration.TotalMilliseconds);
    }

    internal void RecordMessageSent(long byteCount)
    {
        Interlocked.Increment(ref _messagesSent);
        Interlocked.Add(ref _bytesSentTotal, byteCount);
        MessagesSentCounter.Add(1);
        BytesSentCounter.Add(byteCount);
    }

    internal void RecordMessageReceived(long byteCount)
    {
        Interlocked.Increment(ref _messagesReceived);
        Interlocked.Add(ref _bytesReceivedTotal, byteCount);
        MessagesReceivedCounter.Add(1);
        BytesReceivedCounter.Add(byteCount);
    }

    internal void RecordError()
    {
        Interlocked.Increment(ref _errorCount);
        ErrorsCounter.Add(1);
    }
}
