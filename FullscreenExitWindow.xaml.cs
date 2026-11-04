using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DeskTwin.Core;
using WpfPoint = System.Windows.Point;

namespace DeskTwin;

/// <summary>
/// 全屏时悬浮在右上角的“退出全屏”按钮。行为参照 mstsc 的连接条：
/// 进入全屏先显示 3 秒，之后只有把鼠标移到右上角热区才出现，离开约 0.8 秒自动淡出。
/// 只用光标位置判断，不用“系统空闲时间”——在分身里一直在操作键鼠，
/// 空闲时间永远是 0，按空闲判断就再也不会隐藏。
/// </summary>
public partial class FullscreenExitWindow : Window
{
    private static readonly TimeSpan InitialShow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan HideDelay = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan HideAnimation = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan ShowAnimation = TimeSpan.FromMilliseconds(110);
    private const int HotZonePadding = 80;

    private readonly DispatcherTimer _cursorTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(150)
    };

    private bool _autoHidden;
    private DateTime _visibleUntil = DateTime.UtcNow + InitialShow;
    private IntPtr _handle;

    internal event EventHandler? ExitRequested;

    internal FullscreenExitWindow(
        Window owner,
        WindowRect monitorBounds,
        Matrix transformFromDevice)
    {
        InitializeComponent();
        Owner = owner;

        var topLeft = transformFromDevice.Transform(
            new WpfPoint(monitorBounds.Left, monitorBounds.Top));
        var topRight = transformFromDevice.Transform(
            new WpfPoint(monitorBounds.Right, monitorBounds.Top));
        Left = topRight.X - Width - 16;
        Top = topLeft.Y + 12;

        Loaded += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            _visibleUntil = DateTime.UtcNow + InitialShow;
        };
        _cursorTimer.Tick += (_, _) => PollCursor();
        _cursorTimer.Start();
        Closed += (_, _) => _cursorTimer.Stop();
    }

    /// <summary>当前是否处于自动隐藏状态（自检用）。</summary>
    internal bool IsAutoHidden => _autoHidden;

    /// <summary>切换自动隐藏状态；隐藏后不再接受点击。</summary>
    internal void ApplyAutoHide(bool hidden, bool animate)
    {
        if (_autoHidden == hidden)
        {
            return;
        }

        _autoHidden = hidden;
        IsHitTestVisible = !hidden;
        var target = hidden ? 0d : 1d;
        if (!animate)
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = target;
            return;
        }

        var animation = new DoubleAnimation(target, hidden ? HideAnimation : ShowAnimation);
        animation.Completed += (_, _) =>
        {
            // 远程会话里动画偶发不刷新，结束时兜底写入最终值。
            BeginAnimation(OpacityProperty, null);
            Opacity = target;
        };
        BeginAnimation(OpacityProperty, animation);
    }

    /// <summary>自检用：直接按“是否在热区内”和当前时间推进一次判定。</summary>
    internal void EvaluateForTest(bool insideHotZone, DateTime now) =>
        Evaluate(insideHotZone, now, animate: false);

    private void PollCursor()
    {
        if (_handle == IntPtr.Zero)
        {
            _handle = new WindowInteropHelper(this).Handle;
            if (_handle == IntPtr.Zero)
            {
                return;
            }
        }

        if (!GetCursorPos(out var cursor) || !GetWindowRect(_handle, out var bounds))
        {
            return;
        }

        var inside = cursor.X >= bounds.Left - HotZonePadding
                     && cursor.X <= bounds.Right + HotZonePadding
                     && cursor.Y >= bounds.Top - HotZonePadding
                     && cursor.Y <= bounds.Bottom + HotZonePadding;
        Evaluate(inside, DateTime.UtcNow);
    }

    private void Evaluate(bool insideHotZone, DateTime now, bool animate = true)
    {
        if (insideHotZone)
        {
            _visibleUntil = now + HideDelay;
            ApplyAutoHide(false, animate);
            return;
        }

        if (now >= _visibleUntil)
        {
            ApplyAutoHide(true, animate);
        }
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X;
        internal int Y;
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
