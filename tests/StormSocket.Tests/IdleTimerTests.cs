using StormSocket.Core;
using Xunit;

namespace StormSocket.Tests;

public class IdleTimerTests
{
    [Fact]
    public async Task Fires_OnTimeout_when_no_data_received()
    {
        TaskCompletionSource tcs = new();
        IdleTimer timer = new(TimeSpan.FromMilliseconds(200));
        timer.OnTimeout = () =>
        {
            tcs.TrySetResult();
            return ValueTask.CompletedTask;
        };

        timer.Start();

        Task completed = await Task.WhenAny(tcs.Task, Task.Delay(5000));
        Assert.Same(tcs.Task, completed);

        await timer.DisposeAsync();
    }

    [Fact]
    public async Task Does_not_fire_when_data_received_within_timeout()
    {
        bool fired = false;
        IdleTimer timer = new(TimeSpan.FromMilliseconds(300));
        timer.OnTimeout = () =>
        {
            fired = true;
            return ValueTask.CompletedTask;
        };

        timer.Start();

        // Send data activity every 100ms for 500ms total — should never timeout
        for (int i = 0; i < 5; i++)
        {
            await Task.Delay(100);
            timer.OnDataReceived();
        }

        Assert.False(fired);

        await timer.DisposeAsync();
    }

    [Fact]
    public async Task Fires_after_data_stops_arriving()
    {
        TaskCompletionSource tcs = new();
        IdleTimer timer = new(TimeSpan.FromMilliseconds(200));
        timer.OnTimeout = () =>
        {
            tcs.TrySetResult();
            return ValueTask.CompletedTask;
        };

        timer.Start();

        // Keep alive for a bit
        for (int i = 0; i < 3; i++)
        {
            await Task.Delay(80);
            timer.OnDataReceived();
        }

        // Now stop sending data — should timeout
        Task completed = await Task.WhenAny(tcs.Task, Task.Delay(5000));
        Assert.Same(tcs.Task, completed);

        await timer.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_cancels_timer()
    {
        bool fired = false;
        IdleTimer timer = new(TimeSpan.FromMilliseconds(200));
        timer.OnTimeout = () =>
        {
            fired = true;
            return ValueTask.CompletedTask;
        };

        timer.Start();
        await timer.DisposeAsync();

        await Task.Delay(400);
        Assert.False(fired);
    }

    [Fact]
    public void DisconnectReason_IdleTimeout_exists()
    {
        Assert.Equal("IdleTimeout", DisconnectReason.IdleTimeout.ToString());
    }
}
