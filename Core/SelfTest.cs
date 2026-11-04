using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DeskTwin.Core;

public static class SelfTest
{
    public static bool Run(string outputPath)
    {
        var checks = new List<SelfTestResult>();
        checks.Add(RunCheck("Windows x64", () =>
            Environment.Is64BitOperatingSystem ? null : "需要 64 位 Windows。"));
        checks.Add(RunCheck("wtsapi32.dll", () =>
        {
            if (!NativeLibrary.TryLoad("wtsapi32.dll", out var library))
            {
                return "无法加载 wtsapi32.dll。";
            }

            try
            {
                return NativeLibrary.TryGetExport(library, "WTSEnableChildSessions", out _)
                    && NativeLibrary.TryGetExport(library, "WTSGetChildSessionId", out _)
                    ? null
                    : "系统缺少 Child Session API。";
            }
            finally
            {
                NativeLibrary.Free(library);
            }
        }));
        checks.Add(RunCheck("RDP ActiveX", () =>
        {
            var type = Type.GetTypeFromCLSID(new Guid("A0C63C30-F08D-4AB4-907C-34905D770C7D"))
                ?? throw new InvalidOperationException("MsRdpClient10 未注册。");
            var instance = Activator.CreateInstance(type);
            if (instance is not null && Marshal.IsComObject(instance))
            {
                Marshal.FinalReleaseComObject(instance);
            }

            return null;
        }));
        checks.Add(RunCheck("RDP 凭据接口", RdpClientHost.ValidateCredentialInterface));
        checks.Add(RunCheck("DPAPI 凭据加密", () =>
        {
            const string plaintext = "DeskTwin-SelfTest-Credential";
            var encrypted = CredentialStore.Protect(plaintext);
            if (string.IsNullOrWhiteSpace(encrypted) || encrypted.Contains(plaintext, StringComparison.Ordinal))
            {
                return "DPAPI 加密结果不正确。";
            }

            if (CredentialStore.Unprotect(encrypted) != plaintext)
            {
                return "DPAPI 解密结果与原文不一致。";
            }

            var probePath = Path.Combine(
                Path.GetDirectoryName(outputPath)!,
                "credential-probe.dat");
            try
            {
                CredentialStore.SaveTo(probePath, @"SELFTEST\account", plaintext);
                if (File.ReadAllText(probePath).Contains(plaintext, StringComparison.Ordinal))
                {
                    return "磁盘上出现了明文密码。";
                }

                var loaded = CredentialStore.LoadFrom(probePath);
                return loaded is not null
                       && loaded.UserName == @"SELFTEST\account"
                       && loaded.Password == plaintext
                    ? null
                    : "凭据文件保存或读取结果不一致。";
            }
            finally
            {
                CredentialStore.DeleteFile(probePath);
            }
        }));
        checks.Add(RunCheck("Child Session 状态", () =>
        {
            _ = ChildSessionNative.IsEnabled();
            _ = ChildSessionNative.GetSessionId();
            return null;
        }));
        checks.Add(RunCheck("WPF 样式资源", ValidateStyleResources));
        checks.Add(RunCheck("真全屏与退出控件", ValidateFullscreenBounds));
        checks.Add(RunCheck("全屏自适应分辨率", ValidateAdaptiveResolution));
        checks.Add(RunCheck("主窗口布局与分辨率菜单", () =>
        {
            // 构造主窗口即可验证 XAML 能否解析（历史上曾因资源类型错误在这里暴露）；
            // 注意不要 Show/Close，否则会走到真实的退出确认流程。
            var window = new MainWindow();
            window.UpdateLayout();
            if (window.Width < 800 || window.Height < 500)
            {
                return $"主窗口尺寸异常：{window.Width} × {window.Height}";
            }

            var monitor = AdaptiveResolution.ForMonitor(
                FullscreenController.GetMonitorBounds(IntPtr.Zero));
            var expectedTag = $"{monitor.Width}x{monitor.Height}";
            var items = window.ResolutionMenu.Items
                .Cast<System.Windows.Controls.MenuItem>()
                .ToList();
            var nativeItem = items.FirstOrDefault(item =>
                string.Equals(item.Tag as string, expectedTag, StringComparison.Ordinal));
            if (nativeItem is null)
            {
                var tags = string.Join(", ", items.Select(item => item.Tag as string));
                return $"分辨率菜单中没有本机分辨率 {monitor.Width} × {monitor.Height}（当前列表：{tags}）。";
            }

            if (nativeItem.Header?.ToString()?.Contains("本机", StringComparison.Ordinal) != true)
            {
                return "本机分辨率没有标注“（本机）”。";
            }

            if (items.Count(item => item.IsChecked) != 1)
            {
                return "分辨率菜单没有唯一的勾选项。";
            }

            // 缩放模式应把窗口锁成分身分辨率的宽高比（否则画面四周会留黑边）。
            var scaled = items.First(item => item.IsChecked).Tag as string;
            var parts = scaled?.Split('x');
            if (parts?.Length == 2
                && int.TryParse(parts[0], out var targetWidth)
                && int.TryParse(parts[1], out var targetHeight)
                && targetWidth > 0
                && targetHeight > 0)
            {
                window.SetResolutionModeForTest(fitToWindow: false);
                var windowAspect = window.Width / window.Height;
                var desktopAspect = (double)targetWidth / targetHeight;
                if (Math.Abs(windowAspect - desktopAspect) > 0.02)
                {
                    return $"缩放模式窗口比例 {windowAspect:0.000} 未对齐画面比例 {desktopAspect:0.000}，会留黑边。";
                }
            }

            return null;
        }));
        checks.Add(RunCheck("提示窗口布局", () =>
        {
            var window = new NoticeWindow("自检提示", "用于验证“不再显示此提示”对话框能否正常创建。");
            window.Close();
            return null;
        }));
        checks.Add(RunCheck("凭据窗口布局", () =>
        {
            var window = new CredentialWindow(Environment.UserName);
            window.Close();
            return null;
        }));
        checks.Add(RunCheck("静音走本地音频会话", () =>
        {
            // 用一段静音 WAV 让本进程产生音频会话（不会真的出声），再验证能否设置静音。
            try
            {
                using var silence = CreateSilentWav();
                using var player = new System.Media.SoundPlayer(silence);
                player.Play();
                System.Threading.Thread.Sleep(600);
            }
            catch (Exception exception)
            {
                return "无法创建测试音频会话：" + exception.GetBaseException().Message;
            }

            if (!AudioSessionMute.TrySetMute(true, out var muteError))
            {
                return muteError is not null && muteError.Contains("播放设备", StringComparison.Ordinal)
                    ? null
                    : "无法静音本进程音频会话：" + (muteError ?? "未知原因");
            }

            return AudioSessionMute.TrySetMute(false, out var unmuteError)
                ? null
                : "无法取消静音：" + (unmuteError ?? "未知原因");
        }));
        checks.Add(RunCheck("配置读写", () =>
        {
            var directory = Path.GetDirectoryName(outputPath)!;
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, "settings-probe.json");
            var settings = new DesktopSettings();
            File.WriteAllText(probe, JsonSerializer.Serialize(settings));
            var restored = JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(probe));
            File.Delete(probe);
            return restored?.DesktopWidth == 1920 && restored.DesktopHeight == 1080
                ? null
                : "配置序列化结果不正确。";
        }));

        var issues = EnvironmentCheck.Collect();
        var report = new
        {
            Application = "DeskTwin",
            Version = typeof(SelfTest).Assembly.GetName().Version?.ToString(3) ?? "1.3.5",
            TestedAt = DateTimeOffset.Now,
            Os = RuntimeInformation.OSDescription,
            IsAdministrator = EnvironmentCheck.IsAdministrator(),
            Checks = checks,
            EnvironmentIssues = issues,
            Passed = checks.All(check => check.Passed)
        };
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
        return report.Passed;
    }

    private static string? ValidateFullscreenBounds()
    {
        var probe = new System.Windows.Window
        {
            Title = "DeskTwin Fullscreen SelfTest",
            Width = 740,
            Height = 520,
            Left = 60,
            Top = 60,
            WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
            WindowStyle = System.Windows.WindowStyle.SingleBorderWindow,
            ResizeMode = System.Windows.ResizeMode.CanResize,
            Topmost = false,
            ShowInTaskbar = false
        };
        var controller = new FullscreenController(probe);

        try
        {
            probe.Show();
            var handle = new System.Windows.Interop.WindowInteropHelper(probe).Handle;
            controller.Enter();
            probe.UpdateLayout();

            var monitor = FullscreenController.GetMonitorBounds(handle);
            var actual = FullscreenController.GetWindowBounds(handle);
            if (actual != monitor)
            {
                return $"窗口边界 {actual} 未覆盖显示器完整边界 {monitor}。";
            }

            if (probe.WindowState != System.Windows.WindowState.Normal
                || probe.WindowStyle != System.Windows.WindowStyle.None
                || !probe.Topmost)
            {
                return "全屏窗口样式、状态或置顶属性不正确。";
            }

            var source = System.Windows.Interop.HwndSource.FromHwnd(handle);
            var transform = source?.CompositionTarget?.TransformFromDevice
                            ?? System.Windows.Media.Matrix.Identity;
            var exitWindow = new FullscreenExitWindow(probe, monitor, transform);
            try
            {
                exitWindow.Show();
                if (!exitWindow.IsVisible || !exitWindow.Topmost)
                {
                    return "全屏退出控件未能显示或未置顶。";
                }

                exitWindow.ApplyAutoHide(true, animate: false);
                if (!exitWindow.IsAutoHidden || exitWindow.Opacity > 0.01 || exitWindow.IsHitTestVisible)
                {
                    return "全屏退出控件没有按预期自动隐藏。";
                }

                exitWindow.ApplyAutoHide(false, animate: false);
                if (exitWindow.IsAutoHidden || exitWindow.Opacity < 0.99 || !exitWindow.IsHitTestVisible)
                {
                    return "自动隐藏后的退出控件无法恢复显示。";
                }

                // 热区判据：鼠标不在右上角、且初始展示时间已过 → 隐藏；进入热区 → 立刻恢复。
                var later = DateTime.UtcNow.AddMinutes(1);
                exitWindow.EvaluateForTest(insideHotZone: false, later);
                if (!exitWindow.IsAutoHidden || exitWindow.Opacity > 0.01 || exitWindow.IsHitTestVisible)
                {
                    return "鼠标离开热区后退出控件没有隐藏。";
                }

                exitWindow.EvaluateForTest(insideHotZone: true, later);
                if (exitWindow.IsAutoHidden || exitWindow.Opacity < 0.99 || !exitWindow.IsHitTestVisible)
                {
                    return "鼠标进入热区后退出控件没有恢复显示。";
                }
            }
            finally
            {
                exitWindow.Close();
            }

            controller.Exit();
            probe.UpdateLayout();
            if (controller.IsFullscreen
                || probe.WindowStyle != System.Windows.WindowStyle.SingleBorderWindow
                || probe.ResizeMode != System.Windows.ResizeMode.CanResize
                || probe.Topmost)
            {
                return "退出全屏后没有恢复原窗口属性。";
            }

            return null;
        }
        finally
        {
            controller.Exit();
            probe.Close();
        }
    }

    private static string? ValidateAdaptiveResolution()
    {
        var monitor = new WindowRect(0, 0, 2560, 1440);
        if (AdaptiveResolution.ForMonitor(monitor) != new System.Drawing.Size(2560, 1440))
        {
            return "显示器分辨率没有转换为桌面尺寸。";
        }

        if (AdaptiveResolution.Normalize(120, 9000) != new System.Drawing.Size(200, 8192))
        {
            return "分辨率没有按 RDP 允许范围裁剪。";
        }

        if (AdaptiveResolution.ToDesktopScaleFactor(1.5) != 150
            || AdaptiveResolution.ToDesktopScaleFactor(1.0) != 100
            || AdaptiveResolution.ToDesktopScaleFactor(1.4) != 150
            || AdaptiveResolution.ToDesktopScaleFactor(3.0) != 300)
        {
            return "DPI 比例没有对齐到 RDP 支持的缩放档位。";
        }

        if (AdaptiveResolution.ToDeviceScaleFactor(150) != 140
            || AdaptiveResolution.ToDeviceScaleFactor(200) != 180
            || AdaptiveResolution.ToDeviceScaleFactor(100) != 100)
        {
            return "设备缩放比例映射不正确。";
        }

        var reference = new System.Drawing.Size(1920, 1080);
        var keepWidth = AdaptiveResolution.FitToAspect(1000, 500, reference, keepWidth: true);
        if (keepWidth != new System.Drawing.Size(1000, 563))
        {
            return $"保持宽度时未按画面比例校正：{keepWidth.Width} × {keepWidth.Height}。";
        }

        var keepHeight = AdaptiveResolution.FitToAspect(1000, 500, reference, keepWidth: false);
        if (keepHeight != new System.Drawing.Size(889, 500))
        {
            return $"保持高度时未按画面比例校正：{keepHeight.Width} × {keepHeight.Height}。";
        }

        var settings = new DesktopSettings();
        return settings.FitToWindow ? null : "默认分辨率模式应为自适应。";
    }

    /// <summary>生成 0.25 秒静音 PCM WAV，仅用于制造音频会话。</summary>
    private static Stream CreateSilentWav()
    {
        const int sampleRate = 8000;
        const short bits = 16;
        var dataLength = sampleRate * bits / 8 / 4;
        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataLength);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(sampleRate);
            writer.Write(sampleRate * (short)(bits / 8));
            writer.Write((short)(bits / 8));
            writer.Write(bits);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            writer.Write(dataLength);
            writer.Write(new byte[dataLength]);
        }

        stream.Position = 0;
        return stream;
    }

    private static string? ValidateStyleResources()
    {
        var resources = System.Windows.Application.Current?.Resources;
        if (resources is null)
        {
            return "WPF Application 资源未加载。";
        }

        foreach (System.Collections.DictionaryEntry entry in resources)
        {
            if (entry.Value is not System.Windows.Style style)
            {
                continue;
            }

            if (FindInvalidBrushSetter(style.Setters) is { } setterError)
            {
                return $"{entry.Key}：{setterError}";
            }

            foreach (var triggerBase in style.Triggers)
            {
                if (triggerBase is System.Windows.Trigger trigger
                    && FindInvalidBrushSetter(trigger.Setters) is { } triggerError)
                {
                    return $"{entry.Key} 触发器：{triggerError}";
                }
            }
        }

        return null;
    }

    private static string? FindInvalidBrushSetter(System.Windows.SetterBaseCollection setters)
    {
        foreach (var setterBase in setters)
        {
            if (setterBase is not System.Windows.Setter setter
                || setter.Property is not System.Windows.DependencyProperty property
                || property.PropertyType != typeof(System.Windows.Media.Brush)
                || setter.Value is System.Windows.Media.Brush
                || setter.Value is string
                || setter.Value is System.Windows.Markup.MarkupExtension)
            {
                continue;
            }

            return $"{property.Name} 的值类型是 {setter.Value?.GetType().Name ?? "null"}，应为 Brush。";
        }

        return null;
    }

    private static SelfTestResult RunCheck(string name, Func<string?> check)
    {
        try
        {
            var error = check();
            return new SelfTestResult(name, error is null, error);
        }
        catch (Exception exception)
        {
            return new SelfTestResult(name, false, exception.GetBaseException().Message);
        }
    }

    public sealed record SelfTestResult(string Name, bool Passed, string? Error);
}
