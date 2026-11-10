namespace Noct.Core.Audio;

/// <summary>
/// The two CRC variants used by the FLAC bitstream.
/// CRC-8 (poly 0x07) protects each frame header; CRC-16 (poly 0x8005) protects the whole frame.
/// Both are plain MSB-first, initial value zero, no final XOR.
/// </summary>
internal static class FlacCrc
{
    private static readonly byte[] Crc8Table = BuildCrc8();
    private static readonly ushort[] Crc16Table = BuildCrc16();

    public static byte Update8(byte crc, byte value) => Crc8Table[crc ^ value];

    public static byte Compute8(ReadOnlySpan<byte> data)
    {
        byte crc = 0;
        foreach (var b in data)
        {
            crc = Crc8Table[crc ^ b];
        }

        return crc;
    }

    public static ushort Update16(ushort crc, byte value) =>
        (ushort)((crc << 8) ^ Crc16Table[((crc >> 8) ^ value) & 0xFF]);

    private static byte[] BuildCrc8()
    {
        var table = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            var crc = (byte)i;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (byte)((crc & 0x80) != 0 ? (crc << 1) ^ 0x07 : crc << 1);
            }

            table[i] = crc;
        }

        return table;
    }

    private static ushort[] BuildCrc16()
    {
        var table = new ushort[256];
        for (var i = 0; i < 256; i++)
        {
            var crc = (ushort)(i << 8);
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x8005 : crc << 1);
            }

            table[i] = crc;
        }

        return table;
    }
}
