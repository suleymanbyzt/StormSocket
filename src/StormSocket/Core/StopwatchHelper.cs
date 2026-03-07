using System.Diagnostics;

namespace StormSocket.Core;

internal static class StopwatchHelper
{
    internal static TimeSpan GetElapsedTime(long startTimestamp)
    {
#if NET7_0_OR_GREATER
        return Stopwatch.GetElapsedTime(startTimestamp);
#else
        long elapsed = Stopwatch.GetTimestamp() - startTimestamp;
        return TimeSpan.FromTicks(elapsed * TimeSpan.TicksPerSecond / Stopwatch.Frequency);
#endif
    }
}
