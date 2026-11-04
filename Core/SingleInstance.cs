using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DeskTwin.Core;

internal enum SingleInstanceState
{
    /// <summary>拿到了唯一实例，可以继续启动。</summary>
    Acquired,

    /// <summary>已有实例在运行，已尝试把它唤到前台。</summary>
    AlreadyRunning,

    /// <summary>当前进程就在桌面分身（Child Session）里，不允许再开。</summary>
    InsideChildSession
}

/// <summary>
/// 单实例保护：整机只允许一个 DeskTwin（跨会话，用 Global 命名互斥体），
/// 并且拒绝在桌面分身内部再次启动，避免嵌套分身。
/// </summary>
internal static class SingleInstance
{
    // 固定名字：升级后新旧版本仍互斥（旧版本没有这段逻辑，用进程名兜底）。
    private const string MutexName = @"Global\DeskTwin.SingleInstance.8F2C1A44-0E31-4B77-9A55-6C2E7D3A1B90";
    private const string FallbackMutexName = @"Local\DeskTwin.SingleInstance.8F2C1A44-0E31-4B77-9A55-6C2E7D3A1B90";
    private const string ProcessName = "DeskTwin";

    private static Mutex? _mutex;

    /// <summary>
    /// 用来唤醒已有实例的自定义窗口消息。用 RegisterWindowMessage 保证两个进程拿到同一个 id，
    /// 比按进程名查找窗口可靠（用 dotnet 宿主启动时进程名并不是 DeskTwin）。
    /// </summary>
    internal static readonly uint ActivateMessage =
        RegisterWindowMessage("DeskTwin.ActivateInstance.8F2C1A44");

    internal static SingleInstanceState TryAcquire(out string message, out bool activated)
    {
        message = string.Empty;
        activated = false;

        if (IsInsideChildSession(out var sessionId))
        {
            message = $"当前已经在桌面分身里（会话 {sessionId}），不能再打开一个 DeskTwin，"
                      + "否则会形成嵌套分身。请回到主桌面运行。";
            return SingleInstanceState.InsideChildSession;
        }

        if (!TryHoldMutex())
        {
            // 先广播消息让已有实例自己跳到前台，再按进程名兜底找一次。
            BroadcastActivate();
            activated = ActivateExistingInstance();
            return SingleInstanceState.AlreadyRunning;
        }

        // 旧版本没有互斥体，按进程名兜底：避免升级后出现两个窗口。
        if (TryFindOtherInstance(out _))
        {
            BroadcastActivate();
            activated = ActivateExistingInstance();
            return SingleInstanceState.AlreadyRunning;
        }

        return SingleInstanceState.Acquired;
    }

    /// <summary>当前进程是否运行在 Windows Child Session（桌面分身）里。</summary>
    internal static bool IsInsideChildSession(out uint sessionId)
    {
        sessionId = 0;
        uint? childSessionId;
        try
        {
            childSessionId = ChildSessionNative.GetSessionId();
        }
        catch
        {
            // 查不到就当作不是分身，正常启动（真正的错误会在连接时提示）。
            return false;
        }

        if (!ProcessIdToSessionId((uint)Environment.ProcessId, out var current))
        {
            return false;
        }

        sessionId = current;
        return WouldBeNested(childSessionId, current);
    }

    /// <summary>纯判断：进程所在会话就是本机的 Child Session 时，说明已经在分身里。</summary>
    internal static bool WouldBeNested(uint? childSessionId, uint currentSessionId) =>
        childSessionId is { } child
        && child != 0
        && child == currentSessionId;

    /// <summary>自检用：只占用互斥体，不激活已有实例、也不看进程名。</summary>
    internal static bool TryHoldMutexForTest() => TryHoldMutex();

    private static bool TryHoldMutex()
    {
        if (_mutex is not null)
        {
            return true;
        }

        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            if (!createdNew)
            {
                _mutex.Dispose();
                _mutex = null;
                return false;
            }

            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // Global 命名空间被策略拒绝时退回 Local：至少保证同一会话内唯一。
        }
        catch (AbandonedMutexException)
        {
            // 上一个实例异常退出，互斥体已归我们所有。
            return true;
        }

        try
        {
            _mutex = new Mutex(initiallyOwned: true, FallbackMutexName, out var createdNew);
            if (!createdNew)
            {
                _mutex.Dispose();
                _mutex = null;
                return false;
            }

            return true;
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
        catch
        {
            // 实在拿不到互斥体就放行，由进程名检查兜底。
            return true;
        }
    }

    /// <summary>
    /// 找“真正在运行”的同类进程：必须有主窗口。
    /// 之前清理构建产物时留下过没有窗口的残留进程，若按进程名一律拦截会让应用再也启动不了；
    /// 并发竞态由互斥体负责，这里只看得到窗口的实例。
    /// </summary>
    private static bool TryFindOtherInstance(out int processId)
    {
        processId = 0;
        var current = Environment.ProcessId;
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(ProcessName);
        }
        catch
        {
            return false;
        }

        try
        {
            foreach (var process in processes)
            {
                if (process.Id == current || !HasMainWindow(process))
                {
                    continue;
                }

                processId = process.Id;
                return true;
            }

            return false;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static bool HasMainWindow(Process process)
    {
        if (process.MainWindowHandle != IntPtr.Zero)
        {
            return true;
        }

        process.Refresh();
        return process.MainWindowHandle != IntPtr.Zero;
    }

    /// <summary>向所有顶层窗口广播“请到前台”的消息。</summary>
    private static void BroadcastActivate()
    {
        if (ActivateMessage == 0)
        {
            return;
        }

        try
        {
            PostMessage(HwndBroadcast, ActivateMessage, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            // 广播失败就交给按进程名兜底。
        }
    }

    /// <summary>把已有实例的窗口唤醒并置前；失败返回 false。</summary>
    private static bool ActivateExistingInstance()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(ProcessName);
        }
        catch
        {
            return false;
        }

        var activated = false;
        try
        {
            foreach (var process in processes)
            {
                if (process.Id == Environment.ProcessId)
                {
                    continue;
                }

                var handle = process.MainWindowHandle;
                if (handle == IntPtr.Zero)
                {
                    process.Refresh();
                    handle = process.MainWindowHandle;
                }

                if (handle == IntPtr.Zero)
                {
                    continue;
                }

                if (IsIconic(handle))
                {
                    ShowWindow(handle, SwRestore);
                }
                else
                {
                    ShowWindow(handle, SwShow);
                }

                // 前台权限属于当前活动窗口，靠 SetForegroundWindow 可能被系统拒绝，
                // 这里顺带把窗口提到 Z 序顶端，至少不会被盖住。
                SetForegroundWindow(handle);
                BringWindowToTop(handle);
                activated = true;
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }

        return activated;
    }

    private const int HwndBroadcast = 0xFFFF;
    private const int SwShow = 5;
    private const int SwRestore = 9;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
