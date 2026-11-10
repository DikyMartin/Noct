using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Noct.Core.Audio;
using Noct.Core.Library;
using Noct.Core.Playback;

namespace Noct.App.ViewModels;

/// <summary>A label and value pair in the source/output information block.</summary>
public sealed record InfoRow(string Label, string Value);

/// <summary>One entry of the queue list.</summary>
public sealed class QueueItemViewModel : ViewModelBase
{
    private bool _isCurrent;

    public QueueItemViewModel(int index, Track track, bool isCurrent)
    {
        Index = index;
        Track = track;
        _isCurrent = isCurrent;
    }

    public int Index { get; }

    public Track Track { get; }

    public int Number => Index + 1;

    public string Title => Track.Title;

    public string Artist => Track.DisplayArtist;

    public string DurationText => TextFormat.Duration(Track.Duration);

    public bool IsCurrent
    {
        get => _isCurrent;
        set => Set(ref _isCurrent, value);
    }
}

/// <summary>
/// The one object both windows, the tray and the settings page read playback state from.
/// </summary>
/// <remarks>
/// Hiding or showing a window never touches this object, which is why a window transition cannot
/// restart or pause playback. Engine snapshots arrive on a worker thread; they are coalesced so the
/// UI thread applies only the newest one, however fast the engine publishes.
/// </remarks>
public sealed class PlaybackViewModel : ViewModelBase, IDisposable
{
    private readonly PlaybackEngine _engine;
    private readonly ArtworkService _artwork;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _noticeTimer;

    private PlaybackSnapshot? _pending;
    private int _drainScheduled;
    private CancellationTokenSource? _artworkCancellation;

    private PlaybackSnapshot _snapshot = PlaybackSnapshot.Empty;
    private Guid _currentTrackId;
    private OutputNegotiation? _lastOutput;
    private int _queueVersion = -1;
    private int _queueIndex = -1;

    private bool _scrubbing;
    private long _holdPositionUntilTick;
    private double _displayFraction;
    private string _elapsedText = "0:00";
    private string _remainingText = "0:00";
    private double _volume;

    private ImageSource? _artworkImage;
    private Color? _accent;
    private TimeSpan _position;
    private IReadOnlyList<InfoRow> _infoRows = [];
    private IReadOnlyList<QueueItemViewModel> _queueItems = [];
    private string? _noticeMessage;
    private string? _noticeAction;
    private bool _noticeIsError;

