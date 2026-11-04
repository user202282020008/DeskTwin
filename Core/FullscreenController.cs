using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DeskTwin.Core;

/// <summary>
/// Uses the monitor's physical bounds instead of WPF maximization, which is
/// constrained to the work area and can leave the taskbar visible.
/// </summary>
internal sealed class FullscreenController
{
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint MonitorDefaultToNearest = 2;
    private const uint MonitorDefaultToPrimary = 1;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;

    private readonly Window _window;
    private WindowStyle _windowStyle;
    private WindowState _windowState;
    private ResizeMode _resizeMode;
    private bool _topmost;
    private double _minWidth;
    private double _minHeight;
    private Rect _restoreBounds;

    internal FullscreenController(Window window)
    {
        _window = window;
    }

    internal bool IsFullscreen { get; private set; }

    internal void Enter()
    {
        if (IsFullscreen)
        {
            return;
        }

        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("窗口尚未创建，无法进入全屏。");
        }

        _windowStyle = _window.WindowStyle;
        _windowState = _window.WindowState;
        _resizeMode = _window.ResizeMode;
        _topmost = _window.Topmost;
        _minWidth = _window.MinWidth;
        _minHeight = _window.MinHeight;
        _restoreBounds = _windowState == WindowState.Maximized
            ? _window.RestoreBounds
            : new Rect(_window.Left, _window.Top, _window.Width, _window.Height);

        IsFullscreen = true;
        try
        {
            var monitorBounds = GetMonitorBounds(handle);

            // Normal + explicit monitor bounds covers the entire screen,
            // including the taskbar area. Maximized would use the work area.
            _window.WindowState = WindowState.Normal;
            _window.WindowStyle = WindowStyle.None;
            _window.ResizeMode = ResizeMode.NoResize;
            _window.MinWidth = 0;
            _window.MinHeight = 0;
            _window.Topmost = true;

            if (!SetWindowPos(
                    handle,
                    HwndTopmost,
                    monitorBounds.Left,
                    monitorBounds.Top,
                    monitorBounds.Width,
                    monitorBounds.Height,
                    SwpFrameChanged | SwpShowWindow))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "无法把窗口扩展到显示器的完整边界");
            }

            _window.Activate();
        }
        catch
        {
            RestoreWindow();
            throw;
        }
    }

    internal void Exit()
    {
        if (IsFullscreen)
        {
            RestoreWindow();
        }
    }

    /// <summary>
    /// 显示器分辨率变化、窗口被拖到另一块屏幕后，重新贴合当前显示器的物理边界。
    /// </summary>
    internal bool Reapply()
    {
        if (!IsFullscreen)
        {
            return false;
        }

        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var monitorBounds = GetMonitorBounds(handle);
            return SetWindowPos(
                handle,
                HwndTopmost,
                monitorBounds.Left,
                monitorBounds.Top,
                monitorBounds.Width,
                monitorBounds.Height,
                SwpFrameChanged | SwpShowWindow);
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private void RestoreWindow()
    {
        IsFullscreen = false;
        _window.WindowState = WindowState.Normal;
        _window.WindowStyle = _windowStyle;
        _window.ResizeMode = _resizeMode;
        _window.Topmost = _topmost;

        if (!double.IsNaN(_restoreBounds.Left) && !double.IsNaN(_restoreBounds.Top))
        {
            _window.Left = _restoreBounds.Left;
            _window.Top = _restoreBounds.Top;
        }

        if (_restoreBounds.Width > 0 && _restoreBounds.Height > 0)
        {
            _window.Width = _restoreBounds.Width;
            _window.Height = _restoreBounds.Height;
        }

        _window.MinWidth = _minWidth;
        _window.MinHeight = _minHeight;
        _window.WindowState = _windowState;
    }

    internal static WindowRect GetMonitorBounds(IntPtr window)
    {
        // 窗口还没创建时（构造阶段）按主显示器处理。
        var monitor = MonitorFromWindow(
            window,
            window == IntPtr.Zero ? MonitorDefaultToPrimary : MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法确定窗口所在显示器");
        }

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取显示器边界");
        }

        return new WindowRect(
            info.Monitor.Left,
            info.Monitor.Top,
            info.Monitor.Right,
            info.Monitor.Bottom);
    }

    internal static WindowRect GetWindowBounds(IntPtr window)
    {
        if (!GetWindowRect(window, out var rect))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取窗口边界");
        }

        return new WindowRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        internal int Size;
        internal NativeRect Monitor;
        internal NativeRect Work;
        internal uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }
}

internal readonly record struct WindowRect(int Left, int Top, int Right, int Bottom)
{
    internal int Width => Right - Left;
    internal int Height => Bottom - Top;
}
