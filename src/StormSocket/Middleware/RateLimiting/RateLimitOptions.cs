using StormSocket.Core;

namespace StormSocket.Middleware.RateLimiting;

/// <summary>
/// Configuration for the <see cref="RateLimitMiddleware"/>.
/// </summary>
public sealed class RateLimitOptions
{
    /// <summary>The time window for counting messages. Default: 1 second.</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Maximum number of messages allowed within the window. Default: 100.</summary>
    public int MaxMessages { get; set; } = 100;

    /// <summary>Whether to limit per session or per IP address. Default: Session.</summary>
    public RateLimitScope Scope { get; set; } = RateLimitScope.Session;

    /// <summary>Action to take when the limit is exceeded. Default: Disconnect.</summary>
    public RateLimitAction ExceededAction { get; set; } = RateLimitAction.Disconnect;

    /// <summary>
    /// Whether protocol frames reported by the server read loop through
    /// <see cref="RateLimitMiddleware.TryAcceptFrameAsync"/> — ping/pong/close control frames and
    /// the individual fragments of a fragmented message — are charged to the same window as
    /// assembled application messages. When <see langword="false"/>, only assembled messages are
    /// counted, so a client can flood control frames (each of which the server answers) for free.
    /// Default: true.
    /// </summary>
    public bool CountControlFrames { get; set; } = true;

    /// <summary>
    /// Window accounting mode.
    /// <para>
    /// <see langword="true"/> (default) uses a two-bucket sliding window: the previous bucket's
    /// count is carried over weighted by how much of it still overlaps the current instant, holding
    /// the admitted rate at approximately <see cref="MaxMessages"/> per <see cref="Window"/> at any
    /// instant instead of allowing a burst at the boundary.
    /// </para>
    /// <para>
    /// <see langword="false"/> uses a fixed window that resets wholesale at the boundary. It is
    /// marginally cheaper but allows a burst of up to 2x <see cref="MaxMessages"/> around the
    /// boundary (a full window spent just before the reset, plus a full window straight after).
    /// </para>
    /// </summary>
    public bool SlidingWindow { get; set; } = true;

    /// <summary>
    /// Checks that the window and the message allowance describe a usable rate.
    /// </summary>
    /// <remarks>
    /// The <see cref="RateLimitMiddleware"/> constructor rejects the same values on its own, so this
    /// is for callers that want to validate a configuration before building the middleware.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The window or the message allowance is not positive.</exception>
    public void Validate()
    {
        OptionsValidation.RequirePositiveDuration(Window, nameof(RateLimitOptions), nameof(Window), allowInfinite: false);
        OptionsValidation.RequirePositive(MaxMessages, nameof(RateLimitOptions), nameof(MaxMessages));
    }
}
