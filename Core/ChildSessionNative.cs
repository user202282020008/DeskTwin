using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace DeskTwin.Core;

internal static class ChildSessionNative
{
    private const int ErrorNotFound = 1168;
    private const uint NoChildSessionId = uint.MaxValue;
    private const int DefaultRdpPort = 3389;

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnableChildSessions([MarshalAs(UnmanagedType.Bool)] bool enable);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSIsChildSessionsEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSGetChildSessionId(out uint sessionId);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSLogoffSession(IntPtr server, uint sessionId, [MarshalAs(UnmanagedType.Bool)] bool wait);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parent, EnumChildWindowCallback callback, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximumLength);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(IntPtr parent, IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnableWindow(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool enabled);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    private const uint GaRoot = 2;

    /// <summary>给定窗口是否属于（或就是）我们自己的顶层窗口。</summary>
    public static bool IsOurWindowInForeground(IntPtr ourWindow)
    {
        if (ourWindow == IntPtr.Zero)
        {
            return false;
        }

        var foreground = GetForegroundWindow();
        return foreground != IntPtr.Zero && GetAncestor(foreground, GaRoot) == ourWindow;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);

    private const uint ModNoRepeat = 0x4000;
    private const uint VirtualKeyPrintScreen = 0x2C;

    /// <summary>
    /// 接管 PrintScreen：RDP 会把该键转发给子会话，导致本机截图失效。
    /// 仅在 DeskTwin 是前台窗口时注册。
    /// </summary>
    public static bool RegisterScreenshotHotKey(IntPtr window, int id) =>
        RegisterHotKey(window, id, ModNoRepeat, VirtualKeyPrintScreen);

    public static void UnregisterScreenshotHotKey(IntPtr window, int id) =>
        UnregisterHotKey(window, id);

    public static bool IsEnabled()
    {
        if (!WTSIsChildSessionsEnabled(out var enabled))
        {
            throw LastError("无法读取 Child Session 状态");
        }

        return enabled;
    }

    public static void EnsureEnabled()
    {
        if (!IsEnabled() && !WTSEnableChildSessions(true))
        {
            throw LastError("无法启用 Windows Child Session");
        }
    }

    public static uint? GetSessionId()
    {
        if (WTSGetChildSessionId(out var sessionId))
        {
            return sessionId == NoChildSessionId ? null : sessionId;
        }

        var error = Marshal.GetLastWin32Error();
        if (error == ErrorNotFound)
        {
            return null;
        }

        throw new Win32Exception(error, $"无法取得桌面分身会话 ID（Win32 错误 {error}）");
    }

    public static uint? Logoff(uint? expectedSessionId = null, bool wait = true)
    {
        var sessionId = GetSessionId();
        if (sessionId is null)
        {
            return null;
        }

        if (expectedSessionId is not null && expectedSessionId != sessionId)
        {
            throw new InvalidOperationException(
                $"分身会话已从 {expectedSessionId} 变为 {sessionId}，已取消注销。");
        }

        if (!WTSLogoffSession(IntPtr.Zero, sessionId.Value, wait))
        {
            throw LastError($"无法注销桌面分身会话 {sessionId}");
        }

        return sessionId;
    }

    public static int GetRdpPort()
    {
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = machine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp");
            return key?.GetValue("PortNumber") is int port and > 0 and <= ushort.MaxValue
                ? port
                : DefaultRdpPort;
        }
        catch
        {
            return DefaultRdpPort;
        }
    }

    public static bool IsRdpWrapperEnabled()
    {
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = machine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\TermService\Parameters");
            return (key?.GetValue("ServiceDll") as string)?.Contains(
                "rdpwrap.dll", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch
        {
            return false;
        }
    }

    public static void SetInputBlocked(IntPtr hostWindow, bool blocked)
    {
        var inputWindow = FindInputWindow(hostWindow);
        if (inputWindow != IntPtr.Zero)
        {
            EnableWindow(inputWindow, !blocked);
        }
    }

    public static bool FocusInputWindow(IntPtr hostWindow)
    {
        var inputWindow = FindInputWindow(hostWindow);
        if (inputWindow == IntPtr.Zero)
        {
            return false;
        }

        SetFocus(inputWindow);
        return GetFocus() == inputWindow;
    }

    private static IntPtr FindInputWindow(IntPtr hostWindow)
    {
        var result = IntPtr.Zero;
        if (hostWindow == IntPtr.Zero)
        {
            return result;
        }

        EnumChildWindows(hostWindow, (window, _) =>
        {
            if (!IsWindow(window) || !IsChild(hostWindow, window))
            {
                return true;
            }

            var text = new StringBuilder(256);
            GetWindowText(window, text, text.Capacity);
            if (!string.Equals(text.ToString(), "Input Capture Window", StringComparison.Ordinal))
            {
                return true;
            }

            result = window;
            return false;
        }, IntPtr.Zero);
        return result;
    }

    public static int GetConfiguredRdpPort() => GetRdpPort();

    public static void ClearRdpInputWindowCache(IntPtr hostWindow)
    {
        // DeskTwin 每次按 HWND 枚举，无需保留窗口缓存。
    }

    public static void SetRdpInputWindowEnabled(IntPtr hostWindow, bool enabled) =>
        SetInputBlocked(hostWindow, !enabled);

    public static bool TryFocusRdpInputWindow(IntPtr hostWindow) => FocusInputWindow(hostWindow);

    public static bool IsRdpInputWindowFocused(IntPtr hostWindow)
    {
        var inputWindow = FindInputWindow(hostWindow);
        return inputWindow != IntPtr.Zero && GetFocus() == inputWindow;
    }

    private static Win32Exception LastError(string operation)
    {
        var error = Marshal.GetLastWin32Error();
        return new Win32Exception(error, $"{operation}（Win32 错误 {error}）");
    }

    private delegate bool EnumChildWindowCallback(IntPtr window, IntPtr parameter);
}
