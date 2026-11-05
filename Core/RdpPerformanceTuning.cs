using System.IO;
using System.Security;
using Microsoft.Win32;

namespace DeskTwin.Core;

internal readonly record struct RdpTuningRollbackReport(
    bool HardwareEncodeRemoved,
    bool FrameIntervalRemoved);

/// <summary>
/// 撤回 1.5.1 曾强制写入的两项全局图形策略。
/// 1.5.2 不再修改硬件编码或帧率设置，仅清理该版本写过的精确值。
/// </summary>
internal static class RdpPerformanceTuning
{
    private const string PolicyKeyPath =
        @"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services";

    private const string WinStationsKeyPath =
        @"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations";

    internal const int LegacyHardwareEncodeValue = 1;
    internal const int LegacyFrameIntervalValue = 15;

    private static bool _attempted;

    /// <summary>整个进程只尝试一次；非管理员或值不存在时不做任何改动。</summary>
    internal static RdpTuningRollbackReport EnsureLegacySettingsReverted()
    {
        if (_attempted)
        {
            return default;
        }

        _attempted = true;
        return RevertLegacySettings(EnvironmentCheck.IsAdministrator(), RemoveLegacyValue);
    }

    /// <summary>
    /// 独立的分派层让无管理员自检也能验证键路径、值名、管理员门禁和两项都执行。
    /// </summary>
    internal static RdpTuningRollbackReport RevertLegacySettings(
        bool isAdministrator,
        Func<string, string, int, bool> removeValue)
    {
        if (!isAdministrator)
        {
            return default;
        }

        var hardware = removeValue(
            PolicyKeyPath,
            "AVCHardwareEncodePreferred",
            LegacyHardwareEncodeValue);
        var frame = removeValue(
            WinStationsKeyPath,
            "DWMFRAMEINTERVAL",
            LegacyFrameIntervalValue);
        return new RdpTuningRollbackReport(hardware, frame);
    }

    /// <summary>只匹配旧版本写过的值，保留管理员设置的其他值。</summary>
    internal static bool ShouldRemoveLegacyValue(int? current, int legacyValue) =>
        current == legacyValue;

    private static bool RemoveLegacyValue(string path, string name, int legacyValue)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path, writable: true);
            if (key?.GetValue(name) is not int current
                || !ShouldRemoveLegacyValue(current, legacyValue))
            {
                return false;
            }

            key.DeleteValue(name, throwOnMissingValue: false);
            return true;
        }
        catch (Exception exception) when (exception is SecurityException
                                          or UnauthorizedAccessException
                                          or IOException
                                          or NotSupportedException)
        {
            return false;
        }
    }
}
