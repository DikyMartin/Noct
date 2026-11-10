using Noct.Core.Library;

namespace Noct.Core.Playback;

public enum RepeatMode
{
    Off,
    All,
    One,
}

/// <summary>
/// The playing order. Plain and explicit: no shuffle in this release, and the current index is
/// always a real position in the list or -1 when nothing is loaded.
/// </summary>
public sealed class PlaybackQueue
{
    private readonly object _gate = new();
    private readonly List<Track> _items = [];
    private int _index = -1;
    private int _version;

    public RepeatMode Repeat { get; set; } = RepeatMode.Off;

    /// <summary>Increases whenever the list of items changes, so a view knows when to rebuild.</summary>
    public int Version
    {
        get
        {
            lock (_gate)
            {
                return _version;
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    public int Index
    {
        get
        {
            lock (_gate)
            {
                return _index;
            }
        }
    }

    public Track? Current
    {
        get
        {
            lock (_gate)
            {
                return _index >= 0 && _index < _items.Count ? _items[_index] : null;
            }
        }
    }

    public IReadOnlyList<Track> Items
    {
        get
        {
            lock (_gate)
            {
                return _items.ToArray();
            }
        }
    }

    public void Replace(IEnumerable<Track> tracks, int index)
    {
        lock (_gate)
        {
            _items.Clear();
            _items.AddRange(tracks);
            _index = _items.Count == 0 ? -1 : Math.Clamp(index, 0, _items.Count - 1);
            _version++;
        }
    }

    public void Append(IEnumerable<Track> tracks)
    {
        lock (_gate)
        {
            _items.AddRange(tracks);
            _version++;
            if (_index < 0 && _items.Count > 0)
            {
                _index = 0;
            }
        }
    }

    /// <summary>Inserts tracks directly after the current item.</summary>
    public void InsertNext(IEnumerable<Track> tracks)
    {
        lock (_gate)
        {
            var at = _index < 0 ? _items.Count : _index + 1;
            _items.InsertRange(at, tracks);
            _version++;
            if (_index < 0 && _items.Count > 0)
            {
                _index = 0;
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _items.Clear();
            _index = -1;
            _version++;
        }
    }

    public bool RemoveAt(int position)
    {
        lock (_gate)
        {
            if (position < 0 || position >= _items.Count)
            {
                return false;
            }

            _items.RemoveAt(position);
            _version++;
            if (_items.Count == 0)
            {
                _index = -1;
            }
            else if (position < _index)
            {
                _index--;
            }
            else if (position == _index)
            {
                _index = Math.Min(_index, _items.Count - 1);
            }

            return true;
        }
    }

    public bool TrySetIndex(int index)
    {
        lock (_gate)
        {
            if (index < 0 || index >= _items.Count)
            {
                return false;
            }

            _index = index;
            return true;
        }
    }

    /// <summary>The item that would follow, honouring repeat, without changing the index.</summary>
    public (int Index, Track Track)? PeekNext()
    {
        lock (_gate)
        {
            if (_items.Count == 0 || _index < 0)
            {
                return null;
            }

            if (Repeat == RepeatMode.One)
            {
                return (_index, _items[_index]);
            }

            var next = _index + 1;
            if (next < _items.Count)
            {
                return (next, _items[next]);
            }

            return Repeat == RepeatMode.All ? (0, _items[0]) : null;
        }
    }

    public (int Index, Track Track)? PeekPrevious()
    {
        lock (_gate)
        {
            if (_items.Count == 0 || _index < 0)
            {
                return null;
            }

            var previous = _index - 1;
            if (previous >= 0)
            {
                return (previous, _items[previous]);
            }

            return Repeat == RepeatMode.All ? (_items.Count - 1, _items[^1]) : null;
        }
    }

    public int IndexOf(Guid trackId)
    {
        lock (_gate)
        {
            for (var i = 0; i < _items.Count; i++)
            {
                if (_items[i].Id == trackId)
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
