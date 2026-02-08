using StormSocket.Session;

namespace StormSocket.Middleware.RateLimiting;

/// <summary>
/// Defines the action taken when a client exceeds the rate limit.
/// </summary>
public enum RateLimitAction
{
    /// <summary>Immediately terminates the connection via <see cref="ISession.Abort"/>.</summary>
    Disconnect,

    /// <summary>Silently drops the message without closing the connection.</summary>
    Drop,
}