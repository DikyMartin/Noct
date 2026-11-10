namespace Noct.Core.Lyrics;

/// <summary>What happened when lyrics were looked up for one song.</summary>
public sealed record LyricsResult(string? SourcePath, LyricsDocument? Document, string? Problem)
{
    public static LyricsResult None { get; } = new(null, null, null);

    public bool Found => Document is { IsEmpty: false };
}

/// <summary>Finds and reads the .lrc that belongs to a song. Never writes to the music folder.</summary>
public static class LyricsLocator
{
    /// <summary>The .lrc with the same base name beside the song (Song.flac, Song.lrc), or null.</summary>
    public static string? Find(string trackPath)
    {
        // Windows paths are case-insensitive, so Song.LRC and song.lrc both match Song.flac.
        var sibling = Path.ChangeExtension(trackPath, ".lrc");
        return File.Exists(sibling) ? sibling : null;
    }

    public static async Task<LyricsResult> LoadAsync(string path, CancellationToken cancellation)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > LrcParser.MaxFileBytes)
            {
                return new LyricsResult(path, null, $"{info.Name} is larger than 1 MB, so it was not read.");
            }

            var bytes = await File.ReadAllBytesAsync(path, cancellation).ConfigureAwait(false);
            var document = LrcParser.Parse(LrcParser.Decode(bytes));
            return document.IsEmpty
                ? new LyricsResult(path, null, $"{info.Name} has no lyrics in it.")
                : new LyricsResult(path, document, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new LyricsResult(path, null, $"Could not read the lyrics file: {ex.Message}");
        }
    }
}
