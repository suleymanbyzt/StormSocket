namespace StormSocket.Middleware.RateLimiting;

/// <summary>
/// Defines how rate limit buckets are keyed.
/// </summary>
public enum RateLimitScope
{
    /// <summary>Each session has its own independent rate limit counter.</summary>
    Session,

    /// <summary>All sessions from the same IP address share a single rate limit counter.</summary>
    IpAddress,
}