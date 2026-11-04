using DrawingSize = System.Drawing.Size;

namespace DeskTwin.Core;

/// <summary>
/// 全屏时把远程会话分辨率对齐到显示器物理像素，并在连上之后按显示器 DPI
/// 指定会话缩放，避免"1920×1080 被拉伸到 2K/4K"造成的模糊和黑边。
/// </summary>
internal static class AdaptiveResolution
{
    // RDP ActiveX 允许的桌面尺寸范围（Windows 8 及以上）。
    internal const int MinDimension = 200;
    internal const int MaxDimension = 8192;

    // desktopscalefactor 的合法取值（.rdp 中的标准档位）。
    private static readonly int[] SupportedScaleFactors =
        [100, 125, 150, 175, 200, 250, 300, 400, 500];

    internal static DrawingSize Normalize(int width, int height) => new(
        Math.Clamp(width, MinDimension, MaxDimension),
        Math.Clamp(height, MinDimension, MaxDimension));

    internal static DrawingSize ForMonitor(WindowRect monitor) =>
        Normalize(monitor.Width, monitor.Height);

    /// <summary>把显示器 DPI 比例（1.0 = 100%）对齐到 RDP 支持的缩放档位。</summary>
    internal static int ToDesktopScaleFactor(double dpiScale)
    {
        if (double.IsNaN(dpiScale) || dpiScale <= 0)
        {
            return 100;
        }

        var percent = (int)Math.Round(dpiScale * 100, MidpointRounding.AwayFromZero);
        if (percent <= SupportedScaleFactors[0])
        {
            return SupportedScaleFactors[0];
        }

        if (percent >= SupportedScaleFactors[^1])
        {
            return SupportedScaleFactors[^1];
        }

        var best = SupportedScaleFactors[0];
        var bestDelta = int.MaxValue;
        foreach (var candidate in SupportedScaleFactors)
        {
            var delta = Math.Abs(candidate - percent);
            if (delta < bestDelta)
            {
                best = candidate;
                bestDelta = delta;
            }
        }

        return best;
    }

    /// <summary>deviceScaleFactor 只有 100 / 140 / 180 三个合法值。</summary>
    internal static int ToDeviceScaleFactor(int desktopScaleFactor) =>
        desktopScaleFactor >= 200 ? 180
        : desktopScaleFactor >= 150 ? 140
        : 100;

    /// <summary>
    /// 会话缩放必须让 DesktopScaleFactor 与 DeviceScaleFactor**完全一致**。
    /// DeviceScaleFactor 只允许 100/140/180，如果只把 DesktopScaleFactor 设成 150，
    /// Windows 会按 140% 计算 DPI 感知（任务栏图标、字体度量）却按 150% 渲染，
    /// 图标被非整数倍拉伸，看上去模糊、大小不齐、位置错位。
    /// </summary>
    internal static int ToSessionScaleFactor(double dpiScale) =>
        ToDeviceScaleFactor(ToDesktopScaleFactor(dpiScale));

    /// <summary>把用户指定的百分比收拢到 RDP 真正支持的档位，保证两个参数一致。</summary>
    internal static int NormalizeSessionScaleFactor(int percent) =>
        percent switch
        {
            <= 0 => 0,
            < 120 => 100,
            < 160 => 140,
            _ => 180
        };

    internal static bool IsUsable(DrawingSize size) =>
        size.Width is >= MinDimension and <= MaxDimension
        && size.Height is >= MinDimension and <= MaxDimension;

    /// <summary>
    /// 按参考分辨率的宽高比校正尺寸：keepWidth 时保持宽度算高度，否则保持高度算宽度。
    /// 缩放模式下用它把窗口锁成画面比例，避免四周留黑边。
    /// </summary>
    internal static DrawingSize FitToAspect(int width, int height, DrawingSize reference, bool keepWidth)
    {
        if (reference.Width <= 0 || reference.Height <= 0)
        {
            return new DrawingSize(Math.Max(1, width), Math.Max(1, height));
        }

        if (keepWidth)
        {
            var fixedWidth = Math.Max(1, width);
            return new DrawingSize(
                fixedWidth,
                Math.Max(1, (int)Math.Round((double)fixedWidth * reference.Height / reference.Width, MidpointRounding.AwayFromZero)));
        }

        var fixedHeight = Math.Max(1, height);
        return new DrawingSize(
            Math.Max(1, (int)Math.Round((double)fixedHeight * reference.Width / reference.Height, MidpointRounding.AwayFromZero)),
            fixedHeight);
    }
}
