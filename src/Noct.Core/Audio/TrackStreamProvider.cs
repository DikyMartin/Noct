using NAudio.Wave;

namespace Noct.Core.Audio;

/// <summary>Identifies a track inside one continuous output stream.</summary>
/// <param name="StartFrame">Frame index, within this output stream, where the track's first sample sits.</param>
/// <param name="FirstSample">Sample offset inside the track that <paramref name="StartFrame"/> corresponds to.</param>
/// <param name="Frames">Frames the track contributed, or null while it is still being read.</param>
public sealed record TrackSpan(Guid TrackId, long StartFrame, long FirstSample, long? Frames);

/// <summary>
/// Feeds the output device from a chain of FLAC decoders.
/// </summary>
/// <remarks>
/// <para>
/// This is where gapless happens. When the current decoder reaches its end and a next decoder with
/// an identical output format has been armed, the switch occurs inside a single
/// <see cref="Read(Span{byte})"/> call: the next track's first sample is written into the very next
/// slot of the same buffer, so the device receives one uninterrupted stream with no inserted
/// silence and no dropped samples. Nothing is faded or crossfaded to cover the join.
/// </para>
/// <para>
/// A next decoder whose format differs is refused here. The engine handles that case by letting
/// this stream drain and reopening the device, which is reported as a reinitialisation rather than
/// a gapless transition.
/// </para>
/// <para>
/// <see cref="Read(Span{byte})"/> runs on the device's render thread. Everything mutable is guarded,
/// and no file is opened from inside it: the engine pre-opens the next decoder on a worker thread.
/// </para>
/// </remarks>
public sealed class TrackStreamProvider : IWaveProvider, IDisposable
{
    private readonly object _gate = new();
    private readonly AudioFormat _format;
    private readonly List<TrackSpan> _spans = [];

    private FlacDecoder? _current;
    private Guid _currentId;
    private PcmPacker? _packer;
    private int[] _staging = [];

    private FlacDecoder? _next;
    private Guid _nextId;

    private long _producedFrames;
    private bool _ended;
    private bool _disposed;

    public TrackStreamProvider(AudioFormat format)
    {
        _format = format;
        WaveFormat = WasapiAudioOutput.ToWaveFormat(format);
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>Total frames handed to the device since this stream opened.</summary>
    public long ProducedFrames
    {
        get
        {
            lock (_gate)
            {
                return _producedFrames;
            }
        }
    }

    /// <summary>True once every armed decoder has been exhausted.</summary>
    public bool Ended
    {
        get
        {
            lock (_gate)
            {
                return _ended;
            }
        }
    }

    /// <summary>Raised when a track's first sample has been written, named by its id.</summary>
    public event EventHandler<Guid>? TrackStarted;

    /// <summary>Raised when a track's last sample has been written.</summary>
    public event EventHandler<Guid>? TrackCompleted;

    /// <summary>Raised when no armed decoder remains; the engine decides whether to stop.</summary>
    public event EventHandler? SourceExhausted;

    /// <summary>Raised when a decoder throws mid-stream. The stream ends rather than looping on the error.</summary>
    public event EventHandler<Exception>? DecodeFailed;

    /// <summary>Installs the first decoder. Takes ownership and disposes it with the provider.</summary>
    public void SetCurrent(Guid trackId, FlacDecoder decoder)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        if (!Accepts(decoder))
        {
            throw new ArgumentException("Decoder format does not match this stream's output format.", nameof(decoder));
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            _current?.Dispose();
            _current = decoder;
            _currentId = trackId;
            _packer = new PcmPacker(SourceOf(decoder), _format);
            _ended = false;
            _spans.Add(new TrackSpan(trackId, _producedFrames, decoder.Position, null));
        }

        TrackStarted?.Invoke(this, trackId);
    }

