using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Noct.Core.Lyrics;

/// <summary>
/// Reads LRC text. Supports fractional seconds of 1-3 digits, several timestamps on one line,
/// the id tags ar/ti/al and offset, and ignores enhanced per-word &lt;mm:ss.xx&gt; tags.
/// </summary>
public static partial class LrcParser
{
    /// <summary>Lyrics files larger than this are refused instead of read into memory.</summary>
    public const long MaxFileBytes = 1024 * 1024;

    [GeneratedRegex(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex StampPattern();

    [GeneratedRegex(@"^\[([A-Za-z#]+):(.*)\]\s*$")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"<\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?>")]
    private static partial Regex WordStampPattern();

    public static LyricsDocument Parse(string text)
    {
        string? title = null, artist = null, album = null;
        var offset = TimeSpan.Zero;
        var timed = new List<(TimeSpan Time, string Text)>();
        var plain = new List<string>();

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim('\r', '\uFEFF').Trim();
            if (line.Length == 0)
            {
                continue;
            }

            // Leading stamps: "[00:12.50][01:30.00]words". Anything after them is the text.
            var stamps = new List<TimeSpan>();
            var position = 0;
            while (StampPattern().Match(line, position) is { Success: true, Index: var at } match && at == position)
            {
                stamps.Add(ToTime(match));
                position = match.Index + match.Length;
            }

            if (stamps.Count > 0)
            {
                var words = WordStampPattern().Replace(line[position..], string.Empty).Trim();
                foreach (var stamp in stamps)
                {
                    timed.Add((stamp, words));
                }

                continue;
            }

            if (TagPattern().Match(line) is { Success: true } tag)
            {
                var key = tag.Groups[1].Value.ToLowerInvariant();
                var value = tag.Groups[2].Value.Trim();
                switch (key)
                {
                    case "ti": title = value; break;
                    case "ar": artist = value; break;
                    case "al": album = value; break;
                    case "offset" when int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var ms):
                        offset = TimeSpan.FromMilliseconds(ms);
                        break;
                }

                continue;
            }

            plain.Add(WordStampPattern().Replace(line, string.Empty).Trim());
        }

        if (timed.Count > 0)
        {
            // OrderBy is stable, so lines that share a stamp keep the order they had in the file.
            var ordered = timed
                .Select(t => (Time: Clamp(t.Time - offset), t.Text))
                .OrderBy(t => t.Time)
                .ToList();

            var lines = new List<LyricLine>(ordered.Count);
            var groupStart = 0;
            for (var i = 0; i < ordered.Count; i++)
            {
                if (i > 0 && ordered[i].Time != ordered[i - 1].Time)
                {
                    groupStart = i;
                }

                lines.Add(new LyricLine(ordered[i].Time, ordered[i].Text) { Group = groupStart });
            }

            return new LyricsDocument(lines, isSynced: true, title, artist, album, offset);
        }

        var unsynced = plain.Select((t, i) => new LyricLine(null, t) { Group = i }).ToList();
        return new LyricsDocument(unsynced, isSynced: false, title, artist, album, offset);
    }

    /// <summary>Decodes bytes as UTF-8 (with or without BOM) or UTF-16 with a BOM, else Windows Latin-1.</summary>
    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('\uFEFF');
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    private static TimeSpan ToTime(Match m)
    {
        var minutes = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var seconds = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var fraction = 0.0;
        if (m.Groups[3].Success)
        {
            // "5" is half a second, "50" too, "500" too: the digits are a decimal fraction.
            var digits = m.Groups[3].Value;
            fraction = int.Parse(digits, CultureInfo.InvariantCulture) / Math.Pow(10, digits.Length);
        }

        return TimeSpan.FromSeconds(minutes * 60 + seconds + fraction);
    }

    private static TimeSpan Clamp(TimeSpan t) => t < TimeSpan.Zero ? TimeSpan.Zero : t;
}
