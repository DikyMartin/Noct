using System.Windows.Input;

namespace Noct.App.ViewModels;

public enum AppSection
{
    Home,
    NowPlaying,
    Library,
    Queue,
    Settings,
}

/// <summary>Root view model for the main window. Holds navigation and the shared child models.</summary>
public sealed class MainViewModel : ViewModelBase
{
    private AppSection _section = AppSection.Home;
    private bool _showArtworkPanel = true;

    public MainViewModel(PlaybackViewModel playback, LibraryViewModel library, SettingsViewModel settings, HomeViewModel home, LyricsViewModel lyrics)
    {
        Playback = playback;
        Lyrics = lyrics;
        Library = library;
        Settings = settings;
        Home = home;
        Home.OpenAlbumRequested = album =>
        {
            Section = AppSection.Library;
            Library.OpenAlbumById(album.Id);
        };
        NavigateCommand = new RelayCommand(parameter =>
        {
            if (parameter is AppSection section)
            {
                Section = section;
            }
        });
    }

    public PlaybackViewModel Playback { get; }

    public LibraryViewModel Library { get; }

    public HomeViewModel Home { get; }

    public LyricsViewModel Lyrics { get; }

    public SettingsViewModel Settings { get; }

    public ICommand NavigateCommand { get; }

    public AppSection Section
    {
        get => _section;
        set
        {
            if (Set(ref _section, value))
            {
                Raise(nameof(IsHome));
                Raise(nameof(IsNowPlaying));
                Lyrics.PageActive = value == AppSection.NowPlaying;
                Raise(nameof(IsLibrary));
                Raise(nameof(IsQueue));
                Raise(nameof(IsSettings));
            }
        }
    }

    public bool IsHome => _section == AppSection.Home;

    public bool IsNowPlaying => _section == AppSection.NowPlaying;

    public bool IsLibrary => _section == AppSection.Library;

    public bool IsQueue => _section == AppSection.Queue;

    public bool IsSettings => _section == AppSection.Settings;

    /// <summary>Set by the window from its width; the panel collapses when there is no room.</summary>
    public bool ShowArtworkPanel
    {
        get => _showArtworkPanel;
        set => Set(ref _showArtworkPanel, value);
    }
}