    public PlaybackViewModel(PlaybackEngine engine, ArtworkService artwork, Dispatcher dispatcher)
    {
        _engine = engine;
        _volume = Math.Round(Math.Clamp(engine.Volume, 0, 1), 2); // restored from settings, shown before any snapshot
        _artwork = artwork;
        _dispatcher = dispatcher;

        _noticeTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(6) };
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer.Stop();
            ClearNotice();
        };

        PlayPauseCommand = new RelayCommand(PlayPause, () => HasTrack || (CanStartFromLibrary?.Invoke() ?? false));
        NextCommand = new RelayCommand(_engine.Next, () => HasTrack);
        PreviousCommand = new RelayCommand(_engine.Previous, () => HasTrack);
        StopCommand = new RelayCommand(_engine.Stop, () => HasTrack);
        ToggleRepeatCommand = new RelayCommand(CycleRepeat);
        DismissNoticeCommand = new RelayCommand(ClearNotice);
        ClearQueueCommand = new RelayCommand(_engine.ClearQueue, () => _snapshot.QueueCount > 0);
        PlayQueueItemCommand = new RelayCommand(p =>
        {
            if (p is QueueItemViewModel item)
            {
                _engine.PlayQueueIndex(item.Index);
            }
        });
        RemoveQueueItemCommand = new RelayCommand(p =>
        {
            if (p is QueueItemViewModel item)
            {
                _engine.RemoveFromQueue(item.Index);
            }
        });

        _engine.SnapshotChanged += OnSnapshot;
        _engine.NoticeRaised += OnNotice;
    }

    /// <summary>Raised on the UI thread when the playing track changes (Guid.Empty when none).</summary>
    public event EventHandler<Guid>? CurrentTrackChanged;

    /// <summary>Set by the shell so the play button can start the library when the queue is empty.</summary>
    public Func<bool>? CanStartFromLibrary { get; set; }

    public Action? StartFromLibrary { get; set; }

    public ICommand PlayPauseCommand { get; }

    public ICommand NextCommand { get; }

    public ICommand PreviousCommand { get; }

    public ICommand StopCommand { get; }

    public ICommand ToggleRepeatCommand { get; }

    public ICommand DismissNoticeCommand { get; }

    public ICommand ClearQueueCommand { get; }

    public ICommand PlayQueueItemCommand { get; }

    public ICommand RemoveQueueItemCommand { get; }

    /// <summary>
    /// Set by the shell: true while any visible surface shows the elapsed time. When false the
    /// engine stops publishing position-only updates and the UI does no per-tick work at all.
    /// </summary>
    public bool PositionUpdatesEnabled
    {
        get => _engine.PositionTicksEnabled;
        set => _engine.PositionTicksEnabled = value;
    }

    public PlaybackSnapshot Snapshot => _snapshot;

    public Guid CurrentTrackId => _currentTrackId;

    public bool HasTrack => _snapshot.Track is not null;

    public bool IsPlaying => _snapshot.Status == PlaybackStatus.Playing;

    public bool IsOpening => _snapshot.Status == PlaybackStatus.Opening;

    public bool IsFailed => _snapshot.Status == PlaybackStatus.Failed;

    public string Title => _snapshot.Track?.Title ?? "Nothing playing";

    public string Artist => _snapshot.Track?.DisplayArtist ?? string.Empty;

    public string Album => _snapshot.Track?.Album ?? string.Empty;

    public bool HasAlbum => !string.IsNullOrEmpty(_snapshot.Track?.Album);

    /// <summary>What the compact island shows: the title, or a short status when it matters.</summary>
    public string CompactText => _snapshot.Status switch
    {
        PlaybackStatus.Opening => "Opening…",
        PlaybackStatus.Failed => "Playback problem",
        PlaybackStatus.Paused when HasTrack => $"Paused · {Title}",
        _ => HasTrack ? Title : "Nothing playing",
    };

    public string PlayPauseName => IsPlaying ? "Pause" : "Play";

    public ImageSource? Artwork
    {
        get => _artworkImage;
        private set
        {
            if (Set(ref _artworkImage, value))
            {
                Raise(nameof(HasArtwork));
            }
        }
    }

    public bool HasArtwork => _artworkImage is not null;

    /// <summary>A muted tone taken from the artwork, or null when there is none or it is greyscale.</summary>
    public Color? AccentColor
    {
        get => _accent;
        private set => Set(ref _accent, value);
    }

    /// <summary>
    /// Where playback is, as the UI sees it. A committed seek moves it at once, before the engine's
    /// next snapshot arrives, so anything that follows playback (lyrics) reacts immediately.
    /// </summary>
    public TimeSpan Position => _position;

    /// <summary>Raised on the UI thread whenever <see cref="Position"/> changes.</summary>
    public event EventHandler? PositionChanged;

    private void SetPosition(TimeSpan position)
    {
        _position = position;
        PositionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Seeks to an absolute time, as when a lyric line is clicked.</summary>
    public void SeekTo(TimeSpan target)
    {
        if (_snapshot.Duration > TimeSpan.Zero)
        {
            SeekToFraction(target.TotalSeconds / _snapshot.Duration.TotalSeconds);
        }
    }

    public double ProgressFraction
    {
        get => _displayFraction;
        private set => Set(ref _displayFraction, value);
    }

    public string ElapsedText
    {
        get => _elapsedText;
        private set => Set(ref _elapsedText, value);
    }

    public string RemainingText
    {
        get => _remainingText;
        private set => Set(ref _remainingText, value);
    }

    public bool CanSeek => HasTrack && _snapshot.Duration > TimeSpan.Zero;

    public RepeatMode Repeat => _snapshot.Repeat;

    public string RepeatName => _snapshot.Repeat switch
    {
        RepeatMode.All => "Repeat all",
        RepeatMode.One => "Repeat one",
        _ => "Repeat off",
    };

    public bool RepeatActive => _snapshot.Repeat != RepeatMode.Off;

    public bool RepeatIsOne => _snapshot.Repeat == RepeatMode.One;

    /// <summary>
    /// The requested volume, 0..1 in whole percents. The slider, the percentage label and the engine all
    /// follow this one value; nothing reads it back from a playback snapshot, so a snapshot that was
    /// built before the user's last move can never pull the slider back.
    /// </summary>
    public double Volume
    {
        get => _volume;
        set
        {
            if (double.IsNaN(value))
            {
                return;
            }

            value = Math.Round(Math.Clamp(value, 0, 1), 2);
            if (value == _volume)
            {
                return;
            }

            _volume = value;
            Raise();
            Raise(nameof(VolumeLabel));
            _engine.SetVolume(value);
        }
    }
    /// <summary>False in transparent mode: the DAC owns the level, not the software.</summary>
    public bool VolumeEnabled => _snapshot.VolumeIsSoftware;

    public string VolumeHint => _snapshot.VolumeIsSoftware
        ? "Software volume"
        : "Volume is controlled by your DAC. Transparent mode keeps software gain at unity.";

    public string VolumeLabel => _snapshot.VolumeIsSoftware ? $"{(int)Math.Round(_volume * 100)}%" : "DAC";

    public IReadOnlyList<InfoRow> InfoRows
    {
        get => _infoRows;
        private set => Set(ref _infoRows, value);
    }

    public IReadOnlyList<QueueItemViewModel> QueueItems
    {
        get => _queueItems;
        private set
        {
            if (Set(ref _queueItems, value))
            {
                Raise(nameof(QueueIsEmpty));
            }
        }
    }

    public bool QueueIsEmpty => _queueItems.Count == 0;

    public string QueueSummary => _snapshot.QueueCount == 0
        ? "Queue is empty"
        : _snapshot.QueueCount == 1 ? "1 track" : $"{_snapshot.QueueCount} tracks";

    public string? NoticeMessage
    {
        get => _noticeMessage;
        private set
        {
            if (Set(ref _noticeMessage, value))
            {
                Raise(nameof(HasNotice));
            }
        }
    }

    public string? NoticeAction
    {
        get => _noticeAction;
        private set
        {
            if (Set(ref _noticeAction, value))
            {
                Raise(nameof(HasNoticeAction));
            }
        }
    }

    public bool HasNotice => !string.IsNullOrEmpty(_noticeMessage);

    public bool HasNoticeAction => !string.IsNullOrEmpty(_noticeAction);

    public bool NoticeIsError
    {
        get => _noticeIsError;
        private set => Set(ref _noticeIsError, value);
    }

    /// <summary>The tooltip for the tray icon and the island's accessible description.</summary>
    public string NowPlayingDescription => HasTrack
        ? $"{(IsPlaying ? "Playing" : "Paused")}: {Title}{(string.IsNullOrEmpty(Artist) ? string.Empty : " by " + Artist)}"
        : "Nothing playing";

    // ----- Seeking -----

    public bool IsScrubbing => _scrubbing;

    /// <summary>Called when the user presses the seek bar; the display then follows the pointer.</summary>
    public void BeginScrub() => _scrubbing = true;

    /// <summary>Called while dragging so the time labels follow the thumb without seeking yet.</summary>
    public void Scrub(double fraction)
    {
        if (!_scrubbing)
        {
            return;
        }

        fraction = Math.Clamp(fraction, 0, 1);
        ProgressFraction = fraction;
        var target = TimeSpan.FromSeconds(_snapshot.Duration.TotalSeconds * fraction);
        ElapsedText = TextFormat.Duration(target);
        RemainingText = "-" + TextFormat.Duration(_snapshot.Duration - target);
    }

    /// <summary>Commits the seek. The display holds the target briefly so it does not snap back.</summary>
    public void EndScrub(double fraction) => SeekToFraction(fraction, endScrub: true);

    /// <summary>A single seek, from the keyboard or a click, without a drag.</summary>
    public void SeekToFraction(double fraction, bool endScrub = false)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        if (_snapshot.Duration <= TimeSpan.Zero)
        {
            _scrubbing = false;
            return;
        }

        var target = TimeSpan.FromSeconds(_snapshot.Duration.TotalSeconds * fraction);
        _engine.Seek(target);
        SetPosition(target);

        _scrubbing = false;
        _holdPositionUntilTick = Environment.TickCount64 + 600;
        ProgressFraction = fraction;
        ElapsedText = TextFormat.Duration(target);
        RemainingText = "-" + TextFormat.Duration(_snapshot.Duration - target);
        _ = endScrub;
    }

    public void SeekRelative(TimeSpan delta)
    {
        if (_snapshot.Duration <= TimeSpan.Zero)
        {
            return;
        }

        var target = _snapshot.Position + delta;
        SeekToFraction(target.TotalSeconds / _snapshot.Duration.TotalSeconds);
    }

    public void Dispose()
    {
        _engine.SnapshotChanged -= OnSnapshot;
        _engine.NoticeRaised -= OnNotice;
        _noticeTimer.Stop();
        _artworkCancellation?.Cancel();
        _artworkCancellation?.Dispose();
    }

    // ----- Engine events -----

    private void OnSnapshot(object? sender, PlaybackSnapshot snapshot)
    {
        Interlocked.Exchange(ref _pending, snapshot);
        if (Interlocked.Exchange(ref _drainScheduled, 1) == 0)
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Background, DrainPending);
        }
    }

    private void OnNotice(object? sender, PlaybackNotice notice) =>
        _dispatcher.BeginInvoke(DispatcherPriority.Background, () => ShowNotice(notice));

    private void DrainPending()
    {
        Interlocked.Exchange(ref _drainScheduled, 0);
        var snapshot = Interlocked.Exchange(ref _pending, null);
        if (snapshot is not null)
        {
            Apply(snapshot);
        }
    }

    private void Apply(PlaybackSnapshot snapshot)
    {
        var previous = _snapshot;
        _snapshot = snapshot;

        var trackId = snapshot.Track?.Id ?? Guid.Empty;
        var trackChanged = trackId != _currentTrackId;

        if (trackChanged)
        {
            _currentTrackId = trackId;
            LoadArtwork(snapshot.Track);
            if (snapshot.Status == PlaybackStatus.Playing)
            {
                ClearNotice();
            }
        }

        if (previous.Status != snapshot.Status || trackChanged)
        {
            RaiseTransportState();
        }

        if (previous.Duration != snapshot.Duration)
        {
            Raise(nameof(CanSeek));
        }

        if (!_scrubbing && Environment.TickCount64 >= _holdPositionUntilTick)
        {
            ProgressFraction = snapshot.ProgressFraction;
            ElapsedText = TextFormat.Duration(snapshot.Position);
            RemainingText = "-" + TextFormat.Duration(snapshot.Remaining);
            SetPosition(snapshot.Position);
        }
        else if (trackChanged)
        {
            // A new track starts from its own position, whatever seek was held for the last one.
            SetPosition(snapshot.Position);
        }

        if (previous.Repeat != snapshot.Repeat)
        {
            Raise(nameof(Repeat));
            Raise(nameof(RepeatName));
            Raise(nameof(RepeatActive));
            Raise(nameof(RepeatIsOne));
        }

        if (previous.VolumeIsSoftware != snapshot.VolumeIsSoftware)
        {
            Raise(nameof(VolumeEnabled));
            Raise(nameof(VolumeHint));
            Raise(nameof(VolumeLabel));
        }

        if (!ReferenceEquals(snapshot.Output, _lastOutput))
        {
            _lastOutput = snapshot.Output;
            InfoRows = BuildInfoRows(snapshot);
        }
        else if (trackChanged)
        {
            InfoRows = BuildInfoRows(snapshot);
        }

        if (snapshot.QueueVersion != _queueVersion || snapshot.QueueIndex != _queueIndex)
        {
            RefreshQueue(snapshot);
        }

        if (trackChanged)
        {
            CurrentTrackChanged?.Invoke(this, trackId);
        }
    }

    private void RaiseTransportState()
    {
        Raise(nameof(HasTrack));
        Raise(nameof(IsPlaying));
        Raise(nameof(IsOpening));
        Raise(nameof(IsFailed));
        Raise(nameof(Title));
        Raise(nameof(Artist));
        Raise(nameof(Album));
        Raise(nameof(HasAlbum));
        Raise(nameof(CompactText));
        Raise(nameof(PlayPauseName));
        Raise(nameof(CanSeek));
        Raise(nameof(NowPlayingDescription));
        (PlayPauseCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (NextCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (PreviousCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (StopCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void RefreshQueue(PlaybackSnapshot snapshot)
    {
        var rebuild = snapshot.QueueVersion != _queueVersion;
        _queueVersion = snapshot.QueueVersion;
        _queueIndex = snapshot.QueueIndex;

        if (rebuild)
        {
            var items = _engine.Queue.Items;
            QueueItems = items
                .Select((track, index) => new QueueItemViewModel(index, track, index == snapshot.QueueIndex))
                .ToArray();
        }
        else
        {
            foreach (var item in _queueItems)
            {
                item.IsCurrent = item.Index == snapshot.QueueIndex;
            }
        }

        Raise(nameof(QueueSummary));
        (ClearQueueCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private async void LoadArtwork(Track? track)
    {
        _artworkCancellation?.Cancel();
        _artworkCancellation?.Dispose();
        _artworkCancellation = null;

        if (track is null)
        {
            Artwork = null;
            AccentColor = null;
            return;
        }

        var cancellation = new CancellationTokenSource();
        _artworkCancellation = cancellation;

        try
        {
            // The side panel is the largest place this image is drawn (240 DIP); the island and the
            // compact row scale the same bitmap down, so one thumbnail serves all of them.
            var image = await _artwork.GetTrackAsync(track, _artwork.PixelsFor(240), cancellation.Token);
            if (!cancellation.IsCancellationRequested)
            {
                Artwork = image;
                AccentColor = image is null ? null : ArtworkAccent.From(image);
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer track.
        }
    }

    // ----- Commands -----

    private void PlayPause()
    {
        if (!HasTrack)
        {
            StartFromLibrary?.Invoke();
            return;
        }

        _engine.TogglePause();
    }

    private void CycleRepeat()
    {
        var next = _snapshot.Repeat switch
        {
            RepeatMode.Off => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            _ => RepeatMode.Off,
        };

        _engine.SetRepeat(next);
    }

    // ----- Notices -----

    private void ShowNotice(PlaybackNotice notice)
    {
        NoticeMessage = notice.Message;
        NoticeAction = notice.Action;
        NoticeIsError = notice.Severity == NoticeSeverity.Error;

        _noticeTimer.Stop();
        if (notice.Severity != NoticeSeverity.Error)
        {
            _noticeTimer.Start();
        }
    }

    private void ClearNotice()
    {
        _noticeTimer.Stop();
        NoticeMessage = null;
        NoticeAction = null;
        NoticeIsError = false;
    }

    // ----- Source and output information -----

    private IReadOnlyList<InfoRow> BuildInfoRows(PlaybackSnapshot snapshot)
    {
        var rows = new List<InfoRow>();
        var track = snapshot.Track;
        var output = snapshot.Output;

        if (track is not null)
        {
            rows.Add(new InfoRow("Source", track.SourceFormat.Describe()));
        }

        if (output is null)
        {
            rows.Add(new InfoRow("Output", "Not opened yet"));
            rows.Add(new InfoRow("Format", "Unknown until playback starts"));
            return rows;
        }

        var backend = output.ActualBackend ?? output.RequestedBackend;
        var mode = output.ActualMode ?? output.RequestedMode;

        rows.Add(new InfoRow("Device", output.DeviceName ?? "Unknown"));

        if (output.Status != NegotiationStatus.Succeeded)
        {
            rows.Add(new InfoRow("Requested", $"{TextFormat.BackendName(output.RequestedBackend)} · {TextFormat.ModeName(output.RequestedMode)}"));
            rows.Add(new InfoRow("Format", "Not accepted by the device"));
            return rows;
        }

        rows.Add(new InfoRow("Mode", $"{TextFormat.BackendName(backend)} · {TextFormat.ModeName(mode)}"));
        rows.Add(new InfoRow("Output", output.AcceptedFormat?.Describe() ?? "Unknown"));

        if (output.Packing is not null)
        {
            rows.Add(new InfoRow("Packing", output.Packing));
        }

        rows.Add(new InfoRow(
            "Conversions",
            output.Conversions.Count == 0 ? "None reported" : string.Join("; ", output.Conversions)));

        rows.Add(new InfoRow("Transparency", output.Transparency switch
        {
            TransparencyStatus.ConfiguredNotHardwareVerified => "Configured. Not hardware-verified.",
            TransparencyStatus.Unavailable => "Unavailable on this path",
            _ => "Not applicable in Normal mode",
        }));

        return rows;
    }
}
