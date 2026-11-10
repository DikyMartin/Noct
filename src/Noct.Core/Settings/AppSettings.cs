using System.Text.Json.Serialization;
using Noct.Core.Audio;

namespace Noct.Core.Settings;

/// <summary>Persisted user settings. Schema version 1.</summary>
public sealed class AppSettings : IVersionedDocument
{
    /// <summary>
    /// 1: a flat list of source folders. 2: adds <see cref="RemovedAlbumFolders"/>. A version 1 file
    /// loads unchanged, keeps every folder and is rewritten as version 2 on the next save.
    /// 3: a build stored per-song lyrics file choices; that feature was removed, but version 3 files
    /// exist, so the number stays. Any leftover "lyricsFiles" entry is ignored. Older files load
    /// unchanged and are upgraded on save.
    /// </summary>
    public const int CurrentSchemaVersion = 3;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonIgnore]
    public string? LoadIssue { get; set; }

    /// <summary>
    /// Source folders the user added, one entry per Add folder. Each is scanned recursively; every
    /// folder inside that directly holds FLAC files becomes an album. Never written to.
    /// </summary>
    public List<string> LibraryFolders { get; set; } = [];

    /// <summary>
    /// Album folders the user removed from the library. They stay out when a source folder is
    /// rescanned. Only Noct's record is affected; the files remain on disk.
    /// </summary>
    public List<string> RemovedAlbumFolders { get; set; } = [];

    public OutputBackend OutputBackend { get; set; } = OutputBackend.WasapiShared;

    public OutputMode OutputMode { get; set; } = OutputMode.Normal;

    /// <summary>Device key in <c>Backend|Id</c> form, or null for the system default.</summary>
    public string? OutputDeviceKey { get; set; }

    public int OutputLatencyMilliseconds { get; set; } = 200;

    /// <summary>Software volume, 0..1. Forced to unity whenever transparent mode is active.</summary>
    public double Volume { get; set; } = 0.7;

    public bool IslandEnabled { get; set; } = true;

    public bool GaplessEnabled { get; set; } = true;

    public string? LastTrackPath { get; set; }

    public long LastTrackPositionSamples { get; set; }

    public List<string> QueuePaths { get; set; } = [];

    public double MainWindowWidth { get; set; } = 1120;

    public double MainWindowHeight { get; set; } = 720;

    public static AppSettings CreateDefault() => new();

    public AppSettings Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        LibraryFolders = [.. LibraryFolders],
        RemovedAlbumFolders = [.. RemovedAlbumFolders],
        OutputBackend = OutputBackend,
        OutputMode = OutputMode,
        OutputDeviceKey = OutputDeviceKey,
        OutputLatencyMilliseconds = OutputLatencyMilliseconds,
        Volume = Volume,
        IslandEnabled = IslandEnabled,
        GaplessEnabled = GaplessEnabled,
        LastTrackPath = LastTrackPath,
        LastTrackPositionSamples = LastTrackPositionSamples,
        QueuePaths = [.. QueuePaths],
        MainWindowWidth = MainWindowWidth,
        MainWindowHeight = MainWindowHeight,
    };
}

/// <summary>Loads and saves <see cref="AppSettings"/>, coalescing rapid writes.</summary>
public sealed class SettingsStore : IDisposable
{
    private readonly string _path;
    private readonly object _gate = new();
    private readonly Timer _flushTimer;
    private AppSettings _settings;
    private bool _dirty;
    private bool _flushScheduled;
    private bool _disposed;

    public SettingsStore(string? path = null)
    {
        _path = path ?? AppPaths.SettingsFile;
        _settings = JsonStore.Load(_path, AppSettings.CurrentSchemaVersion, AppSettings.CreateDefault);
        if (_settings.SchemaVersion < AppSettings.CurrentSchemaVersion && File.Exists(_path))
        {
            // Migration from an older file: nothing is lost, the version is just brought up to date.
            _settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
            _dirty = true;
        }
        _flushTimer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public AppSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _settings;
            }
        }
    }

    public string? LoadIssue => _settings.LoadIssue;

    /// <summary>Applies a change and schedules a debounced save.</summary>
    public void Update(Action<AppSettings> change)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            change(_settings);
            _dirty = true;

            // Arm once per burst. Re-arming on every update would push the flush back forever while
            // something (such as the playback position) keeps writing, so nothing would be saved.
            if (!_flushScheduled)
            {
                _flushScheduled = true;
                _flushTimer.Change(TimeSpan.FromMilliseconds(750), Timeout.InfiniteTimeSpan);
            }
        }
    }

    public void Flush()
    {
        AppSettings snapshot;
        lock (_gate)
        {
            _flushScheduled = false;
            if (!_dirty)
            {
                return;
            }

            snapshot = _settings.Clone();
            _dirty = false;
        }

        try
        {
            JsonStore.Save(_path, snapshot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lock (_gate)
            {
                _dirty = true;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _flushTimer.Dispose();
        Flush();
    }
}
