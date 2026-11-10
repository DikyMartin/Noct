using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Noct.App.ViewModels;

namespace Noct.App.Views;

public partial class TransportBar : UserControl
{
    public TransportBar()
    {
        InitializeComponent();

        Seek.ScrubStarted += (_, _) => Playback?.BeginScrub();
        Seek.Scrubbing += (_, fraction) => Playback?.Scrub(fraction);
        Seek.SeekCommitted += (_, fraction) => Playback?.EndScrub(fraction);
        Seek.KeySeek += (_, fraction) => Playback?.SeekToFraction(fraction);

        DataContextChanged += (_, _) => HookModel();

        // The popup lives outside the normal visual tree, so it gets the settings model directly
        // instead of resolving it through an ancestor binding that may not reach it.
        Loaded += (_, _) =>
        {
            if (Window.GetWindow(this)?.DataContext is MainViewModel main)
            {
                OutputPanel.DataContext = main.Settings;
            }
        };
    }

    private PlaybackViewModel? Playback => DataContext as PlaybackViewModel;

    private PlaybackViewModel? _hooked;

    private void HookModel()
    {
        if (_hooked is not null)
        {
            _hooked.PropertyChanged -= OnModelChanged;
        }

        _hooked = Playback;
        if (_hooked is not null)
        {
            _hooked.PropertyChanged += OnModelChanged;
            UpdateGlyphs();
        }
    }

    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlaybackViewModel.IsPlaying) or nameof(PlaybackViewModel.RepeatIsOne))
        {
            UpdateGlyphs();
        }
    }

    /// <summary>Swaps the play/pause and repeat glyphs. Geometry is the only thing that changes.</summary>
    private void UpdateGlyphs()
    {
        if (Playback is not { } model)
        {
            return;
        }

        var isPlaying = model.IsPlaying;
        PlayGlyph.Data = (Geometry)FindResource(isPlaying ? "IconPause" : "IconPlay");
        PlayGlyph.Filled = !isPlaying;
        PlayGlyph.StrokeThickness = isPlaying ? 2.25 : 1.5;
        RepeatGlyph.Data = (Geometry)FindResource(model.RepeatIsOne ? "IconRepeatOne" : "IconRepeat");
    }

    private void OnRepeatClick(object sender, RoutedEventArgs e)
    {
        Playback?.ToggleRepeatCommand.Execute(null);

        // The toggle's own checked state is driven by the view model, not by the click.
        RepeatButton.IsChecked = Playback?.RepeatActive ?? false;
    }

    /// <summary>The small cover shown when the side panel has no room opens Now Playing, as the panel's cover does.</summary>
    private void OnArtworkClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this)?.DataContext is MainViewModel main)
        {
            main.Section = AppSection.NowPlaying;
        }
    }

    private void OnOutputSettingsClick(object sender, RoutedEventArgs e)
    {
        OutputButton.IsChecked = false;
        if (Window.GetWindow(this)?.DataContext is MainViewModel main)
        {
            main.Section = AppSection.Settings;
        }
    }
}
