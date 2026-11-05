using System.IO;
using System.Security;
using Microsoft.Win32;

namespace DeskTwin.Core;

/// <summary>远程会话的两项编码/帧率策略状态。</summary>
internal sealed record RdpTuningReport(
    bool HardwareEncodeEnabled,
    bool FrameIntervalRaised,
    bool Changed,
    string? Error)
{
    internal string Describe()
    {
        var encode = HardwareEncodeEnabled ? "AVC 硬件编码已启用" : "AVC 软件编码";
        var frame = FrameIntervalRaised ? "帧率上限 60fps" : "帧率上限 30fps";
        var suffix = Changed ? "（本次已写入）" : string.Empty;
        return Error is null
            ? $"{encode} · {frame}{suffix}"
            : $"{encode} · {frame} · {Error}";
    }
}

/// <summary>
/// 提升本机 RDP 会话（含 Child Session）的实际帧率与拖动跟手度。
/// 两项都是 Windows 官方支持的策略值，不是猜测：
///
/// 1) AVCHardwareEncodePreferred = 1
///    官方说明：未配置时“we will always use software encoding”。2560×1600 的窗口拖动
///    每帧都有大面积像素变化，纯 CPU 编码会成为瓶颈；开启后优先用 GPU 编码
///    （Intel QSV / NVENC），出错会自动回退软件编码，不会连不上。
///
/// 2) DWMFRAMEINTERVAL = 15
///    微软 KB2885213：RDP 远程会话默认把帧率限制在 30fps，这个值把上限提高到约 60fps。
///    需要重启系统才生效。
/// </summary>
internal static class RdpPerformanceTuning
{
    private const string PolicyKeyPath =
        @"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services";

    private const string WinStationsKeyPath =
        @"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations";

    internal const string HardwareEncodeValueName = "AVCHardwareEncodePreferred";
    internal const string FrameIntervalValueName = "DWMFRAMEINTERVAL";

    /// <summary>帧间隔 15ms ≈ 60fps（微软 KB2885213 给出的值）。</summary>
    internal const int FrameIntervalFor60Fps = 15;

    private static RdpTuningReport? _cached;

    /// <summary>整个进程只执行一次；非管理员时写入失败但不影响启动。</summary>
    internal static RdpTuningReport EnsureApplied() => _cached ??= Apply();

    internal static RdpTuningReport Apply()
    {
        var changed = false;
        string? error = null;

        var hardware = ReadDword(PolicyKeyPath, HardwareEncodeValueName) == 1;
        if (!hardware)
        {
            if (TryWriteDword(PolicyKeyPath, HardwareEncodeValueName, 1, out var hardwareError))
            {
                hardware = true;
                changed = true;
            }
            else
            {
                error = hardwareError;
            }
        }

        var frame = IsFrameIntervalSufficient(
            ReadDword(WinStationsKeyPath, FrameIntervalValueName));
        if (!frame)
        {
            if (TryWriteDword(
                    WinStationsKeyPath,
                    FrameIntervalValueName,
                    FrameIntervalFor60Fps,
                    out var frameError))
            {
                frame = true;
                changed = true;
            }
            else
            {
                error ??= frameError;
            }
        }

        return new RdpTuningReport(hardware, frame, changed, error);
    }

    /// <summary>
    /// 帧间隔越小帧率越高；未设置或大于 15ms 都需要写入。
    /// 用户手动调得更激进（比如 10ms）时保留原值，不覆盖。
    /// </summary>
    internal static bool IsFrameIntervalSufficient(int? current) =>
        current is > 0 and <= FrameIntervalFor60Fps;

    internal static int? ReadDword(string path, string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            return key?.GetValue(name) is int value ? value : null;
        }
        catch (Exception exception) when (exception is SecurityException
                                          or UnauthorizedAccessException
                                          or IOException)
        {
            return null;
        }
    }

    private static bool TryWriteDword(string path, string name, int value, out string? error)
    {
        error = null;
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(path, writable: true);
            if (key is null)
            {
                error = "无法打开注册表项";
                return false;
            }

            key.SetValue(name, value, RegistryValueKind.DWord);
            return true;
        }
        catch (Exception exception) when (exception is SecurityException
                                          or UnauthorizedAccessException
                                          or IOException
                                          or NotSupportedException)
        {
            error = exception.Message;
            return false;
        }
    }
}
