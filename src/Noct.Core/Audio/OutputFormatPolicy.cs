using NAudio.Wave;

namespace Noct.Core.Audio;

/// <summary>
/// Builds the ordered list of output formats to offer a device for a given source.
/// </summary>
/// <remarks>
/// Two rules shape every candidate list:
/// <list type="bullet">
/// <item>The sample rate is always the source rate. Nothing here resamples, so a device that
/// cannot run at the source rate is a negotiation failure, not an opportunity to convert.</item>
/// <item>The container is never narrower than the source's valid bits. Widening a sample into a
/// larger slot is a lossless shift; narrowing would discard information.</item>
/// </list>
/// </remarks>
public static class OutputFormatPolicy
{
    /// <summary>Candidate formats for exclusive mode, best fit first.</summary>
    public static IReadOnlyList<WaveFormat> ExclusiveCandidates(SourceFormat source)
    {
        var candidates = new List<WaveFormat>();
        var rate = source.SampleRate;
        var channels = source.Channels;
        var mask = ChannelMask(channels);

        // Exact match first, in both the extensible and the legacy PCM form. Some drivers only
        // advertise one of the two even though they are the same thing on the wire.
        AddPcm(candidates, rate, channels, container: source.BitsPerSample, valid: source.BitsPerSample, mask);

        // Then lossless widening, narrowest useful container first.
        foreach (var container in new[] { 24, 32 })
        {
            if (container <= source.BitsPerSample)
            {
                continue;
            }

            AddPcm(candidates, rate, channels, container, valid: source.BitsPerSample, mask);
        }

        // Finally a full-width container with all bits declared valid. Still a lossless shift of
        // the source value; the extra low bits are zero.
        if (source.BitsPerSample < 32)
        {
            AddPcm(candidates, rate, channels, container: 32, valid: 32, mask);
        }

        return candidates;
    }

    /// <summary>
    /// Candidate formats for shared mode. Shared mode always routes through the Windows mixer,
    /// so the only goal here is a format the mixer accepts without the app resampling anything.
    /// </summary>
    public static IReadOnlyList<WaveFormat> SharedCandidates(SourceFormat source)
    {
        var candidates = new List<WaveFormat>();
        var mask = ChannelMask(source.Channels);
        AddPcm(candidates, source.SampleRate, source.Channels, source.BitsPerSample, source.BitsPerSample, mask);
        if (source.BitsPerSample < 32)
        {
            AddPcm(candidates, source.SampleRate, source.Channels, 32, source.BitsPerSample, mask);
        }

        return candidates;
    }

    private static void AddPcm(List<WaveFormat> candidates, int rate, int channels, int container, int valid, int mask)
    {
        if (container % 8 != 0 || container is < 8 or > 32 || valid > container)
        {
            return;
        }

        candidates.Add(new WaveFormatExtensible(rate, container, channels, AudioSubFormats.Pcm, valid, mask));

        // The legacy WAVEFORMATEX form only exists when the container is fully used.
        if (container == valid && channels <= 2)
        {
            candidates.Add(new WaveFormat(rate, container, channels));
        }
    }

    /// <summary>Standard speaker masks; anything unusual gets a plain front-pair mask.</summary>
    public static int ChannelMask(int channels) => channels switch
    {
        1 => 0x4,                 // front centre
        2 => 0x3,                 // front left + right
        3 => 0x7,
        4 => 0x33,                // quad
        5 => 0x37,
        6 => 0x3F,                // 5.1
        8 => 0x63F,               // 7.1
        _ => (1 << channels) - 1,
    };

    /// <summary>
    /// Describes the lossless repacking needed to place source samples into the accepted format,
    /// or null when the source sits in the container exactly.
    /// </summary>
    public static string? DescribePacking(SourceFormat source, AudioFormat accepted)
    {
        if (accepted.Encoding == SampleEncoding.IeeeFloat)
        {
            return $"{source.BitsPerSample}-bit integer converted to 32-bit float";
        }

        if (accepted.ContainerBits == source.BitsPerSample && accepted.ValidBits == source.BitsPerSample)
        {
            return null;
        }

        if (accepted.ValidBits == source.BitsPerSample)
        {
            return $"{source.BitsPerSample}-bit samples placed in a {accepted.ContainerBits}-bit container, MSB aligned (lossless)";
        }

        if (accepted.ValidBits > source.BitsPerSample)
        {
            return $"{source.BitsPerSample}-bit samples shifted into {accepted.ValidBits} valid bits, low bits zero (lossless)";
        }

        return $"{source.BitsPerSample}-bit samples reduced to {accepted.ValidBits} valid bits (lossy)";
    }

    /// <summary>True when the accepted format can carry every source sample value unchanged.</summary>
    public static bool IsLossless(SourceFormat source, AudioFormat accepted) =>
        accepted.Encoding == SampleEncoding.Pcm &&
        accepted.SampleRate == source.SampleRate &&
        accepted.Channels == source.Channels &&
        accepted.ValidBits >= source.BitsPerSample;
}
