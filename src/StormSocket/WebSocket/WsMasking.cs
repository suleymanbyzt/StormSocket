using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace StormSocket.WebSocket;

/// <summary>
/// XOR masking of WebSocket payloads (RFC 6455 Section 5.3).
/// </summary>
/// <remarks>
/// Every client-to-server frame is masked, so this runs over every byte a server receives and every
/// byte a client sends — it is the hottest loop in the library. Instead of the byte-at-a-time form
/// the RFC describes, the 4-byte key is widened to a machine word (and to a full vector for larger
/// payloads) so 8 or 16+ bytes are masked per instruction. Every width is a multiple of 4, so the
/// key stays in phase across chunks.
/// </remarks>
internal static class WsMasking
{
    /// <summary>
    /// Below this many bytes the vector path is not worth entering: building the repeating-key
    /// vector costs more than the handful of 8-byte blocks it would save.
    /// </summary>
    private const int VectorThreshold = 128;

    /// <summary>Applies the mask key to <paramref name="buffer"/> in place.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ApplyMask(Span<byte> buffer, ReadOnlySpan<byte> maskKey)
    {
        // On a big-endian machine the reinterpreted words would put the key bytes in the wrong
        // order, and the payloads that reach this path are small enough that the byte loop the RFC
        // describes is a perfectly good fallback.
        if (!BitConverter.IsLittleEndian)
        {
            for (int i = 0; i < buffer.Length; i++)
            {
                buffer[i] ^= maskKey[i & 3];
            }

            return;
        }

        uint key = (uint)(maskKey[0] | (maskKey[1] << 8) | (maskKey[2] << 16) | (maskKey[3] << 24));
        MaskLittleEndian(buffer, key);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void MaskLittleEndian(Span<byte> buffer, uint key)
    {
        // Walked by reference rather than by re-slicing: at the small payload sizes that dominate
        // chat and game traffic, the per-step bounds check costs more than the XOR itself.
        ref byte start = ref MemoryMarshal.GetReference(buffer);
        int length = buffer.Length;
        int offset = 0;

        if (Vector.IsHardwareAccelerated && length >= VectorThreshold)
        {
            // Broadcasting the key as a uint fills every lane in one instruction, so the repeating
            // pattern costs nothing to build.
            Vector<byte> maskVector = Vector.AsVectorByte(new Vector<uint>(key));
            int step = Vector<byte>.Count;

            for (; offset <= length - step; offset += step)
            {
                ref byte at = ref Unsafe.Add(ref start, offset);
                Unsafe.WriteUnaligned(ref at, Unsafe.ReadUnaligned<Vector<byte>>(ref at) ^ maskVector);
            }
        }

        ulong wideKey = key | ((ulong)key << 32);

        for (; offset <= length - sizeof(ulong); offset += sizeof(ulong))
        {
            ref byte at = ref Unsafe.Add(ref start, offset);
            Unsafe.WriteUnaligned(ref at, Unsafe.ReadUnaligned<ulong>(ref at) ^ wideKey);
        }

        if (offset <= length - sizeof(uint))
        {
            ref byte at = ref Unsafe.Add(ref start, offset);
            Unsafe.WriteUnaligned(ref at, Unsafe.ReadUnaligned<uint>(ref at) ^ key);
            offset += sizeof(uint);
        }

        for (; offset < length; offset++)
        {
            // The key repeats every 4 bytes and every width above is a multiple of 4, so the phase
            // here is still (offset & 3).
            Unsafe.Add(ref start, offset) ^= (byte)(key >> ((offset & 3) * 8));
        }
    }

    /// <summary>Copies <paramref name="source"/> into <paramref name="destination"/>, applying the mask key as it goes.</summary>
    public static void ApplyMask(ReadOnlySpan<byte> source, Span<byte> destination, ReadOnlySpan<byte> maskKey)
    {
        source.CopyTo(destination);
        ApplyMask(destination[..source.Length], maskKey);
    }
}
