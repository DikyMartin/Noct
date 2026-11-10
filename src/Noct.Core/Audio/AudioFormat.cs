using NAudio.Wave;

namespace Noct.Core.Audio;

public enum SampleEncoding
{
    Pcm,
    IeeeFloat,
}

/// <summary>
/// A concrete audio format. <paramref name="ContainerBits"/> is the physical width of a sample slot
/// and <paramref name="ValidBits"/> is how many of those bits carry source information. Keeping the
/// two apart is what lets the UI report "24-bit in a 32-bit container" honestly rather than claiming
/// the source is 32-bit.
/// </summary>
public sealed record AudioFormat(
    int SampleRate,
    int Channels,
    int ContainerBits,
    int ValidBits,
    SampleEncoding Encoding = SampleEncoding.Pcm)
{
    public int BytesPerFrame => Channels * (ContainerBits / 8);

    public bool IsContainerWiderThanValid => ContainerBits != ValidBits;

    public string Describe()
    {
        var kind = Encoding == SampleEncoding.IeeeFloat ? "float" : "bit";
        return IsContainerWiderThanValid
            ? $"{SampleRate / 1000.0:0.###} kHz / {ValidBits}-{kind} in {ContainerBits}-bit container / {ChannelText()}"
            : $"{SampleRate / 1000.0:0.###} kHz / {ContainerBits}-{kind} / {ChannelText()}";
    }

    public string ChannelText() => Channels switch
    {
        1 => "mono",
        2 => "stereo",
        _ => $"{Channels} channels",
    };

    public static AudioFormat FromWaveFormat(WaveFormat format)
    {
        var encoding = format.Encoding == WaveFormatEncoding.IeeeFloat ? SampleEncoding.IeeeFloat : SampleEncoding.Pcm;
        var validBits = format.BitsPerSample;

        if (format is WaveFormatExtensible extensible)
        {
            validBits = extensible.ValidBitsPerSample > 0 ? extensible.ValidBitsPerSample : format.BitsPerSample;
            encoding = extensible.SubFormat == AudioSubFormats.IeeeFloat ? SampleEncoding.IeeeFloat : SampleEncoding.Pcm;
        }

        return new AudioFormat(format.SampleRate, format.Channels, format.BitsPerSample, validBits, encoding);
    }
}

/// <summary>The two KSDATAFORMAT subtype GUIDs WASAPI uses for PCM and float.</summary>
public static class AudioSubFormats
{
    public static readonly Guid Pcm = new("00000001-0000-0010-8000-00aa00389b71");
    public static readonly Guid IeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");
}

/// <summary>Describes the source material, independent of any output device.</summary>
public sealed record SourceFormat(string Codec, int SampleRate, int Channels, int BitsPerSample)
{
    public string Describe() =>
        $"{Codec} / {SampleRate / 1000.0:0.###} kHz / {BitsPerSample}-bit / {(Channels == 2 ? "stereo" : Channels == 1 ? "mono" : $"{Channels} channels")}";

    public static SourceFormat FromFlac(FlacStreamInfo info) =>
        new("FLAC", info.SampleRate, info.Channels, info.BitsPerSample);
}
