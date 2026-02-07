namespace StormSocket.Core;

/// <summary>
/// Thread-safe connection ID generator using Interlocked.Increment.
/// </summary>
public static class ConnectionId
{
    private static long _nextId;

    /// <summary>Returns the next unique connection ID. Thread-safe.</summary>
    public static long Next() => Interlocked.Increment(ref _nextId);

    /// <summary>
    /// Resets the counter. Only intended for testing.
    /// </summary>
    internal static void Reset() => Interlocked.Exchange(ref _nextId, 0);
}