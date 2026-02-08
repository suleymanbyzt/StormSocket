namespace StormSocket.Middleware.RateLimiting;

/// <summary>
/// Configuration for the <see cref="RateLimitMiddleware"/>.
/// </summary>
public sealed class RateLimitOptions
{
    /// <summary>The time window for counting messages. Default: 1 second.</summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Maximum number of messages allowed within the window. Default: 100.</summary>
    public int MaxMessages { get; init; } = 100;

    /// <summary>Whether to limit per session or per IP address. Default: Session.</summary>
    public RateLimitScope Scope { get; init; } = RateLimitScope.Session;

    /// <summary>Action to take when the limit is exceeded. Default: Disconnect.</summary>
    public RateLimitAction ExceededAction { get; init; } = RateLimitAction.Disconnect;
}