using StormSocket.Core;

namespace StormSocket.Session;

/// <summary>
/// Represents an active client connection. Use this to send data, check metrics, or manage groups.
/// </summary>
public interface ISession : IAsyncDisposable
{
    /// <summary>
    /// Unique session identifier (auto-incremented integer).
    /// </summary>
    long Id { get; }

    ///<summary>
    /// Current connection state (Connecting, Connected, Closing, Closed).
    /// </summary>
    ConnectionState State { get; }

    /// <summary>
    /// Tracks bytes sent/received and connection uptime.
    /// </summary>
    ConnectionMetrics Metrics { get; }

    /// <summary>
    /// Set of group names this session belongs to.
    /// </summary>
    IReadOnlySet<string> Groups { get; }

    /// <summary>
    /// Sends raw bytes to the client.
    /// </summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gracefully closes the connection.
    /// </summary>
    ValueTask CloseAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds this session to a named group for broadcast.
    /// </summary>
    void JoinGroup(string group);

    /// <summary>
    /// Removes this session from a named group.
    /// </summary>
    void LeaveGroup(string group);
}