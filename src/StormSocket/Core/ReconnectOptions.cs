namespace StormSocket.Core;

/// <summary>
/// Auto-reconnect settings shared by TCP and WebSocket clients.
/// </summary>
public sealed class ReconnectOptions
{
    /// <summary>Enable automatic reconnection on disconnect. Default: false.</summary>
    public bool Enabled { get; init; } = false;

    /// <summary>Delay between reconnection attempts. Default: 2 seconds.</summary>
    public TimeSpan Delay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Maximum reconnection attempts. 0 = unlimited. Default: 0.</summary>
    public int MaxAttempts { get; init; } = 0;
}