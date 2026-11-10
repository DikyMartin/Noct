using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows.Input;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using Noct.Core.Audio;
using Noct.Core.Playback;
using Noct.Core.Settings;

namespace Noct.App.ViewModels;

public sealed record DeviceOption(string Key, string Name, OutputBackend Backend, bool IsDefault)
{
    public string Display => IsDefault ? $"{Name}  ·  system default" : Name;
}

public sealed record FormatSupportRow(string Format, string Result, bool Accepted);

/// <summary>
/// Output, playback and library settings, plus the diagnostics that make output claims checkable.
/// </summary>
/// <remarks>
/// Advanced audio controls live here and nowhere else, so the player surface stays about music.
/// Choosing an output never changes silently: a refusal from the device is shown as-is.
/// </remarks>
public sealed class SettingsViewModel : ViewModelBase, IDisposable
{
    private readonly PlaybackEngine _engine;
    private readonly PlaybackViewModel _playback;
    private readonly SettingsStore _settings;
    private readonly LibraryViewModel _library;
    private readonly Dispatcher _dispatcher;

    private OutputBackend _backend;
    private OutputMode _mode;
    private DeviceOption? _selectedDevice;
    private bool _refreshing;
    private bool _probing;
    private IReadOnlyList<FormatSupportRow> _formatRows = [];
    private string _formatStatus = "Not checked yet.";
    private IReadOnlyList<string> _attempts = [];
    private bool _asioAvailable;

    public SettingsViewModel(
        PlaybackEngine engine,
        PlaybackViewModel playback,
        SettingsStore settings,
        LibraryViewModel library,
        Dispatcher dispatcher)
    {
        _engine = engine;
        _playback = playback;
        _settings = settings;
        _library = library;
        _dispatcher = dispatcher;

        var current = settings.Current;
        _backend = current.OutputBackend;
        _mode = current.OutputMode;

        Devices = [];
        RefreshDevices();

        _engine.OutputDevicesChanged += OnDevicesChanged;
        _library.FoldersChanged += OnFoldersChanged;
        _playback.PropertyChanged += OnPlaybackChanged;

        CheckFormatsCommand = new RelayCommand(() => _ = CheckFormatsAsync(), () => !_probing && SelectedDevice is not null);
        RemoveFolderCommand = new RelayCommand(p =>
        {
            if (p is string folder)
            {
                _library.RemoveFolder(folder);
            }
        });
        RefreshDevicesCommand = new RelayCommand(RefreshDevices);
    }

    public ICommand CheckFormatsCommand { get; }

    public ICommand RemoveFolderCommand { get; }

    public ICommand RefreshDevicesCommand { get; }

    public ICommand AddFolderCommand => _library.AddFolderCommand;

    public ObservableCollection<DeviceOption> Devices { get; }

    public LibraryViewModel Library => _library;

    public OutputBackend Backend
    {
        get => _backend;
        set
        {
            if (Set(ref _backend, value))
            {
                // A mode that the new backend cannot honour is dropped, not silently kept.
                if (!TransparentAllowed && _mode == OutputMode.Transparent)
                {
                    _mode = OutputMode.Normal;
                    Raise(nameof(Mode));
                }

                Raise(nameof(TransparentAllowed));
                Raise(nameof(ModeExplanation));
                Raise(nameof(BackendExplanation));
                RefreshDevices();
                ApplyOutput();
            }
        }
    }

    public OutputMode Mode
    {
        get => _mode;
        set
        {
            if (value == OutputMode.Transparent && !TransparentAllowed)
            {
                Raise();
                return;
            }

            if (Set(ref _mode, value))
            {
                Raise(nameof(ModeExplanation));
                ApplyOutput();
            }
        }
    }

    /// <summary>Only WASAPI Exclusive offers a transparent path in this build.</summary>
    public bool TransparentAllowed => _backend == OutputBackend.WasapiExclusive;

    public bool AsioAvailable
    {
        get => _asioAvailable;
        private set
        {
            if (Set(ref _asioAvailable, value))
            {
                Raise(nameof(AsioStatus));
            }
        }
    }

    public string AsioStatus => _asioAvailable
        ? "An ASIO driver is installed. This path is implemented but untested on hardware."
        : "No compatible ASIO driver is installed. WASAPI stays available.";

    public string BackendExplanation => _backend switch
    {
        OutputBackend.WasapiShared => "Plays through the Windows mixer. Other apps keep their sound, and Windows may resample to its mix rate.",
        OutputBackend.WasapiExclusive => "Takes the device for Noct alone and plays at the track's own sample rate. Other apps are silent while it plays.",
        _ => "Uses a third-party ASIO driver. Implemented but untested on hardware in this build.",
    };

    public string ModeExplanation => _mode == OutputMode.Transparent
        ? "Transparent keeps software gain at unity and applies no effects, resampling or normalization. Volume is set on the DAC. This is a configured path, not a hardware-verified bit-perfect one."
        : TransparentAllowed
            ? "Normal allows software volume."
            : "Normal allows software volume. Transparent needs WASAPI Exclusive.";

    public DeviceOption? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (_refreshing || value is null)
            {
                Raise();
                return;
            }

