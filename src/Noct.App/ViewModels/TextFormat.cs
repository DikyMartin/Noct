using System.Globalization;
using Noct.Core.Audio;

namespace Noct.App.ViewModels;

/// <summary>Shared text formatting so both windows show identical strings.</summary>
public static class TextFormat
{
    public static string Duration(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        return value.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)value.TotalHours}:{value.Minutes:D2}:{value.Seconds:D2}")
            : string.Create(CultureInfo.InvariantCulture, $"{value.Minutes}:{value.Seconds:D2}");
    }

    public static string KiloHertz(int sampleRate) =>
        (sampleRate / 1000.0).ToString("0.###", CultureInfo.InvariantCulture) + " kHz";

    public static string Channels(int channels) => channels switch
    {
        1 => "mono",
        2 => "stereo",
        _ => $"{channels} ch",
    };

    /// <summary>"24-bit / 96 kHz", the compact form used in track rows.</summary>
    public static string CompactFormat(int bits, int sampleRate) => $"{bits}-bit / {KiloHertz(sampleRate)}";

    public static string BackendName(OutputBackend backend) => backend switch
    {
        OutputBackend.WasapiShared => "WASAPI Shared",
        OutputBackend.WasapiExclusive => "WASAPI Exclusive",
        OutputBackend.Asio => "ASIO",
        _ => backend.ToString(),
    };

    public static string ModeName(OutputMode mode) => mode switch
    {
        OutputMode.Transparent => "Transparent",
        _ => "Normal",
    };
}
