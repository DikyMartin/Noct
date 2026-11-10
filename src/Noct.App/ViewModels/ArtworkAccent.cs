using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Noct.App.ViewModels;

/// <summary>
/// Picks one muted tone from artwork for the Now Playing page's faint glow. It is the average of a
/// tiny copy of the image, weighted by saturation so a coloured cover is not washed to grey by its
/// black or white borders. Greyscale covers give no accent at all rather than an invented colour.
/// </summary>
public static class ArtworkAccent
{
    private const int Side = 24;

    public static Color? From(BitmapSource source)
    {
        if (source.PixelWidth == 0 || source.PixelHeight == 0)
        {
            return null;
        }

        var scaled = new TransformedBitmap(source, new ScaleTransform((double)Side / source.PixelWidth, (double)Side / source.PixelHeight));
        var converted = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var pixels = new byte[width * height * 4];
        converted.CopyPixels(pixels, width * 4, 0);

        double r = 0, g = 0, b = 0, weight = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            double pb = pixels[i], pg = pixels[i + 1], pr = pixels[i + 2];
            var max = Math.Max(pr, Math.Max(pg, pb));
            var min = Math.Min(pr, Math.Min(pg, pb));
            var saturation = max <= 0 ? 0 : (max - min) / max;

            // Near-black pixels carry no usable hue.
            var w = saturation * saturation * (max / 255.0);
            r += pr * w;
            g += pg * w;
            b += pb * w;
            weight += w;
        }

        if (weight < 0.5)
        {
            return null;
        }

        r /= weight;
        g /= weight;
        b /= weight;

        // Hold the result in a mid-tone band so it never turns neon or vanishes into the background.
        var peak = Math.Max(r, Math.Max(g, b));
        if (peak > 0)
        {
            var scale = Math.Clamp(170 / peak, 0.6, 1.6);
            r = Math.Min(255, r * scale);
            g = Math.Min(255, g * scale);
            b = Math.Min(255, b * scale);
        }

        return Color.FromRgb((byte)r, (byte)g, (byte)b);
    }
}
