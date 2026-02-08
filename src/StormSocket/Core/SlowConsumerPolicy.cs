namespace StormSocket.Core;

/// <summary>
/// Determines behavior when a session's send buffer is full (backpressured)
/// during broadcast operations.
/// </summary>
public enum SlowConsumerPolicy
{
    /// <summary>
    /// Default. SendAsync awaits until the pipe drains (backpressure).
    /// Safe for 1:1 communication but can block broadcast loops.
    /// </summary>
    Wait,

    /// <summary>
    /// Skip sending to backpressured sessions during broadcast.
    /// Best for real-time data where old data is stale (chat, game state, stock prices).
    /// </summary>
    Drop,

    /// <summary>
    /// Disconnect backpressured sessions during broadcast.
    /// Most aggressive - ensures all connected clients can keep up.
    /// Best for financial data feeds, command streams.
    /// </summary>
    Disconnect,
}