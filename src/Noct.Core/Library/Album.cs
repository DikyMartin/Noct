namespace Noct.Core.Library;

/// <summary>
/// One folder that directly contains FLAC files. The folder, not the album tag, defines the album,
/// so a folder whose songs carry different album tags is still one album.
/// </summary>
public sealed class Album
{
    public const string VariousArtists = "Various artists";

    public Album(string folderPath, IReadOnlyList<Track> tracks)
    {
        FolderPath = folderPath;
        Id = Track.StableId(folderPath);
        Title = FolderTitle(folderPath);
        Tracks = tracks;

        var total = TimeSpan.Zero;
        foreach (var track in tracks)
        {
            total += track.Duration;
        }

        Duration = total;
        Artist = ArtistLabel(tracks);
        SearchText = string.Join(' ', new[] { Title, Artist }.Concat(tracks.Select(t => t.Title)).Where(s => !string.IsNullOrEmpty(s))).ToLowerInvariant();
    }

    /// <summary>Identity: derived from the full folder location, so equal names in different places differ.</summary>
    public Guid Id { get; }

    public string FolderPath { get; }

    /// <summary>The folder's name.</summary>
    public string Title { get; }

    /// <summary>The single artist when every track agrees, "Various artists" when they differ, null when untagged.</summary>
    public string? Artist { get; }

    public IReadOnlyList<Track> Tracks { get; }

    public TimeSpan Duration { get; }

    public string SearchText { get; }

    public static string FolderTitle(string folderPath)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(folderPath);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? folderPath : name;
    }

    private static string? ArtistLabel(IReadOnlyList<Track> tracks)
    {
        var artists = tracks
            .Select(t => t.AlbumArtist ?? t.Artist)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Take(2)
            .ToList();

        return artists.Count switch
        {
            0 => null,
            1 => artists[0],
            _ => VariousArtists,
        };
    }
}

/// <summary>Groups tracks into albums by the folder that directly contains them.</summary>
public static class AlbumBuilder
{
    public static IReadOnlyList<Album> Build(IEnumerable<Track> tracks)
    {
        // Key on the normalised folder so "D:\Music\A" and "d:/music/a/" are the same album, while
        // two different folders that happen to share a name stay apart.
        var groups = new Dictionary<string, (string Folder, List<Track> Tracks)>(StringComparer.Ordinal);
        foreach (var track in tracks)
        {
            var folder = Path.GetDirectoryName(track.Path) ?? string.Empty;
            var key = Track.NormalizePath(folder);
            if (!groups.TryGetValue(key, out var group))
            {
                group = (folder, []);
                groups[key] = group;
            }

            group.Tracks.Add(track);
        }

        return groups.Values
            .Select(g =>
            {
                g.Tracks.Sort(TrackOrder.Instance);
                return new Album(g.Folder, g.Tracks);
            })
            .OrderBy(a => a.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(a => a.FolderPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

/// <summary>Finds a cover image file in an album folder.</summary>
public static class AlbumCover
{
    // Order is priority: the first name that exists wins.
    private static readonly string[] Names = ["cover", "folder", "front", "albumart", "album"];
    private static readonly string[] Extensions = [".jpg", ".jpeg", ".png"];

    /// <summary>The cover.jpg/folder.png style file in the folder itself (not subfolders), or null.</summary>
    public static string? Find(string folderPath)
    {
        try
        {
            string? best = null;
            var bestRank = int.MaxValue;
            foreach (var file in Directory.EnumerateFiles(folderPath))
            {
                var extension = Path.GetExtension(file);
                if (!Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var nameRank = Array.FindIndex(Names, n => string.Equals(n, Path.GetFileNameWithoutExtension(file), StringComparison.OrdinalIgnoreCase));
                if (nameRank < 0)
                {
                    continue;
                }

                var rank = (nameRank * 10) + Array.FindIndex(Extensions, e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase));
                if (rank < bestRank)
                {
                    best = file;
                    bestRank = rank;
                }
            }

            return best;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
