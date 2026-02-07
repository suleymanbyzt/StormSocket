using System.Threading.Tasks;
using StormSocket.Core;
using Xunit;

namespace StormSocket.Tests;

public class ConnectionIdTests
{
    [Fact]
    public void Next_ReturnsIncrementingValues()
    {
        ConnectionId.Reset();
        long id1 = ConnectionId.Next();
        long id2 = ConnectionId.Next();
        long id3 = ConnectionId.Next();

        Assert.Equal(1, id1);
        Assert.Equal(2, id2);
        Assert.Equal(3, id3);
    }

    [Fact]
    public void Next_IsThreadSafe()
    {
        ConnectionId.Reset();
        const int count = 10_000;
        long[] ids = new long[count];

        Parallel.For(0, count, i => ids[i] = ConnectionId.Next());

        long[] distinct = ids.Distinct().ToArray();
        Assert.Equal(count, distinct.Length);
    }
}