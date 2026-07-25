using System.Buffers;

namespace StormSocket.WebSocket;

/// <summary>
/// Per-connection scratch buffer that unmasked frame payloads are written into.
/// </summary>
/// <remarks>
/// Every client-to-server frame is masked, so the payload cannot be handed out as a slice of the
/// pipe's read buffer — it has to be XORed somewhere first. Allocating that "somewhere" per frame
/// makes the server's steady-state garbage proportional to its message rate. One buffer per
/// connection, reused frame after frame, removes that allocation entirely and gives the payload the
/// same lifetime the unmasked (client-side) path already has: valid until the next frame is read.
/// </remarks>
internal sealed class WsUnmaskBuffer : IDisposable
{
    private byte[]? _buffer;

    /// <summary>
    /// Returns a backing array of at least <paramref name="length"/> bytes, growing it if needed.
    /// </summary>
    /// <remarks>
    /// The array itself is handed back rather than a <see cref="Memory{T}"/> so the decoder can slice
    /// it directly: resolving <c>Memory.Span</c> costs a type check per frame, which is measurable
    /// against the ~50 ns it takes to decode a small frame.
    /// </remarks>
    public byte[] GetArray(int length)
    {
        if (_buffer is null || _buffer.Length < length)
        {
            if (_buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(_buffer);
            }

            _buffer = ArrayPool<byte>.Shared.Rent(length);
        }

        return _buffer;
    }

    public void Dispose()
    {
        if (_buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = null;
        }
    }
}
