namespace Noct.Core.Library;

/// <summary>
/// Compares strings the way a person reads them: digit runs are compared as numbers, so
/// "2 Intro" comes before "10 Outro", and letters ignore case.
/// </summary>
public sealed class NaturalStringComparer : IComparer<string?>
{
    public static NaturalStringComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                var startX = i;
                var startY = j;
                while (i < x.Length && char.IsAsciiDigit(x[i])) { i++; }
                while (j < y.Length && char.IsAsciiDigit(y[j])) { j++; }

                // Compare as numbers without parsing: strip leading zeros, then length, then digits.
                var a = x.AsSpan(startX, i - startX).TrimStart('0');
                var b = y.AsSpan(startY, j - startY).TrimStart('0');
                if (a.Length != b.Length)
                {
                    return a.Length < b.Length ? -1 : 1;
                }

                var digits = a.SequenceCompareTo(b);
                if (digits != 0)
                {
                    return digits;
                }

                continue;
            }

            var cx = char.ToUpperInvariant(x[i]);
            var cy = char.ToUpperInvariant(y[j]);
            if (cx != cy)
            {
                return cx < cy ? -1 : 1;
            }

            i++;
            j++;
        }

        return (x.Length - i).CompareTo(y.Length - j);
    }
}

/// <summary>
/// The order tracks play in inside one album: disc, then track number when the file has one, then a
/// natural file-name order. Files without a track number follow numbered ones.
/// </summary>
public sealed class TrackOrder : IComparer<Track>
{
    public static TrackOrder Instance { get; } = new();

    public int Compare(Track? x, Track? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        var disc = (x.DiscNumber ?? 0).CompareTo(y.DiscNumber ?? 0);
        if (disc != 0)
        {
            return disc;
        }

        var number = (x.TrackNumber ?? int.MaxValue).CompareTo(y.TrackNumber ?? int.MaxValue);
        if (number != 0)
        {
            return number;
        }

        var name = NaturalStringComparer.Instance.Compare(Path.GetFileName(x.Path), Path.GetFileName(y.Path));
        return name != 0 ? name : string.CompareOrdinal(x.Path, y.Path);
    }
}
