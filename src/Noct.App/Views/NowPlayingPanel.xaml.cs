using System.Windows;
using System.Windows.Controls;
using Noct.App.ViewModels;

namespace Noct.App.Views;

public partial class NowPlayingPanel : UserControl
{
    public NowPlayingPanel()
    {
        InitializeComponent();
    }

    /// <summary>Clicking the artwork opens the Now Playing page. Playback is not touched.</summary>
    private void OnArtworkClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this)?.DataContext is MainViewModel main)
        {
            main.Section = AppSection.NowPlaying;
        }
    }
}