    /// <summary>
    /// Arms the next track for a gapless join. Returns false when the format differs, in which
    /// case the caller must reinitialise the device instead.
    /// </summary>
    public bool TrySetNext(Guid trackId, FlacDecoder decoder)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        if (!Accepts(decoder))
        {
            return false;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            _next?.Dispose();
            _next = decoder;
            _nextId = trackId;
            return true;
        }
    }

    public void ClearNext()
    {
        lock (_gate)
        {
            _next?.Dispose();
            _next = null;
            _nextId = Guid.Empty;
        }
    }

    /// <summary>True when a decoder can join this stream without reinitialising the device.</summary>
    public bool Accepts(FlacDecoder decoder) =>
        decoder.SampleRate == _format.SampleRate &&
        decoder.Channels == _format.Channels &&
        decoder.BitsPerSample <= _format.ValidBits;

    /// <summary>Seeks inside the current track and restarts span accounting from that point.</summary>
    public bool SeekCurrent(long sampleIndex)
    {
        lock (_gate)
        {
            if (_disposed || _current is null)
            {
                return false;
            }

            var ok = _current.Seek(sampleIndex);

            // After a seek the device buffer still holds pre-seek audio, but position reporting must
            // follow the new offset, so a fresh span starts here.
            _spans.Add(new TrackSpan(_currentId, _producedFrames, _current.Position, null));
            _ended = false;
            return ok;
        }
    }

    /// <summary>
    /// Maps a device-played frame count back to a track and an offset inside it. This is what makes
    /// the elapsed time correct across a gapless join instead of restarting at the wrong moment.
    /// </summary>
    public (Guid TrackId, long SampleOffset)? Resolve(long playedFrames)
    {
        lock (_gate)
        {
            TrackSpan? match = null;
            foreach (var span in _spans)
            {
                if (span.StartFrame > playedFrames)
                {
                    break;
                }

                match = span;
            }

            if (match is null)
            {
                return null;
            }

            var offset = match.FirstSample + (playedFrames - match.StartFrame);
            if (match.Frames is { } frames && playedFrames - match.StartFrame > frames)
            {
                offset = match.FirstSample + frames;
            }

            return (match.TrackId, offset);
        }
    }

    public int Read(Span<byte> buffer)
    {
        var bytesPerFrame = _format.BytesPerFrame;
        if (bytesPerFrame <= 0)
        {
            return 0;
        }

        var requestedFrames = buffer.Length / bytesPerFrame;
        if (requestedFrames == 0)
        {
            return 0;
        }

        var startedTracks = new List<Guid>();
        var completedTracks = new List<Guid>();
        var exhausted = false;
        Exception? failure = null;
        var writtenFrames = 0;

        lock (_gate)
        {
            if (_disposed)
            {
                return 0;
            }

            EnsureStaging(requestedFrames);

            try
            {
                while (writtenFrames < requestedFrames)
                {
                    if (_current is null || _packer is null)
                    {
                        if (!PromoteNextLocked(startedTracks))
                        {
                            // Only announce exhaustion once; later reads keep the device fed with
                            // silence while it drains, without re-raising the event every buffer.
                            exhausted = !_ended;
                            break;
                        }

                        continue;
                    }

                    var wanted = requestedFrames - writtenFrames;
                    var frames = _current.ReadSamples(_staging, wanted);

                    if (frames == 0)
                    {
                        CompleteCurrentLocked(completedTracks);
                        if (!PromoteNextLocked(startedTracks))
                        {
                            exhausted = !_ended;
                            break;
                        }

                        continue;
                    }

                    _packer.Pack(
                        _staging,
                        buffer[(writtenFrames * bytesPerFrame)..],
                        frames * _format.Channels);

                    writtenFrames += frames;
                    _producedFrames += frames;
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException or NotSupportedException)
            {
                failure = ex;
                CompleteCurrentLocked(completedTracks);
                _current?.Dispose();
                _current = null;
                _packer = null;
                exhausted = !_ended;
            }

            if (exhausted)
            {
                _ended = true;
            }
        }

        foreach (var id in completedTracks)
        {
            TrackCompleted?.Invoke(this, id);
        }

        foreach (var id in startedTracks)
        {
            TrackStarted?.Invoke(this, id);
        }

        if (failure is not null)
        {
            DecodeFailed?.Invoke(this, failure);
        }

        if (exhausted)
        {
            SourceExhausted?.Invoke(this, EventArgs.Empty);
        }

        // Silence pads only the unfilled tail of the final buffer, so the device drains cleanly
        // instead of replaying stale memory. It never pads a gapless join.
        if (writtenFrames < requestedFrames)
        {
            buffer[(writtenFrames * bytesPerFrame)..(requestedFrames * bytesPerFrame)].Clear();
        }

        return requestedFrames * bytesPerFrame;
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
            _current?.Dispose();
            _current = null;
            _next?.Dispose();
            _next = null;
            _packer = null;
            _spans.Clear();
        }
    }

    private void EnsureStaging(int frames)
    {
        var needed = frames * _format.Channels;
        if (_staging.Length < needed)
        {
            _staging = new int[needed];
        }
    }

    private void CompleteCurrentLocked(List<Guid> completed)
    {
        if (_currentId == Guid.Empty)
        {
            return;
        }

        for (var i = _spans.Count - 1; i >= 0; i--)
        {
            if (_spans[i].TrackId == _currentId && _spans[i].Frames is null)
            {
                _spans[i] = _spans[i] with { Frames = _producedFrames - _spans[i].StartFrame };
                break;
            }
        }

        completed.Add(_currentId);
        TrimSpansLocked();
    }

    private bool PromoteNextLocked(List<Guid> started)
    {
        _current?.Dispose();
        _current = null;
        _packer = null;

        if (_next is null)
        {
            _currentId = Guid.Empty;
            return false;
        }

        _current = _next;
        _currentId = _nextId;
        _next = null;
        _nextId = Guid.Empty;
        _packer = new PcmPacker(SourceOf(_current), _format);
        _spans.Add(new TrackSpan(_currentId, _producedFrames, _current.Position, null));
        started.Add(_currentId);
        return true;
    }

    /// <summary>Keeps the span list small; only the recent past is needed for position mapping.</summary>
    private void TrimSpansLocked()
    {
        const int keep = 8;
        if (_spans.Count > keep)
        {
            _spans.RemoveRange(0, _spans.Count - keep);
        }
    }

    private static SourceFormat SourceOf(FlacDecoder decoder) => SourceFormat.FromFlac(decoder.StreamInfo);
}
