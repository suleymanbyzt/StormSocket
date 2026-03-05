using System.Buffers;
using StormSocket.Events;

namespace StormSocket.WebSocket;

/// <summary>
/// Reassembles fragmented WebSocket messages per RFC 6455 Section 5.4.
/// One instance per connection. Not thread-safe (called from a single read loop).
/// </summary>
internal sealed class WsFragmentAssembler : IDisposable
{
    private readonly int _maxMessageSize;
    private byte[]? _buffer;
    private int _offset;
    private WsOpCode _originalOpCode;
    private bool _isAssembling;
    private bool _compressed;

    public WsFragmentAssembler(int maxMessageSize)
    {
        _maxMessageSize = maxMessageSize;
    }

    /// <summary>True when a fragmented message is in progress.</summary>
    public bool IsAssembling => _isAssembling;

    /// <summary>
    /// Processes one decoded frame. Returns a completed <see cref="WsMessage"/> when the frame
    /// completes a message (single unfragmented frame or final continuation). Returns null when
    /// the frame is a buffered fragment or a control frame (caller handles control frames directly).
    /// </summary>
    public WsMessage? TryAssemble(in WsFrame frame)
    {
        // Control frames pass through regardless of fragmentation state.
        // RFC 6455 5.4: control frames MAY be injected between fragments.
        if (frame.IsControl)
        {
            if (!frame.Fin)
            {
                throw new WsProtocolException(WsCloseStatus.ProtocolError, "Control frame must not be fragmented.");
            }

            return null;
        }

        if (!_isAssembling)
        {
            if (frame.OpCode == WsOpCode.Continuation)
            {
                throw new WsProtocolException(WsCloseStatus.ProtocolError, "Unexpected continuation frame without preceding data frame.");
            }

            // Text or Binary
            if (frame.Fin)
            {
                // Single unfragmented message — zero-copy return
                return new WsMessage
                {
                    Data = frame.Payload,
                    IsText = frame.OpCode == WsOpCode.Text,
                    Compressed = frame.Rsv1,
                };
            }

            // First fragment (FIN=0, Text|Binary)
            _isAssembling = true;
            _originalOpCode = frame.OpCode;
            _compressed = frame.Rsv1;
            _offset = 0;
            AppendPayload(frame.Payload);
            return null;
        }

        // Currently assembling
        if (frame.OpCode is WsOpCode.Text or WsOpCode.Binary)
        {
            throw new WsProtocolException(WsCloseStatus.ProtocolError, "New data frame received while fragmented message is in progress.");
        }

        // Continuation frame
        AppendPayload(frame.Payload);

        if (frame.Fin)
        {
            byte[] result = new byte[_offset];
            _buffer.AsSpan(0, _offset).CopyTo(result);
            bool isText = _originalOpCode == WsOpCode.Text;
            
            Reset();
            
            return new WsMessage { Data = result, IsText = isText, Compressed = _compressed };
        }

        return null;
    }

    private void AppendPayload(ReadOnlyMemory<byte> payload)
    {
        int needed = _offset + payload.Length;
        if (needed > _maxMessageSize)
        {
            Reset();
            throw new WsProtocolException(WsCloseStatus.MessageTooBig, $"Assembled message exceeds maximum size of {_maxMessageSize} bytes.");
        }

        EnsureCapacity(needed);
        payload.Span.CopyTo(_buffer.AsSpan(_offset));
        _offset += payload.Length;
    }

    private void EnsureCapacity(int needed)
    {
        if (_buffer is not null && _buffer.Length >= needed)
        {
            return;
        }

        int newSize = _buffer is null
            ? Math.Max(needed, 4096)
            : Math.Max(needed, _buffer.Length * 2);
        
        newSize = Math.Min(newSize, _maxMessageSize);

        byte[] newBuffer = ArrayPool<byte>.Shared.Rent(newSize);
        if (_buffer is not null)
        {
            _buffer.AsSpan(0, _offset).CopyTo(newBuffer);
            ArrayPool<byte>.Shared.Return(_buffer);
        }

        _buffer = newBuffer;
    }

    public void Reset()
    {
        if (_buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = null;
        }

        _offset = 0;
        _isAssembling = false;
    }

    public void Dispose() => Reset();
}