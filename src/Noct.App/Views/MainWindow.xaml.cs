using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Noct.App.Platform;
using Noct.App.Themes;
using Noct.App.ViewModels;
using Noct.Core.Settings;

namespace Noct.App.Views;

public partial class MainWindow : Window
{
    /// <summary>The side panel needs this much width to leave the library list usable.</summary>
    private const double ArtworkPanelMinimumWindowWidth = 1040;

    /// <summary>The material requested for the main window. Acrylic reveals subdued shapes; Mica is calmer.</summary>
    private const BackdropKind PreferredBackdrop = BackdropKind.Acrylic;

    private readonly MainViewModel _model;
    private readonly SettingsStore _settings;
    private readonly SystemPreferences _preferences;

    public MainWindow(MainViewModel model, SettingsStore settings, SystemPreferences preferences)
    {
        _model = model;
        _settings = settings;
        _preferences = preferences;

        // Size is clamped to the work area so a small display never opens a window it cannot fit.
        var work = SystemParameters.WorkArea;
        Width = Math.Min(settings.Current.MainWindowWidth, work.Width);
        Height = Math.Min(settings.Current.MainWindowHeight, work.Height);
        MinWidth = Math.Min(960, work.Width);
        MinHeight = Math.Min(600, work.Height);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        DataContext = model;
        InitializeComponent();

        SourceInitialized += OnSourceInitialized;
        SizeChanged += OnSizeChangedInternal;
        StateChanged += OnStateChangedInternal;
        Closing += OnClosingInternal;
        _preferences.Changed += OnPreferencesChanged;
        _model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsNowPlaying))
            {
                UpdatePanelVisibility();
            }
        };
    }

    /// <summary>The material DWM accepted for this window after the last evaluation.</summary>
    public BackdropKind ActiveBackdrop { get; private set; }

    /// <summary>
    /// Set by the shell. Returns true when it took over a minimize request (and will hide the
    /// window itself), false to let Windows minimize normally.
    /// </summary>
    public Func<bool>? InterceptMinimize { get; set; }

    /// <summary>Raised when the user closes the window; the shell turns this into a clean exit.</summary>
    public event EventHandler? CloseRequested;

    public IntPtr Handle => new WindowInteropHelper(this).Handle;

    /// <summary>Brings the window back from the island or the tray as one valid, focused window.</summary>
    public void RestoreFromIsland()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();

        // Windows can refuse to raise a background window; a brief topmost toggle is the
        // supported way to make sure it comes to the front without stealing focus repeatedly.
        Topmost = true;
        Topmost = false;
        Focus();
    }

    public void ApplyMaterial()
    {
        var wanted = _preferences.BackdropAllowed ? PreferredBackdrop : BackdropKind.None;
        ActiveBackdrop = WindowBackdrop.Apply(this, wanted, extendIntoClient: true);
        ThemeManager.SetGlass(Application.Current, ActiveBackdrop != BackdropKind.None);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var source = HwndSource.FromHwnd(Handle);
        source?.AddHook(WndProc);

        WindowBackdrop.ApplyRoundedCorners(this, round: true);
        ApplyMaterial();
        UpdatePanelVisibility();
    }


    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_SYSCOMMAND &&
            (wParam.ToInt32() & NativeMethods.SC_MASK) == NativeMethods.SC_MINIMIZE &&
            InterceptMinimize?.Invoke() == true)
        {
            // The window never enters the minimized state: the shell hides it, which removes its
            // taskbar button, and shows the island in its place.
            handled = true;
        }

        if (msg == NativeMethods.WM_GETMINMAXINFO)
        {
            FitMaximizedToWorkArea(hwnd, lParam);
            handled = true;
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// A chromeless window maximizes to the screen plus its resize border, which spills past the
    /// edges and over the taskbar. Maximize to exactly the monitor's work area instead.
    /// </summary>
    private void FitMaximizedToWorkArea(IntPtr hwnd, IntPtr lParam)
    {
        var info = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.MinMaxInfo>(lParam);
        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var monitorInfo = new NativeMethods.MonitorInfo
        {
            Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>(),
        };

        if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
        {
            var work = monitorInfo.Work;
            var area = monitorInfo.Monitor;
            info.MaxPosition = new NativeMethods.Point { X = work.Left - area.Left, Y = work.Top - area.Top };
            info.MaxSize = new NativeMethods.Point { X = work.Right - work.Left, Y = work.Bottom - work.Top };
        }

        var scale = VisualTreeHelper.GetDpi(this);
        info.MinTrackSize = new NativeMethods.Point
        {
            X = (int)Math.Ceiling(MinWidth * scale.DpiScaleX),
            Y = (int)Math.Ceiling(MinHeight * scale.DpiScaleY),
        };

        System.Runtime.InteropServices.Marshal.StructureToPtr(info, lParam, false);
    }

    private void OnPreferencesChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(ApplyMaterial);

    private void OnSizeChangedInternal(object sender, SizeChangedEventArgs e) => UpdatePanelVisibility();

    private void UpdatePanelVisibility()
    {
        // The Now Playing page shows the artwork itself, so the side panel steps aside there.
        var show = ActualWidth >= ArtworkPanelMinimumWindowWidth && !_model.IsNowPlaying;
        _model.ShowArtworkPanel = show;
        PanelColumn.Width = show ? new GridLength(272) : new GridLength(0);
    }

    private void OnStateChangedInternal(object? sender, EventArgs e)
    {
        MaximizeGlyph.Data = (System.Windows.Media.Geometry)FindResource(
            WindowState == WindowState.Maximized ? "IconRestore" : "IconMaximize");
        MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
        System.Windows.Automation.AutomationProperties.SetName(MaximizeButton, MaximizeButton.ToolTip.ToString());

        // Minimizing by a route that bypasses WM_SYSCOMMAND (Show desktop, for example).
        if (WindowState == WindowState.Minimized && InterceptMinimize?.Invoke() == true)
        {
            Dispatcher.BeginInvoke(() => WindowState = WindowState.Normal);
        }
    }

    private void OnClosingInternal(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (WindowState == WindowState.Normal)
        {
            _settings.Update(s =>
            {
                s.MainWindowWidth = ActualWidth;
                s.MainWindowHeight = ActualHeight;
            });
        }

        // Close exits the app for this release. The shell stops playback and releases the device.
        e.Cancel = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Closes for real once the shell has finished its shutdown work.</summary>
    public void CloseForExit()
    {
        _preferences.Changed -= OnPreferencesChanged;
        Closing -= OnClosingInternal;
        Close();
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !string.IsNullOrEmpty(_model.Library.SearchText))
        {
            _model.Library.SearchText = string.Empty;
            e.Handled = true;
        }
    }
}
