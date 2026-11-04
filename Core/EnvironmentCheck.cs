using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace DeskTwin.Core;

public sealed record EnvironmentIssue(string Title, string Message);

public static class EnvironmentCheck
{
    public static IReadOnlyList<EnvironmentIssue> Collect()
    {
        var issues = new List<EnvironmentIssue>();
        if (ChildSessionNative.IsRdpWrapperEnabled())
        {
            issues.Add(new EnvironmentIssue(
                "检测到 RDP Wrapper",
                "RDP Wrapper 与 Windows Child Session 可能不兼容。可以继续尝试，但连接失败时应先检查此项。"));
        }

        var policyValue = ReadDword(
            RegistryHive.LocalMachine,
            @"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services",
            "fDenyTSConnections");
        var localValue = policyValue is null
            ? ReadDword(RegistryHive.LocalMachine,
                @"SYSTEM\CurrentControlSet\Control\Terminal Server", "fDenyTSConnections")
            : null;
        if ((policyValue ?? localValue) == 1)
        {
            issues.Add(new EnvironmentIssue(
                "远程桌面主机已关闭",
                policyValue is not null
                    ? "组策略禁止了远程桌面连接。需要在远程桌面会话主机策略中允许远程连接。"
                    : "系统设置禁止了远程桌面连接。可以在管理员终端将 fDenyTSConnections 设为 0 并重启 TermService。"));
        }

        if (ReadDword(RegistryHive.LocalMachine,
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\PasswordLess\Device",
                "DevicePasswordLessBuildVersion") == 2)
        {
            issues.Add(new EnvironmentIssue(
                "仅允许 Windows Hello 登录",
                "此设置可能导致分身登录时密码始终无效。可在“设置 → 账户 → 登录选项”中关闭该限制，再注销或重启系统。"));
        }

        return issues;
    }

    public static string BuildSummary(IEnumerable<EnvironmentIssue> issues)
    {
        var builder = new StringBuilder();
        foreach (var issue in issues)
        {
            if (builder.Length > 0)
            {
                builder.AppendLine().AppendLine();
            }

            builder.Append('【').Append(issue.Title).Append('】').AppendLine().Append(issue.Message);
        }

        return builder.ToString();
    }

    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static int? ReadDword(RegistryHive hive, string path, string name)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(path);
            return key?.GetValue(name) as int?;
        }
        catch
        {
            return null;
        }
    }
}
