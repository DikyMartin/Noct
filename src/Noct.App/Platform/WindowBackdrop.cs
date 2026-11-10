using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Noct.App.Platform;

public enum BackdropKind
{
    /// <summary>No native material: the window draws its own solid surface.</summary>
    None,

    /// <summary>Mica: subtle, desktop-tinted, calm. Used for the main window.</summary>
    Mica,

    /// <summary>Acrylic: blurred backdrop. Used for transient surfaces such as the island.</summary>
    Acrylic,
}

/// <summary>
/// The only place that talks to DWM about window material. A successful DWM call is not proof the
/// material is visible, so the result says what was <i>requested and accepted</i>; whether it
/// actually renders is confirmed by looking at a screenshot, and recorded in docs/BUILD_NOTES.md.
/// </summary>
internal static class WindowBackdrop
{
    /// <summary>
    /// Applies dark mode and the requested backdrop to a window whose HWND already exists.
    /// Returns the kind that DWM accepted; <see cref="BackdropKind.None"/> when it refused.
    /// </summary>
    public static BackdropKind Apply(Window window, BackdropKind requested, bool extendIntoClient)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        var source = HwndSource.FromHwnd(hwnd);
        if (source is null)
        {
            return BackdropKind.None;
        }

        var dark = 1;
        _ = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

        if (requested == BackdropKind.None)
        {
            var none = NativeMethods.DWMSBT_NONE;
            _ = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref none, sizeof(int));
            return BackdropKind.None;
        }

        // The backdrop is drawn beneath the client area, so the client area must be transparent to
        // WPF and the DWM frame must reach into it. Both are required; either alone shows nothing.
        if (source.CompositionTarget is { } target)
        {
            target.BackgroundColor = Colors.Transparent;
        }

        if (extendIntoClient)
        {
            var margins = new NativeMethods.Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            if (NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref margins) != 0)
            {
                return BackdropKind.None;
            }
        }

        var type = requested == BackdropKind.Mica ? NativeMethods.DWMSBT_MAINWINDOW : NativeMethods.DWMSBT_TRANSIENTWINDOW;
        var result = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int));
        return result == 0 ? requested : BackdropKind.None;
    }

    /// <summary>Asks DWM for round corners on a normal top-level window (Windows 11).</summary>
    public static void ApplyRoundedCorners(Window window, bool round)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        var preference = round ? NativeMethods.DWMWCP_ROUND : NativeMethods.DWMWCP_DONOTROUND;
        _ = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
    }
}
