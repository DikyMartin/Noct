using System.Windows;
using System.Windows.Threading;
using Noct.App.Platform;
using Noct.App.Themes;
using Noct.App.ViewModels;
using Noct.App.Views;
using Noct.Core.Island;
using Noct.Core.Library;
using Noct.Core.Playback;
using Noct.Core.Settings;

namespace Noct.App;

public partial class App : Application
{
    private SingleInstance? _single;
    private SystemPreferences? _preferences;
    private SettingsStore? _settings;
    private PlaybackEngine? _engine;
    private PlaybackViewModel? _playback;
    private LibraryViewModel? _library;
    private SettingsViewModel? _settingsModel;
    private ShellCoordinator? _shell;
    private bool _exited;
    private string[] _startupFolders = [];

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Folders given on the command line (or dropped on Noct.exe) are added as sources, one at a
        // time, exactly as the Add folder button adds them.
        _startupFolders = e.Args.Where(Directory.Exists).ToArray();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Write("Unhandled exception", args.ExceptionObject as Exception);

        _single = SingleInstance.Acquire();
        if (!_single.IsFirstInstance)
        {
            // Another Noct already owns the audio device and the library: ask it to come forward.
            _single.SignalFirstInstance();
            _single.Dispose();
            _single = null;
            Shutdown();
            return;
        }

        try
        {
            Start();
        }
        catch (Exception ex)
        {
            AppLog.Write("Startup failed", ex);
            MessageBox.Show(
                $"Noct could not start.\n\n{ex.Message}\n\nDetails were written to:\n{AppPaths.LogFile}",
                "Noct",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            ExitApplication();
        }
    }

    private void Start()
    {
        AppPaths.EnsureCreated();
        AppLog.Trim();

        _preferences = new SystemPreferences();
        ThemeManager.Load(this, _preferences.HighContrast);
        ThemeManager.SetGlass(this, _preferences.BackdropAllowed);

        _settings = new SettingsStore();
        var index = new LibraryIndex();
        var artwork = new ArtworkService();
        _ = Task.Run(artwork.Prune);

        _engine = new PlaybackEngine(_settings);
        _playback = new PlaybackViewModel(_engine, artwork, Dispatcher);
        artwork.DisplayScale = MonitorService.Primary().Scale;
        _library = new LibraryViewModel(index, _settings, _engine, _playback, artwork, Dispatcher);
        _settingsModel = new SettingsViewModel(_engine, _playback, _settings, _library, Dispatcher);
        var home = new HomeViewModel(_library, _engine);
        var lyrics = new LyricsViewModel(_playback);
        var main = new MainViewModel(_playback, _library, _settingsModel, home, lyrics);

        var mainWindow = new MainWindow(main, _settings, _preferences);
        var tray = new TrayIcon("Noct");
        var controller = new IslandStateController();

        // The shell is assigned before the tray menu is built so Open and Exit can reach it.
        TrayMenu? trayMenu = null;
        trayMenu = new TrayMenu(_playback, () => _shell?.OpenMain(), RequestExit);

        _shell = new ShellCoordinator(
            Dispatcher,
            mainWindow,
            () => new IslandWindow(_playback, _preferences),
            tray,
            trayMenu,
            controller,
            _playback,
            _settings);

        _shell.ExitRequested += (_, _) => RequestExit();
        _single!.ActivationRequested += (_, _) => Dispatcher.BeginInvoke(() => _shell?.OpenMain());
        _single.StartListening();

        _shell.Start();

        // The window is already on screen; the saved library loads behind it and fills in.
        _ = LoadLibraryAsync(home);
    }

    private async Task LoadLibraryAsync(HomeViewModel home)
    {
        try
        {
            await _library!.InitializeAsync();
            RestoreQueue();
            home.EnsurePicked();

            foreach (var folder in _startupFolders)
            {
                _library.AddFolders([folder]);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("Loading the library failed", ex);
        }
    }

    /// <summary>Puts the saved queue back without playing, so Play resumes where the user left off.</summary>
    private void RestoreQueue()
    {
        var saved = _settings!.Current;
        var tracks = _library!.ResolvePaths(saved.QueuePaths);
        if (tracks.Count == 0)
        {
            return;
        }

        var index = tracks.ToList().FindIndex(t => string.Equals(t.Path, saved.LastTrackPath, StringComparison.OrdinalIgnoreCase));
        var resume = TimeSpan.Zero;
        if (index >= 0 && tracks[index].SampleRate > 0)
        {
            resume = TimeSpan.FromSeconds(saved.LastTrackPositionSamples / (double)tracks[index].SampleRate);
        }

        _engine!.LoadQueue(tracks, Math.Max(0, index), resume);
    }

    private void RequestExit() => Dispatcher.BeginInvoke(ExitApplication);

    /// <summary>
    /// Leaves for good: stops playback and releases the audio device first, then removes every
    /// window and the tray icon, then saves state. Tray Exit and the close button both end here.
    /// </summary>
    private void ExitApplication()
    {
        if (_exited)
        {
            return;
        }

        _exited = true;

        try
        {
            _engine?.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Write("Engine shutdown failed", ex);
        }

        _shell?.BeginExit();
        _shell?.Dispose();
        _settingsModel?.Dispose();
        _library?.Dispose();
        _playback?.Dispose();
        _settings?.Dispose();
        _preferences?.Dispose();
        _single?.Dispose();

        Shutdown();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the UI usable: record the error and carry on rather than closing a playing app.
        AppLog.Write("Unhandled UI exception", e.Exception);
        e.Handled = true;
    }
}
