using System.Numerics;

namespace Noct.Core.Audio;

/// <summary>
/// MSB-first bit reader over a seekable stream.
/// </summary>
/// <remarks>
/// Bytes are pulled strictly on demand: a byte only enters the accumulator when at least one of
/// its bits is required. That property is what makes the frame CRC-16 snapshot taken immediately
/// after <see cref="AlignToByte"/> cover exactly the frame bytes and nothing more.
/// </remarks>
internal sealed class FlacBitReader
{
    private const int BufferSize = 64 * 1024;

    private readonly Stream _stream;
    private readonly byte[] _buffer = new byte[BufferSize];
    private int _bufferLength;
    private int _bufferPosition;

    /// <summary>Absolute stream offset of <c>_buffer[_bufferPosition]</c>.</summary>
    private long _bufferOrigin;

    private ulong _cache;
    private int _cacheBits;

    private byte _crc8;
    private ushort _crc16;

    public FlacBitReader(Stream stream)
    {
        _stream = stream;
        _bufferOrigin = stream.Position;
    }

    /// <summary>Byte offset of the next bit to be read; only meaningful when byte aligned.</summary>
    public long BytePosition => _bufferOrigin + _bufferPosition - (_cacheBits / 8);

    public bool IsByteAligned => _cacheBits % 8 == 0;

    public byte Crc8 => _crc8;

    public ushort Crc16 => _crc16;

    public void ResetCrc()
    {
        _crc8 = 0;
        _crc16 = 0;
    }

    /// <summary>Repositions the reader to an absolute byte offset and drops all buffered bits.</summary>
    public void SeekTo(long byteOffset)
    {
        _cache = 0;
        _cacheBits = 0;
        _bufferLength = 0;
        _bufferPosition = 0;
        _bufferOrigin = byteOffset;
        _stream.Position = byteOffset;
        ResetCrc();
    }

    public void AlignToByte()
    {
        _cacheBits -= _cacheBits % 8;
    }

    private bool TryNextByte(out byte value)
    {
        if (_bufferPosition >= _bufferLength)
        {
            _bufferOrigin += _bufferLength;
            _bufferPosition = 0;
            _bufferLength = _stream.Read(_buffer, 0, BufferSize);
            if (_bufferLength <= 0)
            {
                _bufferLength = 0;
                value = 0;
                return false;
            }
        }

        value = _buffer[_bufferPosition++];
        _crc8 = FlacCrc.Update8(_crc8, value);
        _crc16 = FlacCrc.Update16(_crc16, value);
        return true;
    }

    private byte NextByte()
    {
        if (!TryNextByte(out var value))
        {
            throw new EndOfStreamException("Unexpected end of FLAC stream.");
        }

        return value;
    }

    private void Need(int bits)
    {
        while (_cacheBits < bits)
        {
            _cache = (_cache << 8) | NextByte();
            _cacheBits += 8;
        }
    }

    /// <summary>Returns false at a clean end of stream instead of throwing.</summary>
    public bool TryNeed(int bits)
    {
        while (_cacheBits < bits)
        {
            if (!TryNextByte(out var b))
            {
                return false;
            }

            _cache = (_cache << 8) | b;
            _cacheBits += 8;
        }

        return true;
    }

    public uint ReadBits(int count)
    {
        if (count == 0)
        {
            return 0;
        }

        Need(count);
        _cacheBits -= count;
        var mask = count == 64 ? ulong.MaxValue : (1UL << count) - 1;
        return (uint)((_cache >> _cacheBits) & mask);
    }

    public ulong ReadBits64(int count)
    {
        if (count <= 32)
        {
            return ReadBits(count);
        }

        var high = ReadBits(count - 32);
        var low = ReadBits(32);
        return ((ulong)high << 32) | low;
    }

    public int ReadSigned(int count)
    {
        var raw = ReadBits(count);
        if (count >= 32)
        {
            return unchecked((int)raw);
        }

        var shift = 32 - count;
        return unchecked((int)(raw << shift)) >> shift;
    }

    /// <summary>Reads a unary-coded value: the number of zero bits before the terminating one bit.</summary>
    public int ReadUnary()
    {
        var total = 0;
        while (true)
        {
            if (_cacheBits == 0)
            {
                _cache = NextByte();
                _cacheBits = 8;
            }

            var available = _cache & ((1UL << _cacheBits) - 1);
            if (available == 0)
            {
                total += _cacheBits;
                _cacheBits = 0;
                continue;
            }

            var leadingZeros = _cacheBits - 1 - BitOperations.Log2(available);
            total += leadingZeros;
            _cacheBits -= leadingZeros + 1;
            return total;
        }
    }

    public int ReadRiceSigned(int parameter)
    {
        var quotient = (uint)ReadUnary();
        var remainder = parameter > 0 ? ReadBits(parameter) : 0u;
        var value = (quotient << parameter) | remainder;
        return unchecked((int)(value >> 1) ^ -(int)(value & 1));
    }

    /// <summary>Reads FLAC's extended UTF-8 coded number (up to 36 bits).</summary>
    public ulong ReadUtf8Number()
    {
        var first = ReadBits(8);
        int extraBytes;
        ulong value;

        if ((first & 0x80) == 0)
        {
            return first;
        }

        if ((first & 0xE0) == 0xC0)
        {
            extraBytes = 1;
            value = first & 0x1Fu;
        }
        else if ((first & 0xF0) == 0xE0)
        {
            extraBytes = 2;
            value = first & 0x0Fu;
        }
        else if ((first & 0xF8) == 0xF0)
        {
            extraBytes = 3;
            value = first & 0x07u;
        }
        else if ((first & 0xFC) == 0xF8)
        {
            extraBytes = 4;
            value = first & 0x03u;
        }
        else if ((first & 0xFE) == 0xFC)
        {
            extraBytes = 5;
            value = first & 0x01u;
        }
        else if (first == 0xFE)
        {
            extraBytes = 6;
            value = 0;
        }
        else
        {
            throw new InvalidDataException("Invalid UTF-8 coded number in FLAC frame header.");
        }

        for (var i = 0; i < extraBytes; i++)
        {
            var next = ReadBits(8);
            if ((next & 0xC0) != 0x80)
            {
                throw new InvalidDataException("Invalid UTF-8 continuation byte in FLAC frame header.");
            }

            value = (value << 6) | (next & 0x3Fu);
        }

        return value;
    }
}
