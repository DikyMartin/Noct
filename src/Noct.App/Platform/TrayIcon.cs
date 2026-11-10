using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Noct.App.Platform;

/// <summary>
/// A notification-area icon that keeps the app recoverable while the main window is hidden.
/// </summary>
/// <remarks>
/// Built directly on Shell_NotifyIcon with a message-only window, so the app does not need to pull
/// in Windows Forms just for a tray icon. The icon is re-added if Explorer restarts, which would
/// otherwise silently remove it and leave a hidden app with no way back.
/// </remarks>
internal sealed class TrayIcon : IDisposable
{
    private const uint IconId = 1;
    private const int SM_CXSMICON = 49;

    private readonly string _tooltip;
    private readonly uint _callbackMessage = (uint)NativeMethods.WM_USER + 1;
    private readonly uint _taskbarCreatedMessage = NativeMethods.RegisterWindowMessageW("TaskbarCreated");

    private HwndSource? _source;
    private Icon? _icon;
    private bool _added;
    private bool _disposed;

    public TrayIcon(string tooltip)
    {
        _tooltip = tooltip.Length > 127 ? tooltip[..127] : tooltip;
    }

    /// <summary>Left click or double click: bring the main window back.</summary>
    public event EventHandler? OpenRequested;

    /// <summary>Right click: the host shows its menu at the given physical screen position.</summary>
    public event EventHandler<TrayMenuRequestedEventArgs>? MenuRequested;

    public void Show()
    {
        if (_disposed || _added)
        {
            return;
        }

        var parameters = new HwndSourceParameters("NoctTrayMessages")
        {
            ParentWindow = NativeMethods.HWND_MESSAGE,
            WindowStyle = 0,
            Width = 0,
            Height = 0,
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
        _icon = LoadIcon();
        Add();
    }

    public void SetTooltip(string text)
    {
        if (!_added || _source is null)
        {
            return;
        }

        var data = NewData();
        data.Flags = NativeMethods.NIF_TIP;
        data.Tip = text.Length > 127 ? text[..127] : text;
        NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_MODIFY, ref data);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_added && _source is not null)
        {
            var data = NewData();
            NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_DELETE, ref data);
            _added = false;
        }

        _source?.RemoveHook(WndProc);
        _source?.Dispose();
        _source = null;
        _icon?.Dispose();
        _icon = null;
    }

    private void Add()
    {
        if (_source is null)
        {
            return;
        }

        var data = NewData();
        data.Flags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP;
        data.CallbackMessage = _callbackMessage;
        data.Icon = _icon?.Handle ?? IntPtr.Zero;
        data.Tip = _tooltip;
        _added = NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_ADD, ref data);
    }

    private NativeMethods.NotifyIconData NewData() => new()
    {
        Size = Marshal.SizeOf<NativeMethods.NotifyIconData>(),
        Window = _source?.Handle ?? IntPtr.Zero,
        Id = IconId,
        Tip = string.Empty,
        Info = string.Empty,
        InfoTitle = string.Empty,
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _taskbarCreatedMessage)
        {
            _added = false;
            Add();
            return IntPtr.Zero;
        }

        if (msg != _callbackMessage)
        {
            return IntPtr.Zero;
        }

        switch ((int)(lParam.ToInt64() & 0xFFFF))
        {
            case NativeMethods.WM_LBUTTONUP:
            case NativeMethods.WM_LBUTTONDBLCLK:
                OpenRequested?.Invoke(this, EventArgs.Empty);
                handled = true;
                break;

            case NativeMethods.WM_RBUTTONUP:
            case NativeMethods.WM_CONTEXTMENU:
                NativeMethods.GetCursorPos(out var cursor);
                MenuRequested?.Invoke(this, new TrayMenuRequestedEventArgs(cursor.X, cursor.Y));
                handled = true;
                break;
        }

        return IntPtr.Zero;
    }

    private static Icon? LoadIcon()
    {
        try
        {
            var size = GetSystemMetrics(SM_CXSMICON);
            var info = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/Noct.ico"));
            return info is null ? null : new Icon(info.Stream, new System.Drawing.Size(size, size));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UriFormatException)
        {
            return null;
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}

internal sealed class TrayMenuRequestedEventArgs(int x, int y) : EventArgs
{
    public int X { get; } = x;

    public int Y { get; } = y;
}
