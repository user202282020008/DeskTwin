using System.Runtime.InteropServices;

namespace DeskTwin.Core;

/// <summary>
/// 低级鼠标钩子，只用来在全屏时抢右键。
///
/// 全屏后画面区整个是 RDP 的 ActiveX 控件（WinForms 子窗口，存在 airspace 问题），
/// WPF 根本收不到右键事件，顶部那条用来挂右键菜单的栏也已经被隐藏了。
/// 这里用 WH_MOUSE_LL 在系统层截获右键，命中则弹出本应用的菜单并吞掉事件。
/// </summary>
internal sealed class MouseHook : IDisposable
{
    private const int WhMouseLl = 14;
    private const int WmRightButtonDown = 0x0204;

    private readonly LowLevelMouseProc _callback;
    private IntPtr _hook;
    private bool _disposed;

    /// <summary>屏幕坐标是否属于需要接管的范围（返回 true 则吞掉这次右键）。</summary>
    internal Func<int, int, bool>? ShouldIntercept { get; set; }

    internal event Action<int, int>? RightButtonPressed;

    internal MouseHook()
    {
        _callback = OnMouseMessage;
    }

    internal bool IsInstalled => _hook != IntPtr.Zero;

    internal bool Install()
    {
        if (_hook != IntPtr.Zero)
        {
            return true;
        }

        try
        {
            _hook = SetWindowsHookEx(WhMouseLl, _callback, GetModuleHandle(null), 0);
        }
        catch (Exception)
        {
            _hook = IntPtr.Zero;
        }

        return _hook != IntPtr.Zero;
    }

    internal void Uninstall()
    {
        if (_hook == IntPtr.Zero)
        {
            return;
        }

        try
        {
            UnhookWindowsHookEx(_hook);
        }
        catch (Exception)
        {
            // 卸载失败交给进程退出时系统清理。
        }

        _hook = IntPtr.Zero;
    }

    private IntPtr OnMouseMessage(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            // 鼠标钩子回调频率很高（移动也算），这里只在右键按下时做少量工作。
            if (code >= 0
                && wParam == (IntPtr)WmRightButtonDown
                && ShouldIntercept is { } filter)
            {
                var info = Marshal.PtrToStructure<MsllHookStruct>(lParam);
                if (filter(info.Point.X, info.Point.Y))
                {
                    RightButtonPressed?.Invoke(info.Point.X, info.Point.Y);
                    // 吞掉这次右键，避免同时传到远程桌面里弹它的右键菜单。
                    return (IntPtr)1;
                }
            }
        }
        catch (Exception)
        {
            // 钩子回调里绝不能抛异常。
        }

        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Uninstall();
    }

    private delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsllHookStruct
    {
        internal Point Point;
        internal uint MouseData;
        internal uint Flags;
        internal uint Time;
        internal IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int hookId,
        LowLevelMouseProc callback,
        IntPtr module,
        uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
