using Noct.Core.Library;
using Noct.Core.Playback;

namespace Noct.Tests;

public sealed class PlaybackQueueTests
{
    private static Track MakeTrack(string name) => new()
    {
        Id = Track.StableId(name),
        Path = name,
        Title = name,
        SampleRate = 44100,
        Channels = 2,
        BitsPerSample = 16,
        TotalSamples = 44100,
    };

    private static PlaybackQueue MakeQueue(int count, int index = 0)
    {
        var queue = new PlaybackQueue();
        queue.Replace(Enumerable.Range(1, count).Select(i => MakeTrack($"t{i}")), index);
        return queue;
    }

    [Fact]
    public void Empty_queue_has_no_current_or_next()
    {
        var queue = new PlaybackQueue();

        Assert.Null(queue.Current);
        Assert.Equal(-1, queue.Index);
        Assert.Null(queue.PeekNext());
        Assert.Null(queue.PeekPrevious());
    }

    [Fact]
    public void Replace_clamps_an_out_of_range_start_index()
    {
        var queue = MakeQueue(3, index: 99);

        Assert.Equal(2, queue.Index);
        Assert.Equal("t3", queue.Current!.Title);
    }

    [Fact]
    public void Next_stops_at_the_end_when_repeat_is_off()
    {
        var queue = MakeQueue(2, index: 1);

        Assert.Null(queue.PeekNext());
    }

    [Fact]
    public void Repeat_all_wraps_in_both_directions()
    {
        var queue = MakeQueue(3);
        queue.Repeat = RepeatMode.All;

        Assert.Equal(2, queue.PeekPrevious()!.Value.Index);

        queue.TrySetIndex(2);
        Assert.Equal(0, queue.PeekNext()!.Value.Index);
    }

    [Fact]
    public void Repeat_one_keeps_the_same_track()
    {
        var queue = MakeQueue(3, index: 1);
        queue.Repeat = RepeatMode.One;

        Assert.Equal(1, queue.PeekNext()!.Value.Index);
    }

    [Fact]
    public void Removing_an_earlier_item_keeps_the_current_track_current()
    {
        var queue = MakeQueue(4, index: 2);
        var current = queue.Current;

        queue.RemoveAt(0);

        Assert.Equal(1, queue.Index);
        Assert.Same(current, queue.Current);
    }

    [Fact]
    public void Removing_the_current_item_selects_a_valid_neighbour()
    {
        var queue = MakeQueue(3, index: 2);

        queue.RemoveAt(2);

        Assert.Equal(1, queue.Index);
        Assert.Equal("t2", queue.Current!.Title);
    }

    [Fact]
    public void Removing_the_last_item_empties_the_queue()
    {
        var queue = MakeQueue(1);

        queue.RemoveAt(0);

        Assert.Equal(-1, queue.Index);
        Assert.Null(queue.Current);
    }

    [Fact]
    public void Out_of_range_removal_is_rejected()
    {
        var queue = MakeQueue(2);

        Assert.False(queue.RemoveAt(5));
        Assert.False(queue.RemoveAt(-1));
        Assert.Equal(2, queue.Count);
    }

    [Fact]
    public void Insert_next_places_tracks_directly_after_the_current_one()
    {
        var queue = MakeQueue(3, index: 0);

        queue.InsertNext([MakeTrack("x"), MakeTrack("y")]);

        Assert.Equal(["t1", "x", "y", "t2", "t3"], queue.Items.Select(t => t.Title));
        Assert.Equal("x", queue.PeekNext()!.Value.Track.Title);
    }

    [Fact]
    public void Append_to_an_empty_queue_selects_the_first_track()
    {
        var queue = new PlaybackQueue();

        queue.Append([MakeTrack("a"), MakeTrack("b")]);

        Assert.Equal(0, queue.Index);
        Assert.Equal("a", queue.Current!.Title);
    }

    [Fact]
    public void Index_of_finds_a_track_by_stable_id()
    {
        var queue = MakeQueue(3);

        Assert.Equal(1, queue.IndexOf(Track.StableId("t2")));
        Assert.Equal(-1, queue.IndexOf(Guid.NewGuid()));
    }

    [Fact]
    public void Stable_ids_ignore_case_and_slash_style_so_a_saved_queue_matches_after_restart()
    {
        Assert.Equal(Track.StableId(@"D:\Music\A.flac"), Track.StableId("d:/music/a.flac"));
        Assert.NotEqual(Track.StableId(@"D:\Music\A.flac"), Track.StableId(@"D:\Music\B.flac"));
    }
}
