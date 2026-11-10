using System.Threading.Channels;
using NAudio.Wave;
using Noct.Core.Audio;
using Noct.Core.Library;
using Noct.Core.Settings;

namespace Noct.Core.Playback;

/// <summary>
/// The single playback session. Both windows and the tray drive this one object, which is why a
/// window transition cannot restart, pause or duplicate audio.
/// </summary>
/// <remarks>
/// <para>
/// Every state change runs through one serialized command loop on a dedicated worker. Public
/// methods only post a command and return, so no caller can block the UI thread and no two
/// operations can interleave on the device. Ordering is first-in, first-out, which is what makes a
/// rapid play/seek/next burst end in one valid state rather than a race.
/// </para>
/// <para>
/// The engine owns the decoder chain, the output device and the position clock. It never resamples:
/// a track whose rate the device refuses is reported as a failure with the device's own answer.
/// </para>
/// </remarks>
public sealed class PlaybackEngine : IAsyncDisposable, IDisposable
{
    private const int PositionTicksPerSecond = 5;

    private readonly Channel<Command> _commands = Channel.CreateUnbounded<Command>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });

    private readonly Task _pump;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly OutputDeviceCatalog _catalog;
    private readonly SettingsStore _settings;
    private readonly Timer _positionTimer;
    private readonly object _snapshotGate = new();
    private readonly object _timerGate = new();
    private int _timerInterval = Timeout.Infinite;
    private int _positionTicks = 1;

    private IAudioOutput? _output;
    private TrackStreamProvider? _provider;
    private OutputDeviceRef? _device;
    private OutputNegotiation? _negotiation;

    /// <summary>Frames already accounted for before the current start/stop cycle of the device.</summary>
    private long _playedEpoch;

    /// <summary>
    /// True between the decoder running dry and the device finishing the audio already handed to
    /// it. Tearing down at the first event would cut off the last buffer of every track.
    /// </summary>
    private bool _draining;

    /// <summary>Where the first Resume starts after a restored queue.</summary>
    private TimeSpan _resumeAt;

    private Guid _lastPersistedTrack;
    private long _lastPersistTick;

    private DateTimeOffset _drainDeadline;
    private DateTimeOffset _drainStartedAt;
    private long _drainStartPlayed;

    private Guid _armedNextId;
    private int _armedNextQueueIndex = -1;
    private PlaybackSnapshot _snapshot = PlaybackSnapshot.Empty;
    private bool _disposed;

    public PlaybackEngine(SettingsStore settings, OutputDeviceCatalog? catalog = null)
    {
        _settings = settings;
        _catalog = catalog ?? new OutputDeviceCatalog();
        _catalog.DevicesChanged += OnDevicesChanged;
        _catalog.StartWatching();

        Queue = new PlaybackQueue();
        _pump = Task.Run(PumpAsync);

        // Created stopped. It only runs while something is playing or draining; see UpdateTickTimer.
        _positionTimer = new Timer(
            _ => Post(new Command(CommandKind.Tick)),
            null,
            Timeout.Infinite,
            Timeout.Infinite);
    }

    /// <summary>
    /// False while no visible surface shows the playback position (main window hidden and the island
    /// not expanded). The engine then checks in about once a second instead of five times, and does
    /// not publish position-only snapshots.
    /// </summary>
    public bool PositionTicksEnabled
    {
        get => Volatile.Read(ref _positionTicks) != 0;
        set
        {
            Volatile.Write(ref _positionTicks, value ? 1 : 0);
            Post(new Command(CommandKind.RefreshTimer));
        }
    }

    /// <summary>
    /// Runs the timer only when it has work: playing (position, arming the next track) or draining
    /// the last buffer. Idle, paused, stopped and failed states have no timer at all.
    /// </summary>
    private void UpdateTickTimer()
    {
        if (_disposed)
        {
            return;
        }

        var needed = _draining || _output?.State == OutputState.Playing;
        var interval = !needed ? Timeout.Infinite : _draining ? 100 : PositionTicksEnabled ? 200 : 1000;

        lock (_timerGate)
        {
            if (interval == _timerInterval)
            {
                return;
            }

            _timerInterval = interval;
            try
            {
                _positionTimer.Change(interval, interval == Timeout.Infinite ? Timeout.Infinite : interval);
            }
            catch (ObjectDisposedException)
            {
                // Shutting down.
            }
        }
    }

    public event EventHandler<PlaybackSnapshot>? SnapshotChanged;

    /// <summary>Raised once per notice, so a transient message is not re-shown on every tick.</summary>
    public event EventHandler<PlaybackNotice>? NoticeRaised;

    /// <summary>Raised when the set of output devices changes, so the UI can refresh its list.</summary>
    public event EventHandler? OutputDevicesChanged;

    public PlaybackQueue Queue { get; }

    public OutputDeviceCatalog Devices => _catalog;

    public PlaybackSnapshot Snapshot
    {
        get
        {
            lock (_snapshotGate)
            {
                return _snapshot;
            }
        }
    }

    public void PlayTracks(IReadOnlyList<Track> tracks, int index) =>
        Post(new Command(CommandKind.PlayList) { Tracks = tracks, Index = index });

    public void PlayQueueIndex(int index) => Post(new Command(CommandKind.PlayIndex) { Index = index });

    public void TogglePause() => Post(new Command(CommandKind.TogglePause));

    public void Resume() => Post(new Command(CommandKind.Resume));

    public void Pause() => Post(new Command(CommandKind.Pause));

    public void Next() => Post(new Command(CommandKind.Next));

    public void Previous() => Post(new Command(CommandKind.Previous));

    public void Stop() => Post(new Command(CommandKind.Stop));

    public void Seek(TimeSpan position) => Post(new Command(CommandKind.Seek) { Position = position });

    /// <summary>
    /// The requested software volume, 0..1 in whole-percent steps. This one stored value is what the
    /// slider, the percentage label and the output all follow; it is restored from settings at startup.
    /// </summary>
    public double Volume => _settings.Current.Volume;

    /// <summary>
    /// Requests a software volume (0..1, rounded to a whole percent). The value is stored at once, on
    /// the calling thread, so no snapshot published afterwards can carry an older value, and it is
    /// applied to the open output if there is one and software gain is allowed. With no output open it
    /// simply waits and is applied when playback starts.
    /// </summary>
    public void SetVolume(double volume)
    {
        if (double.IsNaN(volume))
        {
            return;
        }

        var requested = Math.Round(Math.Clamp(volume, 0, 1), 2);
        _settings.Update(s => s.Volume = requested);
        Post(new Command(CommandKind.SetVolume));
    }

    public void SetRepeat(RepeatMode mode) => Post(new Command(CommandKind.SetRepeat) { Repeat = mode });

    /// <summary>Changes the output device, backend or mode and reopens the stream in place.</summary>
    public void SetOutput(string? deviceKey, OutputBackend backend, OutputMode mode) =>
        Post(new Command(CommandKind.SetOutput) { DeviceKey = deviceKey, Backend = backend, Mode = mode });

    /// <summary>
    /// Restores a saved queue without opening the device or playing anything. The first Resume
    /// then starts the current track at <paramref name="resumeAt"/>.
    /// </summary>
    public void LoadQueue(IReadOnlyList<Track> tracks, int index, TimeSpan resumeAt) =>
        Post(new Command(CommandKind.LoadQueue) { Tracks = tracks, Index = index, Position = resumeAt });

    public void EnqueueNext(IReadOnlyList<Track> tracks) => Post(new Command(CommandKind.EnqueueNext) { Tracks = tracks });

    public void AppendToQueue(IReadOnlyList<Track> tracks) => Post(new Command(CommandKind.Append) { Tracks = tracks });

    public void RemoveFromQueue(int index) => Post(new Command(CommandKind.RemoveAt) { Index = index });

    public void ClearQueue() => Post(new Command(CommandKind.ClearQueue));

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _positionTimer.DisposeAsync().ConfigureAwait(false);

        Post(new Command(CommandKind.Shutdown));
        _commands.Writer.TryComplete();

        try
        {
            await _pump.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Shutdown must complete even if a driver is wedged.
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        _catalog.DevicesChanged -= OnDevicesChanged;
        _catalog.Dispose();
        _shutdown.Dispose();
    }

    private void Post(Command command)
    {
        if (!_commands.Writer.TryWrite(command) && command.Kind != CommandKind.Tick)
        {
            // The channel is unbounded, so this only happens after completion; nothing to do.
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var command in _commands.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    if (command.Kind == CommandKind.Shutdown)
                    {
                        TeardownOutput();
                        PublishSnapshot(PlaybackStatus.Stopped, null);
                        return;
                    }

                    await ExecuteAsync(command).ConfigureAwait(false);
                    UpdateTickTimer();
                }
                catch (Exception ex)
                {
                    TeardownOutput();
                    PublishSnapshot(
                        PlaybackStatus.Failed,
                        new PlaybackNotice(NoticeSeverity.Error, $"Playback stopped: {ex.Message}", "Choose a track to try again."));
                }
            }
        }
        finally
        {
            TeardownOutput();
        }
    }

    private async Task ExecuteAsync(Command command)
    {
        switch (command.Kind)
        {
            case CommandKind.Tick:
                if (_draining && IsDrainComplete())
                {
                    _draining = false;
                    await FinishStreamAsync().ConfigureAwait(false);
                    break;
                }

                if (_output is null || _output.State != OutputState.Playing)
                {
                    break;
                }

                // Position-only snapshots are skipped while nothing on screen shows the position.
                if (PositionTicksEnabled)
                {
                    PublishSnapshot(null, null);
                }

                ArmNextIfPossible();
                break;

            case CommandKind.RefreshTimer:
                // Showing the position again needs a fresh value right away, not at the next tick.
                if (PositionTicksEnabled && _output?.State == OutputState.Playing)
                {
                    PublishSnapshot(null, null);
                }

                break;

            case CommandKind.PlayList:
                Queue.Replace(command.Tracks ?? [], command.Index);
                PersistQueue();
                await StartCurrentAsync(TimeSpan.Zero).ConfigureAwait(false);
                break;

            case CommandKind.LoadQueue:
                if (_output is null)
                {
                    Queue.Replace(command.Tracks ?? [], command.Index);
                    _resumeAt = command.Position;
                    PublishSnapshot(PlaybackStatus.Idle, null);
                }

                break;

            case CommandKind.PlayIndex:
                if (Queue.TrySetIndex(command.Index))
                {
                    await StartCurrentAsync(TimeSpan.Zero).ConfigureAwait(false);
                }

                break;

            case CommandKind.TogglePause:
                TogglePauseCore();
                break;

            case CommandKind.Pause:
                if (_output?.State == OutputState.Playing)
                {
                    _output.Pause();
                    PublishSnapshot(PlaybackStatus.Paused, null);
                }

                break;

            case CommandKind.Resume:
                if (_output is not null && _output.State != OutputState.Playing)
                {
                    _output.Resume();
                    PublishSnapshot(PlaybackStatus.Playing, null);
                }
                else if (_output is null && Queue.Current is not null)
                {
                    var startAt = _resumeAt;
                    _resumeAt = TimeSpan.Zero;
                    await StartCurrentAsync(startAt).ConfigureAwait(false);
                }

                break;

            case CommandKind.Next:
                await AdvanceAsync(forward: true, userInitiated: true).ConfigureAwait(false);
                break;

            case CommandKind.Previous:
                await PreviousAsync().ConfigureAwait(false);
                break;

            case CommandKind.Stop:
                TeardownOutput();
                PublishSnapshot(PlaybackStatus.Stopped, null);
                break;

            case CommandKind.Seek:
                await SeekCoreAsync(command.Position).ConfigureAwait(false);
                break;

            case CommandKind.SetVolume:
                ApplyVolume();
                break;

            case CommandKind.SetRepeat:
                Queue.Repeat = command.Repeat;
                _settings.Update(_ => { });
                PublishSnapshot(null, null);
                break;

            case CommandKind.SetOutput:
                await ChangeOutputAsync(command.DeviceKey, command.Backend, command.Mode).ConfigureAwait(false);
                break;

            case CommandKind.EnqueueNext:
                Queue.InsertNext(command.Tracks ?? []);
                InvalidateArmedNext();
                PersistQueue();
                PublishSnapshot(null, null);
                break;

            case CommandKind.Append:
                Queue.Append(command.Tracks ?? []);
                PersistQueue();
                PublishSnapshot(null, null);
                break;

            case CommandKind.RemoveAt:
                if (Queue.RemoveAt(command.Index))
                {
                    InvalidateArmedNext();
                    PersistQueue();
                    PublishSnapshot(null, null);
                }

                break;

            case CommandKind.ClearQueue:
                Queue.Clear();
                TeardownOutput();
                PersistQueue();
                PublishSnapshot(PlaybackStatus.Idle, null);
                break;

            case CommandKind.StreamExhausted:
                BeginDrain();
                break;

            case CommandKind.TrackStartedInStream:
                HandleTrackStartedInStream(command.TrackId);
                break;

            case CommandKind.OutputStopped:
                await HandleOutputStoppedAsync(command.Error).ConfigureAwait(false);
                break;

            case CommandKind.DecodeFailed:
                await HandleDecodeFailureAsync(command.Error).ConfigureAwait(false);
                break;
        }
    }

    private void TogglePauseCore()
    {
        if (_output is null)
        {
            if (Queue.Current is not null)
            {
                Post(new Command(CommandKind.Resume));
            }

            return;
        }

        if (_output.State == OutputState.Playing)
        {
            _output.Pause();
            PublishSnapshot(PlaybackStatus.Paused, null);
        }
        else
        {
            _output.Resume();
            PublishSnapshot(PlaybackStatus.Playing, null);
        }
    }

    private async Task StartCurrentAsync(TimeSpan startAt)
    {
        var track = Queue.Current;
        if (track is null)
        {
            TeardownOutput();
            PublishSnapshot(PlaybackStatus.Idle, null);
            return;
        }

        PublishSnapshot(PlaybackStatus.Opening, null);
        TeardownOutput();

        FlacDecoder decoder;
        try
        {
            decoder = await Task.Run(() => FlacDecoder.Open(track.Path)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException or NotSupportedException or UnauthorizedAccessException)
        {
            PublishSnapshot(
                PlaybackStatus.Failed,
                new PlaybackNotice(NoticeSeverity.Error, $"{track.Title} could not be opened: {ex.Message}", "Skip to the next track or rescan the folder."));
            await SkipFailedTrackAsync().ConfigureAwait(false);
            return;
        }

        if (startAt > TimeSpan.Zero)
        {
            var sample = (long)(startAt.TotalSeconds * decoder.SampleRate);
            try
            {
                decoder.Seek(sample);
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException)
            {
                decoder.Dispose();
                PublishSnapshot(
                    PlaybackStatus.Failed,
                    new PlaybackNotice(NoticeSeverity.Error, $"Could not seek in {track.Title}: {ex.Message}"));
                return;
            }
        }

        var source = SourceFormat.FromFlac(decoder.StreamInfo);
        var settings = _settings.Current;
        var device = _catalog.Resolve(settings.OutputDeviceKey, settings.OutputBackend);

        if (device is null)
        {
            decoder.Dispose();
            PublishSnapshot(
                PlaybackStatus.Failed,
                new PlaybackNotice(NoticeSeverity.Error, "No audio output device is available.", "Connect a device, then press play."));
            return;
        }

        _device = device;
        var opened = OpenOutput(device, settings.OutputMode, source, decoder, track.Id, settings.OutputLatencyMilliseconds);
        if (!opened)
        {
            decoder.Dispose();
            return;
        }

        // The stored volume goes onto the stream before it starts, so playback never begins at
        // full level and then drops.
        ApplyGainToOutput();
        _output!.Play();
        PublishSnapshot(PlaybackStatus.Playing, null);
        PersistPosition(track, startAt);
        ArmNextIfPossible();
    }

    private bool OpenOutput(
        OutputDeviceRef device,
        OutputMode mode,
        SourceFormat source,
        FlacDecoder decoder,
        Guid trackId,
        int latencyMilliseconds)
    {
        TrackStreamProvider? provider = null;

        IWaveProvider CreateSource(AudioFormat format)
        {
            provider = new TrackStreamProvider(format);
            provider.SetCurrent(trackId, decoder);
            provider.SourceExhausted += OnSourceExhausted;
            provider.TrackStarted += OnTrackStartedInStream;
            provider.DecodeFailed += OnDecodeFailed;
            return provider;
        }

        IAudioOutput? output;
        OutputNegotiation negotiation;

        if (device.Backend == OutputBackend.Asio)
        {
            var asio = AsioAudioOutput.TryOpen(device, mode, source, CreateSource);
            output = asio.Output;
            negotiation = asio.Negotiation;
        }
        else
        {
            var wasapi = WasapiAudioOutput.TryOpen(_catalog, device, mode, source, latencyMilliseconds, CreateSource);
            output = wasapi.Output;
            negotiation = wasapi.Negotiation;
        }

        _negotiation = negotiation;

        if (output is null)
        {
            DetachProvider(provider);
            provider?.Dispose();

            var action = negotiation.FailureKind switch
            {
                OutputFailureKind.DeviceInUse => "Close the other application using the device, or switch to shared mode in Settings.",
                OutputFailureKind.ExclusiveModeNotAllowed => "Allow exclusive control for this device in Windows sound settings, or switch to shared mode.",
                OutputFailureKind.FormatNotSupported => "Pick a different output device or mode in Settings.",
                OutputFailureKind.TransparentPathUnavailable => "Choose WASAPI Exclusive in Settings, or switch to Normal mode.",
                OutputFailureKind.DeviceUnavailable => "Reconnect the device, then press play.",
                _ => "Choose a different output in Settings.",
            };

            PublishSnapshot(PlaybackStatus.Failed, new PlaybackNotice(NoticeSeverity.Error, negotiation.FailureMessage ?? "Output could not be opened.", action));
            return false;
        }

        _output = output;
        _provider = provider;
        _playedEpoch = 0;
        _output.Stopped += OnOutputStopped;
        return true;
    }

    private async Task SeekCoreAsync(TimeSpan position)
    {
        var track = Queue.Current;
        if (track is null)
        {
            return;
        }

        if (_output is null || _provider is null)
        {
            await StartCurrentAsync(position).ConfigureAwait(false);
            return;
        }

        var wasPlaying = _output.State == OutputState.Playing;
        var sample = (long)Math.Max(0, position.TotalSeconds * track.SampleRate);

        // Stopping first discards the buffered pre-seek audio, so the user hears the new position
        // instead of up to one buffer of the old one.
        _output.Stop();
        _provider.ClearNext();
        InvalidateArmedNext();

        try
        {
            _provider.SeekCurrent(sample);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException)
        {
            PublishSnapshot(PlaybackStatus.Failed, new PlaybackNotice(NoticeSeverity.Error, $"Could not seek: {ex.Message}"));
            return;
        }

        _playedEpoch = _provider.ProducedFrames;

        if (wasPlaying)
        {
            _output.Play();
            PublishSnapshot(PlaybackStatus.Playing, null);
        }
        else
        {
            PublishSnapshot(PlaybackStatus.Paused, null);
        }

        PersistPosition(track, position);
        ArmNextIfPossible();
    }

    private async Task AdvanceAsync(bool forward, bool userInitiated)
    {
        var next = forward ? Queue.PeekNext() : Queue.PeekPrevious();
        if (next is null)
        {
            TeardownOutput();
            PublishSnapshot(
                PlaybackStatus.Stopped,
                userInitiated ? null : new PlaybackNotice(NoticeSeverity.Info, "End of queue."));
            return;
        }

        Queue.TrySetIndex(next.Value.Index);
        await StartCurrentAsync(TimeSpan.Zero).ConfigureAwait(false);
    }

    private async Task PreviousAsync()
    {
        // Restart the current track when more than three seconds in, which is the behaviour most
        // players use and avoids losing your place on an accidental press.
        var snapshot = Snapshot;
        if (snapshot.Position > TimeSpan.FromSeconds(3) && Queue.Current is not null)
        {
            await SeekCoreAsync(TimeSpan.Zero).ConfigureAwait(false);
            return;
        }

        await AdvanceAsync(forward: false, userInitiated: true).ConfigureAwait(false);
    }

    private async Task SkipFailedTrackAsync()
    {
        var next = Queue.PeekNext();
        if (next is null || next.Value.Index == Queue.Index)
        {
            TeardownOutput();
            return;
        }

        Queue.TrySetIndex(next.Value.Index);
        await StartCurrentAsync(TimeSpan.Zero).ConfigureAwait(false);
    }

    /// <summary>
    /// Pre-opens the following track and arms it on the live stream when the formats match, which
    /// is what produces a true gapless join. A format change is left unarmed on purpose.
    /// </summary>
    private void ArmNextIfPossible()
    {
        if (_provider is null || _output is null || !_settings.Current.GaplessEnabled)
        {
            return;
        }

        var next = Queue.PeekNext();
        if (next is null || next.Value.Index == Queue.Index)
        {
            return;
        }

        if (_armedNextId == next.Value.Track.Id && _armedNextQueueIndex == next.Value.Index)
        {
            return;
        }

        var candidate = next.Value.Track;
        var provider = _provider;

        // Opening the file is I/O, so it never happens on the render thread.
        _ = Task.Run(() =>
        {
            FlacDecoder? decoder = null;
            try
            {
                decoder = FlacDecoder.Open(candidate.Path);
                if (!provider.Accepts(decoder))
                {
                    decoder.Dispose();
                    return;
                }

                if (provider.TrySetNext(candidate.Id, decoder))
                {
                    _armedNextId = candidate.Id;
                    _armedNextQueueIndex = next.Value.Index;
                    PublishSnapshot(null, null);
                }
                else
                {
                    decoder.Dispose();
                }
            }
            catch (Exception)
            {
                decoder?.Dispose();
            }
        });
    }

    private void InvalidateArmedNext()
    {
        _armedNextId = Guid.Empty;
        _armedNextQueueIndex = -1;
    }

    private void HandleTrackStartedInStream(Guid trackId)
    {
        // The stream advanced on its own across a gapless join: follow it in the queue without
        // touching the device.
        var index = Queue.IndexOf(trackId);
        if (index >= 0 && index != Queue.Index)
        {
            Queue.TrySetIndex(index);
        }

        InvalidateArmedNext();
        PublishSnapshot(null, null);
        PersistQueue();
        ArmNextIfPossible();
    }

    /// <summary>
    /// The decoder chain ran dry. The device still holds up to one buffer of real audio, so the
    /// stream is left running and the advance happens once that audio has actually been played.
    /// Without this, the last fraction of a second of every track would be discarded.
    /// </summary>
    private void BeginDrain()
    {
        if (_draining || _provider is null || _output is null)
        {
            return;
        }

        _draining = true;
        var latency = _negotiation?.LatencyMilliseconds ?? 200;
        _drainStartedAt = DateTimeOffset.UtcNow;
        _drainStartPlayed = AppFramesPlayed() ?? _playedEpoch;
        _drainDeadline = _drainStartedAt + TimeSpan.FromMilliseconds(latency + 750);
        PublishSnapshot(null, null);
    }

    private bool IsDrainComplete()
    {
        if (_provider is null || _output is null)
        {
            return true;
        }

        if (DateTimeOffset.UtcNow >= _drainDeadline)
        {
            return true;
        }

        var played = AppFramesPlayed();
        return played is not null && played.Value >= _provider.ProducedFrames;
    }

    private async Task FinishStreamAsync()
    {
        if (_provider is null)
        {
            return;
        }

        var next = Queue.PeekNext();
        if (next is null || next.Value.Index == Queue.Index && Queue.Repeat != RepeatMode.One)
        {
            TeardownOutput();
            PublishSnapshot(PlaybackStatus.Stopped, new PlaybackNotice(NoticeSeverity.Info, "End of queue."));
            return;
        }

        // Reaching here means the next track was not armed: either gapless is off or the format
        // differs. Both require reopening the device, which is reported rather than hidden.
        Queue.TrySetIndex(next.Value.Index);
        var differentFormat = _negotiation?.Source is { } previous &&
                              (previous.SampleRate != next.Value.Track.SampleRate ||
                               previous.Channels != next.Value.Track.Channels ||
                               previous.BitsPerSample != next.Value.Track.BitsPerSample);

        await StartCurrentAsync(TimeSpan.Zero).ConfigureAwait(false);

        if (differentFormat && Snapshot.Status == PlaybackStatus.Playing)
        {
            PublishSnapshot(
                null,
                new PlaybackNotice(
                    NoticeSeverity.Info,
                    "Output was reinitialized because the next track uses a different format. This transition is not gapless."));
        }
    }

    private async Task HandleOutputStoppedAsync(Exception? error)
    {
        if (error is null)
        {
            // A clean stop is either an explicit command or the stream draining; both already
            // produced the right snapshot.
            return;
        }

        var (kind, message) = OutputDeviceCatalog.Classify(error);
        TeardownOutput();

        var action = kind switch
        {
            OutputFailureKind.DeviceUnavailable => "Reconnect the device, then press play. Settings lets you pick another output.",
            OutputFailureKind.DeviceInUse => "Close the other application using the device, or switch to shared mode.",
            _ => "Press play to try again, or choose another output in Settings.",
        };

        PublishSnapshot(PlaybackStatus.Failed, new PlaybackNotice(NoticeSeverity.Error, message, action));
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task HandleDecodeFailureAsync(Exception? error)
    {
        var track = Queue.Current;
        var message = error is null
            ? "A track could not be decoded."
            : $"{track?.Title ?? "This track"} stopped decoding: {error.Message}";

        PublishSnapshot(PlaybackStatus.Failed, new PlaybackNotice(NoticeSeverity.Error, message, "Skipping to the next track."));
        await SkipFailedTrackAsync().ConfigureAwait(false);
    }

    private async Task ChangeOutputAsync(string? deviceKey, OutputBackend backend, OutputMode mode)
    {
        _settings.Update(s =>
        {
            s.OutputDeviceKey = deviceKey;
            s.OutputBackend = backend;
            s.OutputMode = mode;
        });

        var snapshot = Snapshot;
        var wasPlaying = snapshot.Status == PlaybackStatus.Playing;
        var position = snapshot.Position;

        if (_output is null)
        {
            // Nothing playing: just report what the new selection would do for the current track.
            var track = Queue.Current;
            if (track is not null)
            {
                var device = _catalog.Resolve(deviceKey, backend);
                if (device is not null)
                {
                    _negotiation = device.Backend == OutputBackend.Asio
                        ? AsioAudioOutput.Negotiate(device, mode, track.SourceFormat)
                        : WasapiAudioOutput.Negotiate(_catalog, device, mode, track.SourceFormat, _settings.Current.OutputLatencyMilliseconds);
                }
            }

            PublishSnapshot(null, null);
            return;
        }

        TeardownOutput();
        await StartCurrentAsync(position).ConfigureAwait(false);

        if (!wasPlaying && _output is not null)
        {
            _output.Pause();
            PublishSnapshot(PlaybackStatus.Paused, null);
        }
    }

    /// <summary>Re-applies the stored volume to the open output and tells the UI.</summary>
    private void ApplyVolume()
    {
        ApplyGainToOutput();
        PublishSnapshot(null, null);
    }

    /// <summary>
    /// Puts the stored volume on the open WASAPI stream. Does nothing when no output is open (the value
    /// is applied at start) and nothing when the level belongs to the DAC (transparent mode).
    /// </summary>
    private void ApplyGainToOutput()
    {
        if (_output is not WasapiAudioOutput wasapi || IsGainStageBypassed())
        {
            return;
        }

        try
        {
            wasapi.SetSoftwareVolume((float)_settings.Current.Volume);
        }
        catch (Exception)
        {
            // A device without a session volume is not an error worth interrupting playback for.
        }
    }

    /// <summary>
    /// True when the level is the DAC's business: a transparent stream is open, or transparent mode is
    /// configured and no stream is open yet. Software volume is not offered then.
    /// </summary>
    private bool IsGainStageBypassed() => _output is not null
        ? _negotiation?.Transparency == TransparencyStatus.ConfiguredNotHardwareVerified
        : _settings.Current.OutputMode == OutputMode.Transparent;
    private void TeardownOutput()
    {
        var output = _output;
        var provider = _provider;
        _output = null;
        _provider = null;
        _playedEpoch = 0;
        _draining = false;
        InvalidateArmedNext();

        if (output is not null)
        {
            output.Stopped -= OnOutputStopped;
            try
            {
                output.Stop();
            }
            catch (Exception)
            {
                // Ignore: the device may already be gone.
            }

            output.Dispose();
        }

        DetachProvider(provider);
        provider?.Dispose();
    }

    private void DetachProvider(TrackStreamProvider? provider)
    {
        if (provider is null)
        {
            return;
        }

        provider.SourceExhausted -= OnSourceExhausted;
        provider.TrackStarted -= OnTrackStartedInStream;
        provider.DecodeFailed -= OnDecodeFailed;
    }

    private void OnSourceExhausted(object? sender, EventArgs e) => Post(new Command(CommandKind.StreamExhausted));

    private void OnTrackStartedInStream(object? sender, Guid trackId) =>
        Post(new Command(CommandKind.TrackStartedInStream) { TrackId = trackId });

    private void OnDecodeFailed(object? sender, Exception error) =>
        Post(new Command(CommandKind.DecodeFailed) { Error = error });

    private void OnOutputStopped(object? sender, OutputStoppedEventArgs e) =>
        Post(new Command(CommandKind.OutputStopped) { Error = e.Exception });

    private void OnDevicesChanged(object? sender, EventArgs e) => OutputDevicesChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Builds the snapshot. Position comes from the frames the device reports as played, clamped
    /// into the range the provider has actually produced, so the displayed time can neither run
    /// ahead of the audio nor jump backwards.
    /// </summary>
    private void PublishSnapshot(PlaybackStatus? status, PlaybackNotice? notice)
    {
        PlaybackSnapshot snapshot;
        lock (_snapshotGate)
        {
            var track = Queue.Current;
            var resolvedStatus = status ?? _snapshot.Status;

            if (_output is not null)
            {
                resolvedStatus = _output.State switch
                {
                    OutputState.Playing => PlaybackStatus.Playing,
                    OutputState.Paused => PlaybackStatus.Paused,
                    _ => status ?? _snapshot.Status,
                };
            }

            var position = ComputePosition(out var resolvedTrackId);
            if (resolvedTrackId is { } id && id != Guid.Empty)
            {
                var index = Queue.IndexOf(id);
                if (index >= 0)
                {
                    track = Queue.Items[index];
                }
            }

            var transparent = IsGainStageBypassed();
            var duration = track?.Duration ?? TimeSpan.Zero;
            if (duration > TimeSpan.Zero && position > duration)
            {
                position = duration;
            }

            snapshot = new PlaybackSnapshot
            {
                Status = resolvedStatus,
                Track = track,
                Position = position,
                Duration = duration,
                QueueIndex = Queue.Index,
                QueueCount = Queue.Count,
                QueueVersion = Queue.Version,
                Repeat = Queue.Repeat,
                Output = _negotiation,
                Volume = transparent ? 1.0 : _settings.Current.Volume,
                VolumeIsSoftware = !transparent,
                NextTrackJoinedGaplessly = _armedNextId != Guid.Empty,

                // A notice describes an event, not a state. It is carried only while the status is
                // unchanged, and cleared as soon as playback moves on, so the UI never shows a
                // stale error next to a track that is playing fine.
                Notice = notice ?? (status is null or PlaybackStatus.Stopped ? _snapshot.Notice : null),
            };

                _snapshot = snapshot;
        }

        if (notice is not null)
        {
            NoticeRaised?.Invoke(this, notice);
        }

        SnapshotChanged?.Invoke(this, snapshot);

        if (snapshot.Track is not null && snapshot.Status == PlaybackStatus.Playing)
        {
            PersistPosition(snapshot.Track, snapshot.Position);
        }
    }

    /// <summary>
    /// Frames of <i>application</i> audio the device has played, or null when it reports nothing.
    /// </summary>
    /// <remarks>
    /// The device counts frames at its own accepted rate. When a resampler sits in the path those
    /// rates differ, so the count must be scaled back into the source timebase before it can be
    /// compared with what the provider produced. Skipping this scaling makes the elapsed time run
    /// at the wrong speed on any device whose rate does not match the file.
    /// </remarks>
    private long? AppFramesPlayed()
    {
        var reported = _output?.PlayedFrames;
        if (reported is not { } deviceFrames || deviceFrames < 0)
        {
            return null;
        }

        var app = _negotiation?.AppOutputFormat;
        var accepted = _negotiation?.AcceptedFormat;
        if (app is null || accepted is null || accepted.SampleRate <= 0 || accepted.SampleRate == app.SampleRate)
        {
            return _playedEpoch + deviceFrames;
        }

        var scaled = (long)((double)deviceFrames * app.SampleRate / accepted.SampleRate);
        return _playedEpoch + scaled;
    }

    private TimeSpan ComputePosition(out Guid? trackId)
    {
        trackId = null;
        var provider = _provider;
        if (provider is null)
        {
            return _snapshot.Status is PlaybackStatus.Stopped or PlaybackStatus.Idle ? TimeSpan.Zero : _snapshot.Position;
        }

        var produced = provider.ProducedFrames;
        var format = _negotiation?.AppOutputFormat ?? _negotiation?.AcceptedFormat;
        var sampleRate = format?.SampleRate ?? 0;
        if (sampleRate <= 0)
        {
            return _snapshot.Position;
        }

        long played;
        if (_draining)
        {
            // While the device drains its final buffer the endpoint counter stops short of what was
            // handed over, which would leave the elapsed time frozen just before the end. Walking
            // forward in real time instead lets the display reach the actual end of the track.
            var elapsed = DateTimeOffset.UtcNow - _drainStartedAt;
            played = _drainStartPlayed + (long)(elapsed.TotalSeconds * sampleRate);
        }
        else if (AppFramesPlayed() is { } reported)
        {
            played = reported;
        }
        else
        {
            // No usable device counter (ASIO): fall back to produced frames less one buffer.
            var buffered = (long)((_negotiation?.LatencyMilliseconds ?? 0) / 1000.0 * sampleRate);
            played = Math.Max(_playedEpoch, produced - buffered);
        }

        played = Math.Clamp(played, _playedEpoch, produced);

        var resolved = provider.Resolve(played);
        if (resolved is null)
        {
            return _snapshot.Position;
        }

        trackId = resolved.Value.TrackId;
        return TimeSpan.FromSeconds((double)resolved.Value.SampleOffset / sampleRate);
    }

    private void PersistPosition(Track track, TimeSpan position)
    {
        // Position is only a resume hint, so writing it a few times a minute is plenty. A track
        // change always persists immediately.
        var now = Environment.TickCount64;
        if (track.Id == _lastPersistedTrack && now - _lastPersistTick < 5000)
        {
            return;
        }

        _lastPersistedTrack = track.Id;
        _lastPersistTick = now;

        _settings.Update(s =>
        {
            s.LastTrackPath = track.Path;
            s.LastTrackPositionSamples = (long)(position.TotalSeconds * Math.Max(1, track.SampleRate));
        });
    }

    private void PersistQueue()
    {
        var paths = Queue.Items.Select(t => t.Path).ToList();
        _settings.Update(s => s.QueuePaths = paths);
    }

    private enum CommandKind
    {
        Tick,
        RefreshTimer,
        PlayList,
        PlayIndex,
        TogglePause,
        Pause,
        Resume,
        Next,
        Previous,
        Stop,
        Seek,
        SetVolume,
        SetRepeat,
        SetOutput,
        EnqueueNext,
        LoadQueue,
        Append,
        RemoveAt,
        ClearQueue,
        StreamExhausted,
        TrackStartedInStream,
        OutputStopped,
        DecodeFailed,
        Shutdown,
    }

    private sealed class Command(CommandKind kind)
    {
        public CommandKind Kind { get; } = kind;

        public IReadOnlyList<Track>? Tracks { get; init; }

        public int Index { get; init; }

        public TimeSpan Position { get; init; }

        public RepeatMode Repeat { get; init; }

        public string? DeviceKey { get; init; }

        public OutputBackend Backend { get; init; }

        public OutputMode Mode { get; init; }

        public Guid TrackId { get; init; }

        public Exception? Error { get; init; }
    }
}
