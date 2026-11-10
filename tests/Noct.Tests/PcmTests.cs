using Noct.Core.Audio;

namespace Noct.Tests;

public sealed class PcmTests
{
    [Fact]
    public void Exact_width_path_applies_no_shift()
    {
        var source = new SourceFormat("FLAC", 44100, 2, 16);
        var target = new AudioFormat(44100, 2, 16, 16);

        var packer = new PcmPacker(source, target);

        Assert.Equal(0, packer.Shift);
    }

    [Theory]
    [InlineData(16, 16, 16)]
    [InlineData(24, 24, 24)]
    [InlineData(16, 32, 16)]
    [InlineData(24, 32, 24)]
    [InlineData(16, 32, 32)]
    [InlineData(24, 32, 32)]
    public void Packing_then_unpacking_returns_every_integer_unchanged(int sourceBits, int containerBits, int validBits)
    {
        var source = new SourceFormat("FLAC", 48000, 2, sourceBits);
        var target = new AudioFormat(48000, 2, containerBits, validBits);
        var packer = new PcmPacker(source, target);

        var max = (1 << (sourceBits - 1)) - 1;
        var min = -(1 << (sourceBits - 1));
        int[] values = [0, 1, -1, max, min, max - 1, min + 1, max / 2, min / 2, 12345 % max, -(12345 % max)];

        var packed = new byte[values.Length * (containerBits / 8)];
        packer.Pack(values, packed, values.Length);

        var restored = new int[values.Length];
        packer.Unpack(packed, restored, values.Length);

        Assert.Equal(values, restored);
    }

    [Fact]
    public void Sixteen_bit_value_in_a_32_bit_container_is_msb_aligned()
    {
        var source = new SourceFormat("FLAC", 44100, 1, 16);
        var target = new AudioFormat(44100, 1, 32, 32);
        var packer = new PcmPacker(source, target);

        var packed = new byte[4];
        packer.Pack([0x1234], packed, 1);

        // 0x1234 << 16 = 0x12340000, little endian.
        Assert.Equal(new byte[] { 0x00, 0x00, 0x34, 0x12 }, packed);
        Assert.Equal(16, packer.Shift);
    }

    [Fact]
    public void Negative_full_scale_keeps_its_sign_through_a_24_bit_container()
    {
        var source = new SourceFormat("FLAC", 96000, 2, 24);
        var target = new AudioFormat(96000, 2, 24, 24);
        var packer = new PcmPacker(source, target);

        var packed = new byte[3];
        packer.Pack([-8388608], packed, 1);

        Assert.Equal(new byte[] { 0x00, 0x00, 0x80 }, packed);
    }

    [Fact]
    public void Narrowing_a_source_is_refused_because_it_would_discard_samples()
    {
        var source = new SourceFormat("FLAC", 96000, 2, 24);
        var target = new AudioFormat(96000, 2, 16, 16);

        Assert.Throws<ArgumentException>(() => new PcmPacker(source, target));
    }

    [Fact]
    public void Channel_count_mismatch_is_refused_because_the_packer_never_mixes()
    {
        var source = new SourceFormat("FLAC", 44100, 2, 16);
        var target = new AudioFormat(44100, 1, 16, 16);

        Assert.Throws<ArgumentException>(() => new PcmPacker(source, target));
    }

    [Fact]
    public void Container_wider_than_valid_bits_is_reported_as_such()
    {
        var format = new AudioFormat(48000, 2, 32, 24);

        Assert.True(format.IsContainerWiderThanValid);
        Assert.Contains("24-bit in 32-bit container", format.Describe());
    }

    [Theory]
    [InlineData(16, 44100)]
    [InlineData(24, 96000)]
    [InlineData(24, 192000)]
    public void Exclusive_candidates_always_keep_the_source_rate_and_never_narrow(int bits, int rate)
    {
        var source = new SourceFormat("FLAC", rate, 2, bits);

        var candidates = OutputFormatPolicy.ExclusiveCandidates(source);

        Assert.NotEmpty(candidates);
        Assert.All(candidates, candidate =>
        {
            Assert.Equal(rate, candidate.SampleRate);
            var accepted = AudioFormat.FromWaveFormat(candidate);
            Assert.True(accepted.ValidBits >= bits, $"{accepted.Describe()} would narrow a {bits}-bit source");
        });

        // The exact-width format is always offered first.
        var first = AudioFormat.FromWaveFormat(candidates[0]);
        Assert.Equal(bits, first.ContainerBits);
        Assert.Equal(bits, first.ValidBits);
    }

    [Fact]
    public void Lossless_check_rejects_a_rate_change()
    {
        var source = new SourceFormat("FLAC", 44100, 2, 16);

        Assert.True(OutputFormatPolicy.IsLossless(source, new AudioFormat(44100, 2, 32, 32)));
        Assert.False(OutputFormatPolicy.IsLossless(source, new AudioFormat(48000, 2, 16, 16)));
        Assert.False(OutputFormatPolicy.IsLossless(source, new AudioFormat(44100, 2, 32, 32, SampleEncoding.IeeeFloat)));
    }
}
