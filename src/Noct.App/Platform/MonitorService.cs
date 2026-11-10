using System.Windows;
using System.Windows.Interop;
using Noct.Core.Island;

namespace Noct.App.Platform;

/// <summary>A monitor's physical bounds and effective scale at a moment in time.</summary>
public readonly record struct MonitorSnapshot(IntPtr Handle, PixelRect Bounds, PixelRect WorkArea, double Scale);

/// <summary>
/// Monitor lookup for placing the island. Always returns something usable: if the monitor a window
/// was on has been unplugged, the primary monitor is used instead.
/// </summary>
internal static class MonitorService
{
    /// <summary>The monitor a window is on, or the primary monitor when it is on none.</summary>
    public static MonitorSnapshot ForWindow(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var handle = hwnd == IntPtr.Zero
            ? IntPtr.Zero
            : NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTOPRIMARY);

        return handle == IntPtr.Zero ? Primary() : FromHandle(handle);
    }

    public static MonitorSnapshot ForHandle(IntPtr handle) => handle == IntPtr.Zero ? Primary() : FromHandle(handle);

    public static MonitorSnapshot ForCursor()
    {
        if (!NativeMethods.GetCursorPos(out var point))
        {
            return Primary();
        }

        var handle = NativeMethods.MonitorFromPoint(point, NativeMethods.MONITOR_DEFAULTTONEAREST);
        return handle == IntPtr.Zero ? Primary() : FromHandle(handle);
    }

    public static MonitorSnapshot Primary()
    {
        var handle = NativeMethods.MonitorFromPoint(new NativeMethods.Point { X = 0, Y = 0 }, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
        return FromHandle(handle);
    }

    /// <summary>True when the monitor handle still refers to a connected monitor.</summary>
    public static bool Exists(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        var info = new NativeMethods.MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        return NativeMethods.GetMonitorInfo(handle, ref info);
    }

    private static MonitorSnapshot FromHandle(IntPtr handle)
    {
        var info = new NativeMethods.MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (handle == IntPtr.Zero || !NativeMethods.GetMonitorInfo(handle, ref info))
        {
            // Last resort when Windows reports nothing: a plausible 1080p primary at 100%.
            var fallback = new PixelRect(0, 0, (int)SystemParameters.PrimaryScreenWidth, (int)SystemParameters.PrimaryScreenHeight);
            return new MonitorSnapshot(IntPtr.Zero, fallback, fallback, 1.0);
        }

        var scale = 1.0;
        if (NativeMethods.GetDpiForMonitor(handle, NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0)
        {
            scale = dpiX / 96.0;
        }

        return new MonitorSnapshot(handle, ToRect(info.Monitor), ToRect(info.Work), scale);
    }

    private static PixelRect ToRect(NativeMethods.Rect rect) =>
        new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
}
