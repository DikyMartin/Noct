namespace Noct.Core.Audio;

/// <summary>
/// Writes decoded integer samples into an output container.
/// </summary>
/// <remarks>
/// <para>
/// The only operation applied is a left shift when the container carries more valid bits than the
/// source. That is a lossless change of scale required by the container convention: WASAPI expects
/// a sample to occupy the most significant bits of its slot, so a 24-bit value in a 32-bit slot is
/// stored as <c>value &lt;&lt; 8</c>. No gain, dither, rounding or clipping happens here.
/// </para>
/// <para>
/// <see cref="Shift"/> is exposed so a test can assert the shift is zero on the exact-match path
/// and that unpacking reverses it exactly.
/// </para>
/// </remarks>
public sealed class PcmPacker
{
    private readonly int _containerBytes;
    private readonly int _shift;
    private readonly bool _float;
    private readonly float _floatScale;

    public PcmPacker(SourceFormat source, AudioFormat target)
    {
        Source = source;
        Target = target;
        _containerBytes = target.ContainerBits / 8;
        _float = target.Encoding == SampleEncoding.IeeeFloat;
        _shift = Math.Max(0, target.ValidBits - source.BitsPerSample);
        _floatScale = 1f / (1 << (source.BitsPerSample - 1));

        if (target.Channels != source.Channels)
        {
            throw new ArgumentException("Channel count must match; this packer does not mix channels.", nameof(target));
        }

        if (!_float && target.ValidBits < source.BitsPerSample)
        {
            throw new ArgumentException(
                $"Refusing to pack {source.BitsPerSample}-bit samples into {target.ValidBits} valid bits: that would discard sample data.",
                nameof(target));
        }
    }

    public SourceFormat Source { get; }

    public AudioFormat Target { get; }

    /// <summary>Lossless left shift applied to each sample. Zero on an exact-width path.</summary>
    public int Shift => _shift;

    public int BytesPerFrame => _containerBytes * Target.Channels;

    /// <summary>Packs <paramref name="sampleCount"/> interleaved samples and returns bytes written.</summary>
    public int Pack(ReadOnlySpan<int> samples, Span<byte> destination, int sampleCount)
    {
        var byteCount = sampleCount * _containerBytes;
        if (destination.Length < byteCount)
        {
            throw new ArgumentException("Destination buffer is too small for the requested sample count.", nameof(destination));
        }

        if (_float)
        {
            for (var i = 0; i < sampleCount; i++)
            {
                var value = samples[i] * _floatScale;
                BitConverter.TryWriteBytes(destination[(i * 4)..], value);
            }

            return byteCount;
        }

        switch (_containerBytes)
        {
            case 2:
                for (var i = 0; i < sampleCount; i++)
                {
                    var value = samples[i] << _shift;
                    var offset = i * 2;
                    destination[offset] = (byte)value;
                    destination[offset + 1] = (byte)(value >> 8);
                }

                break;

            case 3:
                for (var i = 0; i < sampleCount; i++)
                {
                    var value = samples[i] << _shift;
                    var offset = i * 3;
                    destination[offset] = (byte)value;
                    destination[offset + 1] = (byte)(value >> 8);
                    destination[offset + 2] = (byte)(value >> 16);
                }

                break;

            case 4:
                for (var i = 0; i < sampleCount; i++)
                {
                    var value = samples[i] << _shift;
                    var offset = i * 4;
                    destination[offset] = (byte)value;
                    destination[offset + 1] = (byte)(value >> 8);
                    destination[offset + 2] = (byte)(value >> 16);
                    destination[offset + 3] = (byte)(value >> 24);
                }

                break;

            case 1:
                for (var i = 0; i < sampleCount; i++)
                {
                    destination[i] = (byte)(samples[i] << _shift);
                }

                break;

            default:
                throw new NotSupportedException($"Unsupported container width: {Target.ContainerBits} bits.");
        }

        return byteCount;
    }

    /// <summary>
    /// Reverses <see cref="Pack"/>. Used by the PCM equality test to prove the round trip through
    /// the container is exact for integer paths.
    /// </summary>
    public void Unpack(ReadOnlySpan<byte> packed, Span<int> samples, int sampleCount)
    {
        if (_float)
        {
            throw new NotSupportedException("Float packing is not bit-exact and has no integer inverse.");
        }

        for (var i = 0; i < sampleCount; i++)
        {
            var offset = i * _containerBytes;
            var value = 0;
            for (var b = 0; b < _containerBytes; b++)
            {
                value |= packed[offset + b] << (8 * b);
            }

            var totalBits = _containerBytes * 8;
            if (totalBits < 32)
            {
                var signShift = 32 - totalBits;
                value = (value << signShift) >> signShift;
            }

            samples[i] = value >> _shift;
        }
    }
}
