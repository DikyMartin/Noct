using Noct.Core.Audio;
using Noct.Core.Playback;
using Noct.Core.Settings;

namespace Noct.Tests;

/// <summary>
/// The requested volume is one stored value, rounded to a whole percent. These tests use a real engine
/// with no track loaded, which is exactly the "output not opened yet" situation.
/// </summary>
public sealed class VolumeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "noct-volume-" + Guid.NewGuid().ToString("N"));

    public VolumeTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private SettingsStore NewStore(Action<AppSettings>? configure = null)
    {
        var store = new SettingsStore(Path.Combine(_root, "settings.json"));
        if (configure is not null)
        {
            store.Update(configure);
        }

        return store;
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.25, 0.25)]
    [InlineData(0.7, 0.7)]
    [InlineData(1.0, 1.0)]
    [InlineData(0.254, 0.25)]
    [InlineData(0.7049999, 0.7)]
    [InlineData(-0.5, 0.0)]
    [InlineData(1.5, 1.0)]
    public void The_requested_volume_is_stored_at_once_as_a_whole_percent_in_range(double requested, double expected)
    {
        using var store = NewStore();
        using var engine = new PlaybackEngine(store);

        engine.SetVolume(requested);

        // Stored on the calling thread, before the engine thread has done anything.
        Assert.Equal(expected, engine.Volume);
        Assert.Equal(expected, store.Current.Volume);
    }

    [Fact]
    public void A_value_that_is_not_a_number_is_ignored()
    {
        using var store = NewStore(s => s.Volume = 0.4);
        using var engine = new PlaybackEngine(store);

        engine.SetVolume(double.NaN);

        Assert.Equal(0.4, engine.Volume);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.25)]
    [InlineData(0.7)]
    [InlineData(1.0)]
    public async Task Changing_volume_before_any_output_is_open_is_kept_and_reported_in_the_next_snapshot(double requested)
    {
        using var store = NewStore();
        using var engine = new PlaybackEngine(store);
        var published = new TaskCompletionSource<PlaybackSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.SnapshotChanged += (_, s) => published.TrySetResult(s);

        engine.SetVolume(requested);
        var snapshot = await published.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(requested, snapshot.Volume);
        Assert.True(snapshot.VolumeIsSoftware);
    }

    [Fact]
    public async Task In_transparent_mode_software_volume_is_not_offered_even_before_an_output_is_open_but_the_choice_is_kept()
    {
        using var store = NewStore(s => s.OutputMode = OutputMode.Transparent);
        using var engine = new PlaybackEngine(store);
        var published = new TaskCompletionSource<PlaybackSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.SnapshotChanged += (_, s) => published.TrySetResult(s);

        engine.SetVolume(0.25);
        var snapshot = await published.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(snapshot.VolumeIsSoftware);
        Assert.Equal(1.0, snapshot.Volume);
        Assert.Equal(0.25, engine.Volume); // used again when the user returns to Normal mode
    }

    [Fact]
    public void The_saved_volume_comes_back_after_a_restart()
    {
        using (var store = NewStore())
        using (var engine = new PlaybackEngine(store))
        {
            engine.SetVolume(0.42);
            store.Flush();
        }

        using var reopened = NewStore();
        using var engine2 = new PlaybackEngine(reopened);

        Assert.Equal(0.42, engine2.Volume);
    }
}
