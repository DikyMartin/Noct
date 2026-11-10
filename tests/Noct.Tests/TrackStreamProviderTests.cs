using Noct.Core.Audio;

namespace Noct.Tests;

/// <summary>
/// Software-path evidence. These tests prove that the integers the decoder produced reach the
/// buffer handed to the output device unchanged, and that a gapless join neither inserts nor drops
/// a sample. They say nothing about what a DAC does afterwards; that is a separate, untested claim.
/// </summary>
public sealed class TrackStreamProviderTests
{
    private static AudioFormat ExactFormat(FlacStreamInfo info) =>
        new(info.SampleRate, info.Channels, info.BitsPerSample <= 16 ? 16 : 24, info.BitsPerSample);

    /// <summary>Drains a provider through reads of an awkward size so joins land mid-buffer.</summary>
    private static byte[] Drain(TrackStreamProvider provider, AudioFormat format, int bufferFrames)
    {
        var buffer = new byte[bufferFrames * format.BytesPerFrame];
        var output = new MemoryStream();

        for (var guard = 0; guard < 5_000_000 && !provider.Ended; guard++)
        {
            var read = provider.Read(buffer);
            Assert.Equal(buffer.Length, read);
            output.Write(buffer, 0, read);
        }

        Assert.True(provider.Ended, "Provider never reported the end of its source.");

        // The final read is padded with silence; only produced frames are real audio.
        var realBytes = (int)(provider.ProducedFrames * format.BytesPerFrame);
        return output.GetBuffer().AsSpan(0, realBytes).ToArray();
    }

    private static int[] Unpack(byte[] packed, SourceFormat source, AudioFormat format)
    {
        var packer = new PcmPacker(source, format);
        var samples = new int[packed.Length / (format.ContainerBits / 8)];
        packer.Unpack(packed, samples, samples.Length);
        return samples;
    }

    [Theory]
    [InlineData("tone_16_44100_stereo", 16)]
    [InlineData("tone_16_44100_mono", 16)]
    [InlineData("tone_16_22050_stereo", 16)]
    [InlineData("tone_16_88200_quad", 16)]
    [InlineData("noise_16_44100_stereo", 16)]
    [InlineData("extremes_16_44100_stereo", 16)]
    [InlineData("silence_16_44100_stereo", 16)]
    [InlineData("tone_24_48000_stereo", 24)]
    [InlineData("tone_24_96000_stereo", 24)]
    [InlineData("noise_24_48000_stereo", 24)]
    [InlineData("extremes_24_44100_stereo", 24)]
    public void Pre_output_samples_equal_the_encoder_input_in_the_exact_container(string name, int bits)
    {
        var decoder = FlacDecoder.Open(TestData.Flac(name));
        var source = SourceFormat.FromFlac(decoder.StreamInfo);
        var format = ExactFormat(decoder.StreamInfo);

        using var provider = new TrackStreamProvider(format);
        provider.SetCurrent(Guid.NewGuid(), decoder);

        var actual = Unpack(Drain(provider, format, bufferFrames: 1777), source, format);
        var expected = TestData.ReferenceSamples(name, bits);

        Assert.Equal(expected.Length, actual.Length);
        Assert.True(expected.AsSpan().SequenceEqual(actual), $"{name}: pre-output samples differ from the reference PCM.");
    }

    [Theory]
    [InlineData("tone_16_44100_stereo", 16)]
    [InlineData("tone_24_48000_stereo", 24)]
    [InlineData("extremes_24_44100_stereo", 24)]
    public void Widening_into_a_32_bit_container_is_lossless_and_reversible(string name, int bits)
    {
        var decoder = FlacDecoder.Open(TestData.Flac(name));
        var source = SourceFormat.FromFlac(decoder.StreamInfo);
        var format = new AudioFormat(source.SampleRate, source.Channels, 32, bits);

        using var provider = new TrackStreamProvider(format);
        provider.SetCurrent(Guid.NewGuid(), decoder);

        var actual = Unpack(Drain(provider, format, bufferFrames: 4096), source, format);
        var expected = TestData.ReferenceSamples(name, bits);

        Assert.True(expected.AsSpan().SequenceEqual(actual));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(777)]
    [InlineData(4096)]
    [InlineData(10_000)]
    public void Gapless_join_preserves_exact_sample_order_and_count_across_every_boundary(int bufferFrames)
    {
        string[] names = ["tone_16_44100_stereo", "noise_16_44100_stereo", "dc_16_44100_stereo", "extremes_16_44100_stereo"];

        var first = FlacDecoder.Open(TestData.Flac(names[0]));
        var format = ExactFormat(first.StreamInfo);
        var source = SourceFormat.FromFlac(first.StreamInfo);
        var ids = names.Select(_ => Guid.NewGuid()).ToArray();

        using var provider = new TrackStreamProvider(format);
        var started = new List<Guid>();
        var completed = new List<Guid>();
        var armed = 1;

        provider.TrackStarted += (_, id) =>
        {
            started.Add(id);

            // Arm the following track as soon as the previous one begins, as the engine does.
            if (armed < names.Length)
            {
                var next = FlacDecoder.Open(TestData.Flac(names[armed]));
                Assert.True(provider.TrySetNext(ids[armed], next));
                armed++;
            }
        };
        provider.TrackCompleted += (_, id) => completed.Add(id);

        provider.SetCurrent(ids[0], first);

        var actual = Unpack(Drain(provider, format, bufferFrames), source, format);
        var expected = names.SelectMany(n => TestData.ReferenceSamples(n, 16)).ToArray();

        Assert.Equal(expected.Length, actual.Length);
        Assert.True(expected.AsSpan().SequenceEqual(actual), "A sample was inserted, dropped or reordered at a join.");
        Assert.Equal(ids, started);
        Assert.Equal(ids, completed);
    }