            if (Set(ref _selectedDevice, value))
            {
                (CheckFormatsCommand as RelayCommand)?.RaiseCanExecuteChanged();
                ApplyOutput();
            }
        }
    }

    public bool GaplessEnabled
    {
        get => _settings.Current.GaplessEnabled;
        set
        {
            if (_settings.Current.GaplessEnabled != value)
            {
                _settings.Update(s => s.GaplessEnabled = value);
                Raise();
            }
        }
    }

    public bool IslandEnabled
    {
        get => _settings.Current.IslandEnabled;
        set
        {
            if (_settings.Current.IslandEnabled != value)
            {
                _settings.Update(s => s.IslandEnabled = value);
                Raise();
            }
        }
    }

    public IReadOnlyList<string> Folders => _library.Folders;

    public bool HasFolders => _library.Folders.Count > 0;

    public IReadOnlyList<FormatSupportRow> FormatRows
    {
        get => _formatRows;
        private set => Set(ref _formatRows, value);
    }

    public string FormatStatus
    {
        get => _formatStatus;
        private set => Set(ref _formatStatus, value);
    }

    public bool IsProbing
    {
        get => _probing;
        private set
        {
            if (Set(ref _probing, value))
            {
                (CheckFormatsCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>What the last negotiation tried, line by line.</summary>
    public IReadOnlyList<string> NegotiationAttempts
    {
        get => _attempts;
        private set
        {
            if (Set(ref _attempts, value))
            {
                Raise(nameof(HasAttempts));
            }
        }
    }

    public bool HasAttempts => _attempts.Count > 0;

    public string VersionText
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version is null ? "Noct" : $"Noct {version.Major}.{version.Minor}.{version.Build}";
        }
    }

    public string DataFolder => AppPaths.Root;

    public void Dispose()
    {
        _engine.OutputDevicesChanged -= OnDevicesChanged;
        _library.FoldersChanged -= OnFoldersChanged;
        _playback.PropertyChanged -= OnPlaybackChanged;
    }

    private void ApplyOutput()
    {
        if (_refreshing)
        {
            return;
        }

        _engine.SetOutput(_selectedDevice?.Key, _backend, _mode);
    }

    private void OnDevicesChanged(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(DispatcherPriority.Background, RefreshDevices);

    private void OnFoldersChanged(object? sender, EventArgs e)
    {
        Raise(nameof(Folders));
        Raise(nameof(HasFolders));
    }

    private void OnPlaybackChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaybackViewModel.InfoRows))
        {
            NegotiationAttempts = _playback.Snapshot.Output?.Attempts ?? [];
        }
    }

    private void RefreshDevices()
    {
        _refreshing = true;
        try
        {
            var all = _engine.Devices.Enumerate();
            AsioAvailable = all.Any(d => d.Backend == OutputBackend.Asio);

            var wanted = all.Where(d => d.Backend == _backend).ToList();
            Devices.Clear();
            foreach (var device in wanted)
            {
                Devices.Add(new DeviceOption(device.Key, device.Name, device.Backend, device.IsDefault));
            }

            var savedKey = _settings.Current.OutputDeviceKey;
            var match = Devices.FirstOrDefault(d => d.Key == savedKey)
                        ?? Devices.FirstOrDefault(d => d.IsDefault)
                        ?? Devices.FirstOrDefault();

            _selectedDevice = match;
            Raise(nameof(SelectedDevice));
            (CheckFormatsCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>
    /// Asks the selected device which sample rates it accepts in exclusive mode. This only asks;
    /// it does not play anything and it does not take the device.
    /// </summary>
    private async Task CheckFormatsAsync()
    {
        var device = SelectedDevice;
        if (device is null)
        {
            return;
        }

        IsProbing = true;
        FormatStatus = "Asking the device…";

        try
        {
            var rows = await Task.Run(() =>
            {
                var results = new List<FormatSupportRow>();
                int[] rates = [44100, 48000, 88200, 96000, 176400, 192000];
                int[] depths = [16, 24];

                foreach (var depth in depths)
                {
                    foreach (var rate in rates)
                    {
                        var source = new SourceFormat("FLAC", rate, 2, depth);
                        var candidates = OutputFormatPolicy.ExclusiveCandidates(source);
                        var probes = _engine.Devices.ProbeFormats(device.Key.Split('|', 2)[1], AudioClientShareMode.Exclusive, candidates);
                        var accepted = probes.FirstOrDefault(p => p.Supported);

                        results.Add(new FormatSupportRow(
                            $"{depth}-bit / {TextFormat.KiloHertz(rate)}",
                            accepted is not null
                                ? $"Accepted as {accepted.Requested.Describe()}"
                                : probes.FirstOrDefault(p => p.Error is not null)?.Error ?? "Rejected by the device",
                            accepted is not null));
                    }
                }

                return results;
            });

            FormatRows = rows;
            FormatStatus = $"{rows.Count(r => r.Accepted)} of {rows.Count} formats accepted in exclusive mode. This reports what the driver says; it does not verify the audio that reaches the DAC.";
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            FormatStatus = $"The device could not be queried: {ex.Message}";
        }
        finally
        {
            IsProbing = false;
        }
    }
}
