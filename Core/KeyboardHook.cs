using System.Runtime.InteropServices;

namespace DeskTwin.Core;

/// <summary>
/// 低级键盘钩子，只用来抢 PrintScreen。
///
/// 背景：RDP 控件（mstscax）自身装了 WH_KEYBOARD_LL 钩子，会把 PrintScreen 转发进子会话，
/// 优先级高于 RegisterHotKey，所以热键方案抢不到这个键。子会话里截图工具是 UWP 应用、
/// 在 Child Session 中无法激活，用户按下后只会看到“该文件没有与之关联的应用”的报错。
/// 这里用同级钩子在本机截获并吞掉该按键。
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int VkSnapshot = 0x2C;

    private readonly LowLevelKeyboardProc _callback;
    private IntPtr _hook;
    private bool _disposed;

    /// <summary>是否要吞掉这次按键（通常判断前台窗口是不是我们自己的）。</summary>
    internal Func<bool>? ShouldIntercept { get; set; }

    internal event Action? PrintScreenPressed;

    internal KeyboardHook()
    {
        _callback = OnKeyboardMessage;
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
            _hook = SetWindowsHookEx(WhKeyboardLl, _callback, GetModuleHandle(null), 0);
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
            // 卸载失败也只能忽略，进程退出时系统会清理。
        }

        _hook = IntPtr.Zero;
    }

    private IntPtr OnKeyboardMessage(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code >= 0
                && (wParam == (IntPtr)WmKeyDown || wParam == (IntPtr)WmSysKeyDown))
            {
                var info = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
                if (info.VirtualKeyCode == VkSnapshot && (ShouldIntercept?.Invoke() ?? false))
                {
                    PrintScreenPressed?.Invoke();
                    // 返回 1 = 吞掉按键：RDP 收不到，也就不会转发进子会话。
                    return (IntPtr)1;
                }
            }
        }
        catch (Exception)
        {
            // 钩子回调里绝不能抛异常，否则会拖垮整个键盘输入链路。
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

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        internal uint VirtualKeyCode;
        internal uint ScanCode;
        internal uint Flags;
        internal uint Time;
        internal IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int hookId,
        LowLevelKeyboardProc callback,
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
