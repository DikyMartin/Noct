using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using Noct.App.Platform;
using Noct.App.ViewModels;

namespace Noct.App.Views;

/// <summary>
/// The tray menu: Open, play/pause and Exit, drawn with the app's dark menu style.
/// </summary>
/// <remarks>
/// A WPF menu can only dismiss itself on an outside click if its owner is the foreground window, so
/// the menu is anchored to a 1 x 1 transparent helper window that is placed at the pointer, made
/// foreground, and hidden again when the menu closes. It never appears in the taskbar.
/// </remarks>
internal sealed class TrayMenu : IDisposable
{
    private readonly PlaybackViewModel _playback;
    private readonly ContextMenu _menu;
    private readonly MenuItem _playPause;
    private Window? _anchor;

    public TrayMenu(PlaybackViewModel playback, Action open, Action exit)
    {
        _playback = playback;

        var openItem = new MenuItem { Header = "Open Noct" };
        openItem.Click += (_, _) => open();

        _playPause = new MenuItem { Header = "Play" };
        _playPause.Click += (_, _) => _playback.PlayPauseCommand.Execute(null);

        var exitItem = new MenuItem { Header = "Exit" };
        exitItem.Click += (_, _) => exit();

        _menu = new ContextMenu { Placement = PlacementMode.Bottom, StaysOpen = false };
        _menu.Items.Add(openItem);
        _menu.Items.Add(_playPause);
        _menu.Items.Add(new Separator());
        _menu.Items.Add(exitItem);
        _menu.Closed += (_, _) => _anchor?.Hide();
    }

    public void Show(int screenX, int screenY)
    {
        _playPause.Header = _playback.IsPlaying ? "Pause" : "Play";
        _playPause.IsEnabled = _playback.PlayPauseCommand.CanExecute(null);

        _anchor ??= new Window
        {
            Width = 1,
            Height = 1,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            Topmost = true,
            Title = "Noct tray menu",
        };

        _anchor.Show();
        var handle = new WindowInteropHelper(_anchor).Handle;
        NativeMethods.SetWindowPos(handle, NativeMethods.HWND_TOPMOST, screenX, screenY, 1, 1, NativeMethods.SWP_SHOWWINDOW);
        NativeMethods.SetForegroundWindow(handle);

        _menu.PlacementTarget = _anchor;
        _menu.IsOpen = true;
    }

    public void Dispose()
    {
        _menu.IsOpen = false;
        _anchor?.Close();
        _anchor = null;
    }
}
