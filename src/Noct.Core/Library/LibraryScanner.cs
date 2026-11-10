using Noct.Core.Audio;

namespace Noct.Core.Library;

public sealed record ScanProgress(string CurrentPath, int Found, int Failed, int Examined, bool Enumerating);

public sealed record ScanResult(
    IReadOnlyList<Track> Tracks,
    IReadOnlyList<TrackProblem> Problems,
    IReadOnlyList<string> Roots,
    bool Cancelled);

/// <summary>
/// Walks folders for FLAC files and reads each one's metadata.
/// </summary>
/// <remarks>
/// Runs entirely off the UI thread and honours cancellation between files, so a large folder never
/// freezes the window and Cancel takes effect promptly. A file that cannot be read is recorded as a
/// <see cref="TrackProblem"/> and the scan continues: one bad file must not abort the library.
/// Music files are opened read-only and never modified.
/// </remarks>
public sealed class LibraryScanner
{
    private static readonly EnumerationOptions Enumeration = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        MatchCasing = MatchCasing.CaseInsensitive,
    };

    /// <param name="roots">Folders to search recursively.</param>
    /// <param name="progress">Receives updates about every few files.</param>
    /// <param name="cancellationToken">Stops the scan between files.</param>
    /// <param name="existing">
    /// Previously indexed tracks. A file whose size and write time are unchanged reuses its entry,
    /// so a rescan of a large library re-reads only what changed.
    /// </param>
    public async Task<ScanResult> ScanAsync(
        IReadOnlyList<string> roots,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<Guid, Track>? existing = null,
        IReadOnlyCollection<string>? excludedFolders = null)
    {
        var tracks = new List<Track>();
        var problems = new List<TrackProblem>();
        var cancelled = false;

        try
        {
            await Task.Run(
            () =>
            {
                var files = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var excluded = excludedFolders?.Select(Track.NormalizePath).ToHashSet(StringComparer.Ordinal);
                foreach (var root in roots)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        cancelled = true;
                        return;
                    }

                    try
                    {
                        if (!Directory.Exists(root))
                        {
                            problems.Add(new TrackProblem(root, "Folder is missing or unavailable."));
                            continue;
                        }

                        foreach (var file in Directory.EnumerateFiles(root, "*.flac", Enumeration))
                        {
                            if (cancellationToken.IsCancellationRequested)
                            {
                                cancelled = true;
                                return;
                            }

                            // Overlapping source folders list the same file twice; index it once.
                            // Files in a folder the user removed from the library stay out.
                            if (!seen.Add(Track.NormalizePath(file)) || IsExcluded(file, excluded))
                            {
                                continue;
                            }

                            files.Add(file);
                            if (files.Count % 64 == 0)
                            {
                                progress?.Report(new ScanProgress(file, 0, 0, files.Count, true));
                            }
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        problems.Add(new TrackProblem(root, $"Folder could not be read: {ex.Message}"));
                    }
                }

                // Files are independent, so a few read at once. Results go to a fixed slot per file,
                // which keeps the output order the same as the enumeration order on every run.
                var slots = new Track?[files.Count];
                var failures = new System.Collections.Concurrent.ConcurrentQueue<TrackProblem>();
                var examined = 0;
                var found = 0;
                var options = new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
                    CancellationToken = cancellationToken,
                };

                try
                {
                    Parallel.For(0, files.Count, options, index =>
                    {
                        var file = files[index];
                        try
                        {
                            var info = new FileInfo(file);
                            if (existing is not null &&
                                existing.TryGetValue(Track.StableId(file), out var known) &&
                                known.FileLength == info.Length &&
                                known.LastWriteUtc == info.LastWriteTimeUtc)
                            {
                                slots[index] = known;
                            }
                            else
                            {
                                slots[index] = Track.FromMetadata(file, FlacDecoder.ReadMetadata(file, includePicture: false), info);
                            }

                            Interlocked.Increment(ref found);
                        }
                        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or NotSupportedException or IOException or UnauthorizedAccessException)
                        {
                            failures.Enqueue(new TrackProblem(file, Describe(ex)));
                        }

                        var done = Interlocked.Increment(ref examined);
                        if (done % 32 == 0 || done == files.Count)
                        {
                            progress?.Report(new ScanProgress(file, Volatile.Read(ref found), failures.Count, done, false));
                        }
                    });
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    return;
                }

                tracks.AddRange(slots.Where(t => t is not null)!);
                problems.AddRange(failures.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase));            },
            cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancelling before the worker starts is still a cancelled scan, not a failure.
            cancelled = true;
        }

        return new ScanResult(tracks, problems, roots, cancelled);
    }

    private static bool IsExcluded(string file, HashSet<string>? excluded) =>
        excluded is { Count: > 0 } && excluded.Contains(Track.NormalizePath(Path.GetDirectoryName(file) ?? string.Empty));

    private static string Describe(Exception ex) => ex switch
    {
        InvalidDataException => $"Not a readable FLAC file: {ex.Message}",
        EndOfStreamException => "File is truncated or incomplete.",
        NotSupportedException => $"Unsupported FLAC variant: {ex.Message}",
        UnauthorizedAccessException => "Access denied.",
        IOException => $"File could not be read: {ex.Message}",
        _ => ex.Message,
    };
}
