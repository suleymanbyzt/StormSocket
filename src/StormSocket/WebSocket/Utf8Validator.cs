namespace StormSocket.WebSocket;

/// <summary>
/// Incremental UTF-8 validator for WebSocket text payloads (RFC 6455 Section 8.1).
/// </summary>
/// <remarks>
/// A code point may be split across fragments, so validation has to carry state between frames
/// instead of decoding each fragment on its own. Feed every fragment in order, then check
/// <see cref="IsComplete"/> when FIN arrives: a message that ends mid-sequence is invalid too.
/// Rejects overlong encodings, UTF-16 surrogates (U+D800-U+DFFF) and anything above U+10FFFF,
/// all of which <see cref="System.Text.Encoding.UTF8"/> would silently replace with U+FFFD.
/// </remarks>
internal struct Utf8Validator
{
    private int _pending;       // continuation bytes still expected
    private int _codePoint;     // code point accumulated so far
    private int _sequenceLength; // total length of the sequence in progress

    /// <summary>True when no multi-byte sequence is half-finished.</summary>
    public readonly bool IsComplete => _pending == 0;

    /// <summary>Discards any partially decoded sequence.</summary>
    public void Reset()
    {
        _pending = 0;
        _codePoint = 0;
        _sequenceLength = 0;
    }

    /// <summary>
    /// Validates the next chunk of a text payload. Returns false as soon as the bytes cannot be
    /// part of a well-formed UTF-8 stream; the validator must not be reused after that.
    /// </summary>
    public bool TryFeed(ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            if (_pending == 0)
            {
                if (b <= 0x7F)
                {
                    continue;
                }

                if (b is >= 0xC2 and <= 0xDF)       // 2-byte; C0/C1 would always be overlong
                {
                    _pending = 1;
                    _sequenceLength = 2;
                    _codePoint = b & 0x1F;
                }
                else if (b is >= 0xE0 and <= 0xEF)  // 3-byte
                {
                    _pending = 2;
                    _sequenceLength = 3;
                    _codePoint = b & 0x0F;
                }
                else if (b is >= 0xF0 and <= 0xF4)  // 4-byte; F5+ is beyond U+10FFFF
                {
                    _pending = 3;
                    _sequenceLength = 4;
                    _codePoint = b & 0x07;
                }
                else
                {
                    return false;                    // lone continuation byte or invalid lead
                }

                continue;
            }

            if ((b & 0xC0) != 0x80)
            {
                return false;                        // expected a continuation byte
            }

            _codePoint = (_codePoint << 6) | (b & 0x3F);
            _pending--;

            if (_pending != 0)
            {
                continue;
            }

            bool valid = _sequenceLength switch
            {
                2 => _codePoint >= 0x80,
                3 => _codePoint >= 0x800 && _codePoint is < 0xD800 or > 0xDFFF,
                _ => _codePoint is >= 0x10000 and <= 0x10FFFF,
            };

            if (!valid)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Validates a complete, self-contained payload.</summary>
    public static bool IsValid(ReadOnlySpan<byte> data)
    {
        Utf8Validator validator = default;
        return validator.TryFeed(data) && validator.IsComplete;
    }
}
