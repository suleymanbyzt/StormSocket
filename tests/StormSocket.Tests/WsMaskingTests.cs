using StormSocket.WebSocket;
using Xunit;

namespace StormSocket.Tests;

public class WsMaskingTests
{
    /// <summary>The byte-at-a-time form from RFC 6455 Section 5.3, used as the reference.</summary>
    private static byte[] MaskReference(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> key)
    {
        byte[] result = new byte[payload.Length];
        for (int i = 0; i < payload.Length; i++)
        {
            result[i] = (byte)(payload[i] ^ key[i & 3]);
        }

        return result;
    }

    [Theory]
    // Lengths around the vector and 8-byte block boundaries, where the key phase is easiest to lose.
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(127)]
    [InlineData(1024)]
    [InlineData(4099)]
    public void ApplyMask_MatchesTheReferenceImplementation(int length)
    {
        Random random = new(length);
        byte[] payload = new byte[length];
        random.NextBytes(payload);
        byte[] key = [0x37, 0xFA, 0x21, 0x3D];

        byte[] expected = MaskReference(payload, key);

        byte[] actual = (byte[])payload.Clone();
        WsMasking.ApplyMask(actual, key);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ApplyMask_IsItsOwnInverse()
    {
        byte[] original = new byte[777];
        new Random(1).NextBytes(original);
        byte[] key = [0x01, 0x02, 0x03, 0x04];

        byte[] roundTrip = (byte[])original.Clone();
        WsMasking.ApplyMask(roundTrip, key);
        Assert.NotEqual(original, roundTrip);

        WsMasking.ApplyMask(roundTrip, key);
        Assert.Equal(original, roundTrip);
    }

    [Fact]
    public void ApplyMask_CopyingOverload_MatchesInPlace()
    {
        byte[] source = new byte[333];
        new Random(2).NextBytes(source);
        byte[] key = [0xAA, 0xBB, 0xCC, 0xDD];

        byte[] destination = new byte[source.Length + 8];
        WsMasking.ApplyMask(source, destination.AsSpan(0, source.Length), key);

        Assert.Equal(MaskReference(source, key), destination[..source.Length]);
        Assert.Equal(new byte[8], destination[source.Length..]);
    }
}
