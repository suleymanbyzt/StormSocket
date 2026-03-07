using StormSocket.Core;
using Xunit;

namespace StormSocket.Tests;

public class ServerMetricsTests
{
    [Fact]
    public void RecordConnectionOpened_IncrementsCounters()
    {
        ServerMetrics metrics = new();

        metrics.RecordConnectionOpened();
        metrics.RecordConnectionOpened();

        Assert.Equal(2, metrics.ActiveConnections);
        Assert.Equal(2, metrics.TotalConnections);
    }

    [Fact]
    public void RecordConnectionClosed_DecrementsActive()
    {
        ServerMetrics metrics = new();

        metrics.RecordConnectionOpened();
        metrics.RecordConnectionOpened();
        metrics.RecordConnectionClosed(TimeSpan.FromSeconds(5));

        Assert.Equal(1, metrics.ActiveConnections);
        Assert.Equal(2, metrics.TotalConnections);
    }

    [Fact]
    public void RecordMessageSent_TracksCountAndBytes()
    {
        ServerMetrics metrics = new();

        metrics.RecordMessageSent(100);
        metrics.RecordMessageSent(200);

        Assert.Equal(2, metrics.MessagesSent);
        Assert.Equal(300, metrics.BytesSentTotal);
    }

    [Fact]
    public void RecordMessageReceived_TracksCountAndBytes()
    {
        ServerMetrics metrics = new();

        metrics.RecordMessageReceived(50);
        metrics.RecordMessageReceived(150);

        Assert.Equal(2, metrics.MessagesReceived);
        Assert.Equal(200, metrics.BytesReceivedTotal);
    }

    [Fact]
    public void RecordError_IncrementsErrorCount()
    {
        ServerMetrics metrics = new();

        metrics.RecordError();
        metrics.RecordError();
        metrics.RecordError();

        Assert.Equal(3, metrics.ErrorCount);
    }

    [Fact]
    public void AllCounters_StartAtZero()
    {
        ServerMetrics metrics = new();

        Assert.Equal(0, metrics.ActiveConnections);
        Assert.Equal(0, metrics.TotalConnections);
        Assert.Equal(0, metrics.MessagesSent);
        Assert.Equal(0, metrics.MessagesReceived);
        Assert.Equal(0, metrics.BytesSentTotal);
        Assert.Equal(0, metrics.BytesReceivedTotal);
        Assert.Equal(0, metrics.ErrorCount);
    }

    [Fact]
    public void FullLifecycle_TracksEverything()
    {
        ServerMetrics metrics = new();

        // 3 connections open
        metrics.RecordConnectionOpened();
        metrics.RecordConnectionOpened();
        metrics.RecordConnectionOpened();

        // Some messages
        metrics.RecordMessageReceived(100);
        metrics.RecordMessageReceived(200);
        metrics.RecordMessageSent(50);

        // 1 error
        metrics.RecordError();

        // 2 connections close
        metrics.RecordConnectionClosed(TimeSpan.FromSeconds(10));
        metrics.RecordConnectionClosed(TimeSpan.FromSeconds(5));

        Assert.Equal(1, metrics.ActiveConnections);
        Assert.Equal(3, metrics.TotalConnections);
        Assert.Equal(2, metrics.MessagesReceived);
        Assert.Equal(300, metrics.BytesReceivedTotal);
        Assert.Equal(1, metrics.MessagesSent);
        Assert.Equal(50, metrics.BytesSentTotal);
        Assert.Equal(1, metrics.ErrorCount);
    }
}
