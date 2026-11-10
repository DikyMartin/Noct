using System.Windows.Input;
using Noct.Core.Lyrics;

namespace Noct.App.ViewModels;

/// <summary>One lyric line as the page shows it.</summary>
public sealed class LyricLineViewModel : ViewModelBase
{
    private bool _isActive;

    public LyricLineViewModel(int index, LyricLine line)
    {
        Index = index;
        Text = line.Text;
        Time = line.Time;
        Group = line.Group;
    }

    public int Index { get; }

    public int Group { get; }

    public string Text { get; }

    public TimeSpan? Time { get; }

    /// <summary>Only lines with a timestamp can be clicked to seek.</summary>
    public bool CanSeek => Time is not null;

    public bool IsActive
    {
        get => _isActive;
        set => Set(ref _isActive, value);
    }
}

public enum LyricsState
{
    /// <summary>Nothing playing, or the page has not been opened yet.</summary>
    Idle,
    Loading,
    Ready,
    Missing,
    Problem,
}

/// <summary>
/// Lyrics for the playing song. Loads the song's .lrc, follows the playback position and tells the
/// page which line to bring into view. It only does work while the Now Playing page is showing.
/// </summary>
public sealed class LyricsViewModel : ViewModelBase, IDisposable
{
    private readonly PlaybackViewModel _playback;
    private IReadOnlyList<LyricLineViewModel> _lines = [];
    private LyricsDocument? _document;
    private LyricsState _state = LyricsState.Idle;
    private string _problem = string.Empty;
    private bool _pageActive;
    private bool _following = true;
    private Guid _loadedFor = Guid.Empty;
    private int _activeGroup = -1;
    private int _loadVersion;
    private CancellationTokenSource? _loadCancellation;

    public LyricsViewModel(PlaybackViewModel playback)
    {
        _playback = playback;

        ResumeFollowCommand = new RelayCommand(ResumeFollow);
        SeekToLineCommand = new RelayCommand(parameter =>
        {
            if (parameter is LyricLineViewModel { Time: { } time })
            {
                // Clicking a line is the user taking the lead, so following resumes from there.
                _following = true;
                RaiseFollowState();
                _playback.SeekTo(time);
            }
        });

        _playback.CurrentTrackChanged += OnTrackChanged;
        _playback.PositionChanged += OnPositionChanged;
    }

    /// <summary>Raised when the page should bring the active line into view.</summary>
    public event EventHandler? ActiveLineChanged;

    public ICommand ResumeFollowCommand { get; }

    public ICommand SeekToLineCommand { get; }

    public IReadOnlyList<LyricLineViewModel> Lines
    {
        get => _lines;
        private set => Set(ref _lines, value);
    }

    public LyricsState State
    {
        get => _state;
        private set
        {
            if (Set(ref _state, value))
            {
                Raise(nameof(HasLyrics));
                Raise(nameof(ShowEmptyState));
                Raise(nameof(EmptyTitle));
                Raise(nameof(EmptyHint));
            }
        }
    }

    public bool HasLyrics => _state == LyricsState.Ready;

    public bool IsSynced => _document?.IsSynced == true;

    /// <summary>Index of the first line of the active group, or -1. Used for scrolling and by tests.</summary>
    public int ActiveIndex => _activeGroup;

    public bool ShowEmptyState => _state is LyricsState.Missing or LyricsState.Problem;

    public string EmptyTitle => _state == LyricsState.Problem ? "Lyrics could not be read" : "Lyrics unavailable";

    public string EmptyHint => _state == LyricsState.Problem
        ? _problem
        : "Add a .lrc file with the same name as the song to its folder.";

    public bool IsFollowing => _following;

    /// <summary>The "Follow song" button shows once the user has scrolled away from the active line.</summary>
    public bool ShowFollowButton => IsSynced && !_following;

    /// <summary>True when lyrics are shown but carry no timestamps, so they cannot follow the song.</summary>
    public bool IsUnsyncedNotice => _state == LyricsState.Ready && !IsSynced;

    /// <summary>The Now Playing page sets this when it becomes visible or hidden.</summary>
    public bool PageActive
    {
        get => _pageActive;
        set
        {
            if (_pageActive == value)
            {
                return;
            }

            _pageActive = value;
            if (value)
            {
                _following = true;
                RaiseFollowState();
                EnsureLoaded();
                UpdateActive(force: true);
            }
        }
    }

    /// <summary>The user scrolled by hand: stop pulling the view back to the active line.</summary>
    public void UserScrolled()
    {
        if (_following && IsSynced)
        {
            _following = false;
            RaiseFollowState();
        }
    }

    private void ResumeFollow()
    {
        _following = true;
        RaiseFollowState();
        ActiveLineChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseFollowState()
    {
        Raise(nameof(IsFollowing));
        Raise(nameof(ShowFollowButton));
    }

    // ----- Loading -----

    private void OnTrackChanged(object? sender, Guid trackId)
    {
        // The previous song's lyrics go immediately, even if the new ones are not read yet.
        _loadCancellation?.Cancel();
        _loadVersion++;
        _loadedFor = Guid.Empty;
        SetDocument(null, LyricsState.Idle);

        if (_pageActive)
        {
            _following = true;
            RaiseFollowState();
            EnsureLoaded();
        }
    }

    private async void EnsureLoaded()
    {
        var track = _playback.Snapshot.Track;
        if (track is null)
        {
            SetDocument(null, LyricsState.Idle);
            return;
        }

        if (_loadedFor == track.Id)
        {
            return;
        }

        _loadedFor = track.Id;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        var cancellation = _loadCancellation = new CancellationTokenSource();
        var version = ++_loadVersion;
        State = LyricsState.Loading;

        LyricsResult result;
        try
        {
            var path = LyricsLocator.Find(track.Path);

            result = path is null
                ? LyricsResult.None
                : await Task.Run(() => LyricsLocator.LoadAsync(path, cancellation.Token), cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (version != _loadVersion)
        {
            return; // a newer track or pick replaced this load
        }

        if (result.Found)
        {
            SetDocument(result.Document, LyricsState.Ready);
        }
        else
        {
            _problem = result.Problem ?? string.Empty;
            SetDocument(null, result.Problem is null ? LyricsState.Missing : LyricsState.Problem);
        }
    }

    private void SetDocument(LyricsDocument? document, LyricsState state)
    {
        _document = document;
        _activeGroup = -1;
        Lines = document is null ? [] : document.Lines.Select((line, i) => new LyricLineViewModel(i, line)).ToArray();
        State = state;
        Raise(nameof(IsSynced));
        Raise(nameof(ActiveIndex));
        Raise(nameof(IsUnsyncedNotice));
        RaiseFollowState();
        UpdateActive(force: true);
    }

    // ----- Following playback -----

    private void OnPositionChanged(object? sender, EventArgs e) => UpdateActive(force: false);

    private void UpdateActive(bool force)
    {
        if (!_pageActive && !force)
        {
            return;
        }

        var group = _document?.FindActiveGroup(_playback.Position) ?? -1;
        if (group == _activeGroup && !force)
        {
            return;
        }

        var previous = _activeGroup;
        _activeGroup = group;

        if (group != previous || force)
        {
            foreach (var line in _lines)
            {
                var active = group >= 0 && line.Group == group;
                if (line.IsActive != active)
                {
                    line.IsActive = active;
                }
            }

            Raise(nameof(ActiveIndex));
            if (_following)
            {
                ActiveLineChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public void Dispose()
    {
        _playback.CurrentTrackChanged -= OnTrackChanged;
        _playback.PositionChanged -= OnPositionChanged;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
    }
}
