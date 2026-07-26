namespace StormSocket.Core;

/// <summary>
/// Range guards shared by the option types' <c>Validate</c> methods.
/// </summary>
/// <remarks>
/// The wording of the messages is the point of these helpers: each one names the property, reports
/// the value it was given and states the range it has to be in, so a misconfiguration is
/// diagnosable from the exception alone rather than from a later failure deep in the I/O path.
/// </remarks>
internal static class OptionsValidation
{
    /// <summary>Guards a size or count that has to be at least 1.</summary>
    public static void RequirePositive(long value, string owner, string property)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(property, value, $"{owner}.{property} must be greater than 0.");
        }
    }

    /// <summary>Guards a limit where 0 carries a meaning of its own (unlimited, or disabled).</summary>
    public static void RequireNonNegative(long value, string owner, string property)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(property, value, $"{owner}.{property} must be 0 or greater.");
        }
    }

    /// <summary>Guards a duration that has to elapse for the setting to mean anything.</summary>
    /// <param name="allowInfinite">
    /// True when <see cref="Timeout.InfiniteTimeSpan"/> is accepted as "wait forever".
    /// </param>
    public static void RequirePositiveDuration(TimeSpan value, string owner, string property, bool allowInfinite)
    {
        if (allowInfinite && value == Timeout.InfiniteTimeSpan)
        {
            return;
        }

        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                property,
                value,
                allowInfinite
                    ? $"{owner}.{property} must be a positive duration, or Timeout.InfiniteTimeSpan to wait indefinitely."
                    : $"{owner}.{property} must be a positive duration.");
        }
    }

    /// <summary>Guards a duration where <see cref="TimeSpan.Zero"/> carries a meaning of its own.</summary>
    /// <param name="allowInfinite">
    /// True when <see cref="Timeout.InfiniteTimeSpan"/> is accepted as "wait forever". It is negative,
    /// so it has to be recognised before the sign check.
    /// </param>
    public static void RequireNonNegativeDuration(TimeSpan value, string owner, string property, bool allowInfinite = false)
    {
        if (allowInfinite && value == Timeout.InfiniteTimeSpan)
        {
            return;
        }

        if (value < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                property,
                value,
                allowInfinite
                    ? $"{owner}.{property} must be TimeSpan.Zero, a positive duration, or Timeout.InfiniteTimeSpan."
                    : $"{owner}.{property} must be TimeSpan.Zero or a positive duration.");
        }
    }

    /// <summary>
    /// Guards the heartbeat settings of a WebSocket endpoint. They live on a shared type, so the
    /// owning options object supplies its own name for the message.
    /// </summary>
    public static void ValidateHeartbeat(HeartbeatOptions? heartbeat, string owner)
    {
        if (heartbeat is null)
        {
            throw new ArgumentException($"{owner}.Heartbeat must not be null. Leave it at its default to use the standard ping/pong settings.", "Heartbeat");
        }

        RequireNonNegativeDuration(heartbeat.PingInterval, $"{owner}.Heartbeat", nameof(HeartbeatOptions.PingInterval));

        if (heartbeat.PingInterval > TimeSpan.Zero && heartbeat.MaxMissedPongs < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(HeartbeatOptions.MaxMissedPongs),
                heartbeat.MaxMissedPongs,
                $"{owner}.Heartbeat.MaxMissedPongs must be at least 1 while {owner}.Heartbeat.PingInterval is greater than TimeSpan.Zero, otherwise every connection is declared dead before it can answer a single ping. Set PingInterval to TimeSpan.Zero to disable the heartbeat instead.");
        }
    }
}
