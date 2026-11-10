namespace Noct.Core.Lyrics;

/// <summary>One displayed line. <see cref="Time"/> is null for lyrics without timestamps.</summary>
public sealed record LyricLine(TimeSpan? Time, string Text)
{
    /// <summary>
    /// Index of the first line that shares this line's timestamp. Lines with the same time are one
    /// group and light up together; for unsynced lyrics this is the line's own index.
    /// </summary>
    public int Group { get; init; }
}

/// <summary>Parsed lyrics, already ordered by time, with the file's offset applied.</summary>
public sealed class LyricsDocument
{
    public LyricsDocument(IReadOnlyList<LyricLine> lines, bool isSynced, string? title = null, string? artist = null,
        string? album = null, TimeSpan offset = default)
    {
        Lines = lines;
        IsSynced = isSynced;
        Title = title;
        Artist = artist;
        Album = album;
        Offset = offset;
    }

    public IReadOnlyList<LyricLine> Lines { get; }

    /// <summary>True when lines carry timestamps; only then can they follow playback or be clicked to seek.</summary>
    public bool IsSynced { get; }

    public string? Title { get; }

    public string? Artist { get; }

    public string? Album { get; }

    /// <summary>The [offset:] value from the file. Positive shows lyrics earlier (time = stamp - offset).</summary>
    public TimeSpan Offset { get; }

    public bool IsEmpty => Lines.Count == 0;

    /// <summary>
    /// Group index (the <see cref="LyricLine.Group"/> of the active lines) at <paramref name="position"/>,
    /// or -1 before the first timestamp or when the lyrics are not synced. Binary search over the
    /// sorted lines, so it is cheap enough to call on every position update.
    /// </summary>
    public int FindActiveGroup(TimeSpan position)
    {
        if (!IsSynced || Lines.Count == 0)
        {
            return -1;
        }

        int low = 0, high = Lines.Count - 1, found = -1;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            if (Lines[mid].Time!.Value <= position)
            {
                found = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return found < 0 ? -1 : Lines[found].Group;
    }
}