    [Fact]
    public void Position_mapping_follows_the_stream_across_a_join()
    {
        var first = FlacDecoder.Open(TestData.Flac("tone_16_44100_stereo"));
        var format = ExactFormat(first.StreamInfo);
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();

        using var provider = new TrackStreamProvider(format);
        provider.SetCurrent(firstId, first);
        Assert.True(provider.TrySetNext(secondId, FlacDecoder.Open(TestData.Flac("noise_16_44100_stereo"))));

        Drain(provider, format, 3000);

        var firstFrames = first.TotalSamples;

        var inFirst = provider.Resolve(firstFrames - 1);
        var atJoin = provider.Resolve(firstFrames);
        var intoSecond = provider.Resolve(firstFrames + 250);

        Assert.Equal(firstId, inFirst!.Value.TrackId);
        Assert.Equal(firstFrames - 1, inFirst.Value.SampleOffset);
        Assert.Equal(secondId, atJoin!.Value.TrackId);
        Assert.Equal(0, atJoin.Value.SampleOffset);
        Assert.Equal(secondId, intoSecond!.Value.TrackId);
        Assert.Equal(250, intoSecond.Value.SampleOffset);
    }

    [Fact]
    public void A_track_with_a_different_rate_or_depth_is_refused_so_the_engine_reinitialises_instead()
    {
        var first = FlacDecoder.Open(TestData.Flac("tone_16_44100_stereo"));
        var format = ExactFormat(first.StreamInfo);

        using var provider = new TrackStreamProvider(format);
        provider.SetCurrent(Guid.NewGuid(), first);

        using var differentRate = FlacDecoder.Open(TestData.Flac("tone_24_48000_stereo"));
        using var differentChannels = FlacDecoder.Open(TestData.Flac("tone_16_44100_mono"));
        using var lowerRate = FlacDecoder.Open(TestData.Flac("tone_16_22050_stereo"));

        Assert.False(provider.TrySetNext(Guid.NewGuid(), differentRate));
        Assert.False(provider.TrySetNext(Guid.NewGuid(), differentChannels));
        Assert.False(provider.TrySetNext(Guid.NewGuid(), lowerRate));
    }

    [Fact]
    public void Seeking_mid_stream_continues_with_the_exact_samples_from_that_offset()
    {
        var decoder = FlacDecoder.Open(TestData.Flac("tone_16_44100_stereo"));
        var format = ExactFormat(decoder.StreamInfo);
        var source = SourceFormat.FromFlac(decoder.StreamInfo);
        var id = Guid.NewGuid();

        using var provider = new TrackStreamProvider(format);
        provider.SetCurrent(id, decoder);

        // Consume a little, then jump.
        var scratch = new byte[2000 * format.BytesPerFrame];
        provider.Read(scratch);
        const int target = 30_000;
        Assert.True(provider.SeekCurrent(target));

        var drained = Unpack(Drain(provider, format, 1500), source, format);
        var expected = TestData.ReferenceSamples("tone_16_44100_stereo", 16).AsSpan(target * format.Channels).ToArray();

        // Drain() truncates to ProducedFrames, which also counts the 2000 frames read before the
        // seek (they are not in the drained bytes). The real audio after the seek is therefore the
        // leading part of the result; whatever follows it is silence padding.
        Assert.True(drained.Length >= expected.Length);
        Assert.True(expected.AsSpan().SequenceEqual(drained.AsSpan(0, expected.Length)));
        Assert.All(drained.AsSpan(expected.Length).ToArray(), sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void A_corrupt_frame_ends_the_stream_and_reports_the_failure_instead_of_looping()
    {
        var decoder = FlacDecoder.Open(TestData.Flac("corrupt_bitflip"));
        var format = ExactFormat(decoder.StreamInfo);

        using var provider = new TrackStreamProvider(format);
        Exception? failure = null;
        var exhausted = 0;
        provider.DecodeFailed += (_, ex) => failure = ex;
        provider.SourceExhausted += (_, _) => exhausted++;
        provider.SetCurrent(Guid.NewGuid(), decoder);

        Drain(provider, format, 4096);

        Assert.NotNull(failure);
        Assert.IsType<InvalidDataException>(failure);
        Assert.Equal(1, exhausted);
    }

    [Fact]
    public void Exhaustion_is_announced_once_even_if_the_device_keeps_reading()
    {
        var decoder = FlacDecoder.Open(TestData.Flac("dc_16_44100_stereo"));
        var format = ExactFormat(decoder.StreamInfo);

        using var provider = new TrackStreamProvider(format);
        var exhausted = 0;
        provider.SourceExhausted += (_, _) => exhausted++;
        provider.SetCurrent(Guid.NewGuid(), decoder);

        Drain(provider, format, 4096);
        var buffer = new byte[4096 * format.BytesPerFrame];
        for (var i = 0; i < 10; i++)
        {
            provider.Read(buffer);
        }

        Assert.Equal(1, exhausted);
    }

    [Fact]
    public void Padding_after_the_end_is_silence_and_is_not_counted_as_produced_audio()
    {
        var decoder = FlacDecoder.Open(TestData.Flac("dc_16_44100_stereo"));
        var format = ExactFormat(decoder.StreamInfo);
        var total = decoder.TotalSamples;

        using var provider = new TrackStreamProvider(format);
        provider.SetCurrent(Guid.NewGuid(), decoder);

        var buffer = new byte[(total + 5000) * format.BytesPerFrame];
        Array.Fill(buffer, (byte)0xAB);
        provider.Read(buffer);

        Assert.Equal(total, provider.ProducedFrames);
        Assert.All(buffer.AsSpan((int)(total * format.BytesPerFrame)).ToArray(), b => Assert.Equal(0, b));
    }
}
