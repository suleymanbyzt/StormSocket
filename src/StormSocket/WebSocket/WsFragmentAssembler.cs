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
    private Utf8Validator _utf8;
    private bool _validateUtf8;

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
                if (frame.Payload.Length > _maxMessageSize)
                {
                    throw new WsProtocolException(WsCloseStatus.MessageTooBig,
                        $"Message size {frame.Payload.Length} exceeds maximum of {_maxMessageSize} bytes.");
                }

                // A compressed payload is validated by the caller once it has been inflated.
                if (frame.OpCode == WsOpCode.Text && !frame.Rsv1 && !Utf8Validator.IsValid(frame.Payload.Span))
                {
                    throw new WsProtocolException(WsCloseStatus.InvalidPayload, "Text message is not valid UTF-8.");
                }

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
            _utf8.Reset();
            _validateUtf8 = frame.OpCode == WsOpCode.Text && !frame.Rsv1;
            AppendPayload(frame.Payload);
            return null;
        }

        // Currently assembling
        if (frame.OpCode is WsOpCode.Text or WsOpCode.Binary)
        {
            throw new WsProtocolException(WsCloseStatus.ProtocolError, "New data frame received while fragmented message is in progress.");
        }

        // RFC 7692 Section 6: RSV1 belongs on the first frame of a message only.
        if (frame.Rsv1)
        {
            throw new WsProtocolException(WsCloseStatus.ProtocolError, "RSV1 must not be set on a continuation frame.");
        }

        // Continuation frame
        AppendPayload(frame.Payload);

        if (frame.Fin)
        {
            // RFC 6455 Section 8.1: a message that ends in the middle of a code point is invalid.
            if (_validateUtf8 && !_utf8.IsComplete)
            {
                Reset();
                throw new WsProtocolException(WsCloseStatus.InvalidPayload, "Text message ends with an incomplete UTF-8 sequence.");
            }

            byte[] result = new byte[_offset];
            _buffer.AsSpan(0, _offset).CopyTo(result);
            bool isText = _originalOpCode == WsOpCode.Text;
            bool compressed = _compressed;

            Reset();

            return new WsMessage { Data = result, IsText = isText, Compressed = compressed };
        }

        return null;
    }

    private void AppendPayload(ReadOnlyMemory<byte> payload)
    {
        // long arithmetic: _offset + payload.Length can overflow int when MaxMessageSize is set
        // near int.MaxValue, which would turn the bound check below into a bypass.
        long needed64 = (long)_offset + payload.Length;
        if (needed64 > _maxMessageSize)
        {
            Reset();
            throw new WsProtocolException(WsCloseStatus.MessageTooBig, $"Assembled message exceeds maximum size of {_maxMessageSize} bytes.");
        }

        // RFC 6455 Section 8.1: validate as fragments arrive so a bad byte fails the connection
        // immediately rather than after the whole message has been buffered.
        if (_validateUtf8 && !_utf8.TryFeed(payload.Span))
        {
            Reset();
            throw new WsProtocolException(WsCloseStatus.InvalidPayload, "Text message is not valid UTF-8.");
        }

        int needed = (int)needed64;
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
        _validateUtf8 = false;
        _utf8.Reset();
    }

    public void Dispose() => Reset();
}