using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DeskTwin.Core;

public static class SelfTest
{
    public static bool Run(string outputPath)
    {
        // 自检会创建并关闭窗口；默认的 OnLastWindowClose 会让 Application 进入关闭流程，
        // 之后再建窗口就会报“应用程序对象正在关闭”。整个自检期间改为显式关闭。
        if (System.Windows.Application.Current is { } application)
        {
            try
            {
                application.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
            }
            catch (InvalidOperationException)
            {
                // 已经在关闭流程里时无法修改，忽略即可。
            }
        }

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
        checks.Add(RunCheck("会话缩放参数一致", () =>
        {
            // DesktopScaleFactor 与 DeviceScaleFactor 必须相等。
            // DeviceScaleFactor 只允许 100/140/180，若只把 Desktop 设成 150，
            // 任务栏等系统元素会被非整数倍拉伸（图标模糊、大小不齐）。
            if (AdaptiveResolution.ToSessionScaleFactor(1.5) != 140)
            {
                return $"150% 显示器的会话缩放应为 140%，实际 {AdaptiveResolution.ToSessionScaleFactor(1.5)}%。";
            }

            if (AdaptiveResolution.ToSessionScaleFactor(1.0) != 100)
            {
                return "100% 显示器应得到 100%。";
            }

            if (AdaptiveResolution.ToSessionScaleFactor(2.0) != 180)
            {
                return "200% 显示器应得到 180%。";
            }

            var illegal = 0;
            foreach (var percent in new[] { 0, 96, 100, 125, 140, 150, 175, 180, 250 })
            {
                var normalized = AdaptiveResolution.NormalizeSessionScaleFactor(percent);
                if (normalized != 0 && normalized is not (100 or 140 or 180))
                {
                    illegal++;
                }
            }

            return illegal == 0 ? null : $"{illegal} 个缩放值落到了非法档位。";
        }));
        checks.Add(RunCheck("主窗口布局与分辨率菜单", () =>
        {
            // 构造主窗口即可验证 XAML 能否解析（历史上曾因资源类型错误在这里暴露）。
            // 不 Show/Close：在 OnStartup 里同步显示窗口会让 Application 进入关闭流程。
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

            var checkedItems = items.Where(item => item.IsChecked).ToList();
            if (checkedItems.Count != 1)
            {
                return "分辨率菜单没有唯一的勾选项。";
            }

            var parts = (checkedItems[0].Tag as string)?.Split('x');
            if (parts?.Length != 2
                || !int.TryParse(parts[0], out var targetWidth)
                || !int.TryParse(parts[1], out var targetHeight)
                || targetWidth <= 0
                || targetHeight <= 0)
            {
                return "分辨率菜单的勾选项没有有效的宽高。";
            }

            // 关键回归点：比例必须按**画面区**算。按窗口整体算会差一个标题栏高度，
            // 画面区就会比桌面更"扁"，等比缩放后左右各留一条黑边。
            var headerHeight = window.HeaderRow.Height.IsAbsolute
                ? window.HeaderRow.Height.Value
                : 24;
            if (headerHeight <= 0)
            {
                return "标题栏高度为 0，画面区会盖住标题栏。";
            }

            var reference = new System.Drawing.Size(targetWidth, targetHeight);
            var chrome = new System.Windows.Size(0, headerHeight);
            const double sampleWidth = 1180;
            var fitted = MainWindow.FitWindowToViewportAspect(
                sampleWidth,
                chrome,
                reference,
                new System.Windows.Size(320, 200));

            var desktopAspect = (double)targetWidth / targetHeight;
            var viewportAspect = (fitted.Width - chrome.Width) / (fitted.Height - chrome.Height);
            if (Math.Abs(viewportAspect - desktopAspect) > 0.005)
            {
                return $"缩放模式下画面区比例 {viewportAspect:0.0000} 未对齐分身比例 {desktopAspect:0.0000}，会留黑边。";
            }

            // 反向确认：如果按窗口整体对齐（旧做法），算出来的高度会明显偏小——
            // 用整窗口高度当画面区高度就是黑边的成因。
            var naiveHeight = sampleWidth / desktopAspect;
            if (Math.Abs(naiveHeight - fitted.Height) < headerHeight / 2)
            {
                return $"窗口高度 {fitted.Height:0.#} 与按整窗口比例算出的 {naiveHeight:0.#} 几乎相同，"
                       + "说明比例没有把标题栏算进去。";
            }

            // 画面区不得超过所选分辨率（DIP 换算）：超过就要 RDP 放大，而它只在缩小方向生效。
            var maxWidth = MainWindow.MaxViewportWidth(reference, 1.5);
            var expectedMax = targetWidth / 1.5;
            if (Math.Abs(maxWidth - expectedMax) > 0.01)
            {
                return $"画面区宽度上限计算错误：{maxWidth:0.##}（期望 {expectedMax:0.##}）。";
            }

            if (maxWidth >= targetWidth)
            {
                return "画面区上限没有除以 DPI，窗口会大于分辨率从而出现黑边。";
            }

            // 算出的窗口尺寸要真的能让画面铺满：修正后不应出现黑边。
            var fixedViewport = new System.Windows.Size(
                fitted.Width - chrome.Width,
                fitted.Height - chrome.Height);
            var fixedPresented = MainWindow.ComputePresentedSize(fixedViewport, reference);
            if (fixedPresented.Width < fixedViewport.Width - 1
                || fixedPresented.Height < fixedViewport.Height - 1)
            {
                return $"修正后画面 {fixedPresented.Width:0} × {fixedPresented.Height:0} 仍小于画面区 "
                       + $"{fixedViewport.Width:0} × {fixedViewport.Height:0}，还会留黑边。";
            }

            // 再用旧算法（窗口 1180×740，画面区只剩 716 高）验证它确实会留黑边，
            // 证明这条断言真的能抓到问题，不是空跑。
            var legacyViewport = new System.Windows.Size(1180, 740 - headerHeight);
            var legacyPresented = MainWindow.ComputePresentedSize(legacyViewport, reference);
            if (legacyViewport.Width - legacyPresented.Width <= 2)
            {
                return $"旧算法（窗口高度按整窗口比例算）在 {legacyViewport.Width:0} × {legacyViewport.Height:0} "
                       + "的画面区里没有产生黑边，说明这条回归断言失效了。";
            }

            return null;
        }));
        checks.Add(RunCheck("关闭窗口不抛“正在关闭”异常", () =>
        {
            // 回归用例：过去在 Closing 事件里同步调用 Close()，WPF 已把窗口标记为 closing，
            // 会抛“在窗口关闭期间，无法…调用 Show、ShowDialog、Close”，表现为点 ✕ 后弹错误框。
            MainWindow? window = null;
            try
            {
                window = new MainWindow
                {
                    WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
                    Left = -8000,
                    Top = -8000,
                    ShowInTaskbar = false,
                    SuppressDialogsForTest = true,
                    // 强制走“没有分身会话”的关闭路径：这台机器平时可能正开着分身，
                    // 否则会走“有会话→弹确认框”的分支，永远测不到真正出问题的那条路。
                    HasSessionForTest = false
                };
                var closed = false;
                Exception? failure = null;
                window.Closed += (_, _) => closed = true;
                window.Show();
                PumpDispatcher(window.Dispatcher, System.Windows.Threading.DispatcherPriority.Loaded);

                try
                {
                    window.Close();
                }
                catch (Exception exception)
                {
                    failure = exception;
                }

                for (var i = 0; i < 8 && !closed; i++)
                {
                    PumpDispatcher(window.Dispatcher, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                }

                if (failure is not null)
                {
                    return "关闭窗口抛异常：" + failure.GetBaseException().Message;
                }

                if (!closed)
                {
                    return "无会话时点关闭，窗口没有真正关闭（关闭流程被吞掉）。";
                }

                return null;
            }
            finally
            {
                if (window is { IsVisible: true })
                {
                    window.SuppressDialogsForTest = true;
                    window.Close();
                }
            }
        }));
        checks.Add(RunCheck("单实例与嵌套保护", () =>
        {
            // 1) 纯逻辑：进程所在会话 == 本机 Child Session 时才算“在分身里”。
            if (!SingleInstance.WouldBeNested(childSessionId: 2, currentSessionId: 2))
            {
                return "会话相同却没有判定为嵌套。";
            }

            if (SingleInstance.WouldBeNested(childSessionId: 2, currentSessionId: 1))
            {
                return "主会话被误判为嵌套（会挡住正常启动）。";
            }

            if (SingleInstance.WouldBeNested(childSessionId: null, currentSessionId: 1)
                || SingleInstance.WouldBeNested(childSessionId: 0, currentSessionId: 0))
            {
                return "没有 Child Session 时被误判为嵌套。";
            }

            // 2) 真机：先占住互斥体，再起一个子进程，它必须报告“已在运行”并退出。
            SingleInstance.TryHoldMutexForTest();
            var exitCode = ProbeSecondInstance();
            return exitCode == 4
                ? null
                : $"第二个实例没有被拦住（退出码 {exitCode}，期望 4）。";
        }));
        checks.Add(RunCheck("关窗后进程能自行退出", () =>
        {
            // 回归用例：窗口关掉、进程却还在（任务管理器里出现无窗口的 DeskTwin）。
            // 起一个子进程走真实启动路径，2 秒后自行关闭，必须在超时前以 0 退出。
            var exitCode = RunProbe("--probe-close-exit", 25000, out var timedOut);
            if (timedOut)
            {
                return "关窗后进程 25 秒仍未退出（残留进程）。";
            }

            return exitCode == 0 ? null : $"关窗后进程退出码异常：{exitCode}。";
        }));
        checks.Add(RunCheck("托盘图标与挂托盘", () =>
        {
            // 1) 托盘图标能用嵌入资源创建（单文件/自定义图标都走这条）。
            try
            {
                using var tray = new TrayIcon();
                tray.Visible = true;
                tray.Visible = false;
            }
            catch (Exception exception)
            {
                return "无法创建托盘图标：" + exception.GetBaseException().Message;
            }

            // 2) 有会话时关窗选择“挂托盘”：窗口隐藏但**不关闭**，之后能恢复。
            var window = new MainWindow
            {
                WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
                Left = -8000,
                Top = -8000,
                ShowInTaskbar = false,
                SuppressDialogsForTest = true,
                HasSessionForTest = true,
                ClosePromptResultForTest = System.Windows.MessageBoxResult.Yes
            };
            var closed = false;
            window.Closed += (_, _) => closed = true;
            try
            {
                window.Show();
                PumpDispatcher(window.Dispatcher, System.Windows.Threading.DispatcherPriority.Loaded);
                window.Close();
                PumpDispatcher(window.Dispatcher, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

                if (closed)
                {
                    return "选择挂托盘却把窗口关掉了。";
                }

                if (window.IsVisible)
                {
                    return "选择挂托盘后窗口仍然可见（没有隐藏到托盘）。";
                }

                window.RestoreFromTrayForTest();
                PumpDispatcher(window.Dispatcher, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                if (!window.IsVisible)
                {
                    return "从托盘恢复后窗口没有重新显示。";
                }

                return null;
            }
            finally
            {
                window.ExitForTest();
            }
        }));
        checks.Add(RunCheck("PrintScreen 钩子", () =>
        {
            // RDP 控件自己装了低级键盘钩子，会把 PrintScreen 转发进子会话；
            // 子会话里的截图工具（UWP）启动不了，用户只会看到“没有关联的应用”。
            // 这里验证我们的同级钩子能装起来、能收到并吞掉这个键。
            var pressed = 0;
            using var hook = new KeyboardHook { ShouldIntercept = () => true };
            hook.PrintScreenPressed += () => pressed++;
            if (!hook.Install())
            {
                return "无法安装键盘钩子（可能被安全软件拦截）。";
            }

            PumpDispatcher(
                System.Windows.Threading.Dispatcher.CurrentDispatcher,
                System.Windows.Threading.DispatcherPriority.Loaded);
            System.Threading.Thread.Sleep(200);

            KeybdEvent(VkSnapshot, 0, 0);
            KeybdEvent(VkSnapshot, 0, KeyEventKeyUp);
            for (var i = 0; i < 4 && pressed == 0; i++)
            {
                PumpDispatcher(
                    System.Windows.Threading.Dispatcher.CurrentDispatcher,
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                System.Threading.Thread.Sleep(80);
            }

            return pressed > 0 ? null : "钩子装上但没收到 PrintScreen。";
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
            Version = typeof(SelfTest).Assembly.GetName().Version?.ToString(3) ?? "1.4.4",
            TestedAt = DateTimeOffset.Now,
            Os = RuntimeInformation.OSDescription,
            IsAdministrator = EnvironmentCheck.IsAdministrator(),
            // 自检不具管理员权限时无法启用 Child Session，这里只如实记录状态，避免“全绿但根本不能用”。
            ChildSessionsEnabled = ChildSessionNative.IsEnabled(),
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

    /// <summary>起一个带指定参数的自检探针子进程，返回退出码；超时则 timedOut = true。</summary>
    private static int RunProbe(string argument, int timeoutMs, out bool timedOut)
    {
        timedOut = false;
        var host = Environment.ProcessPath;
        if (string.IsNullOrEmpty(host))
        {
            return -1;
        }

        var entryArgument = Environment.GetCommandLineArgs().FirstOrDefault();
        var arguments = host.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase)
                        && entryArgument?.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) == true
            ? $"\"{entryArgument}\" {argument}"
            : argument;

        var startInfo = new System.Diagnostics.ProcessStartInfo(host, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = System.Diagnostics.Process.Start(startInfo);
        if (process is null)
        {
            return -1;
        }

        process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(timeoutMs))
        {
            timedOut = true;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // 已经退出就忽略。
            }

            return -1;
        }

        return process.ExitCode;
    }

    /// <summary>启动一个 --probe-instance 子进程，返回它的退出码（4 = 已被占用）。</summary>
    private static int ProbeSecondInstance()
    {
        var host = Environment.ProcessPath;
        if (string.IsNullOrEmpty(host))
        {
            return -1;
        }

        // 不能用 Assembly.Location：单文件发布下它恒为空（IL3000）。
        // 命令行第 0 项在 dotnet 宿主下就是 dll 路径，在单文件下就是 exe 路径。
        var entryArgument = Environment.GetCommandLineArgs().FirstOrDefault();
        var arguments = host.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase)
                        && entryArgument?.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) == true
            ? $"\"{entryArgument}\" --probe-instance"
            : "--probe-instance";

        var startInfo = new System.Diagnostics.ProcessStartInfo(host, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = System.Diagnostics.Process.Start(startInfo);
        if (process is null)
        {
            return -1;
        }

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(20000);
        Console.WriteLine("probe-instance: " + output.Trim());
        return process.HasExited ? process.ExitCode : -2;
    }

    private const byte VkSnapshot = 0x2C;
    private const uint KeyEventKeyUp = 2;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    private static void KeybdEvent(byte virtualKey, byte scanCode, uint flags) =>
        keybd_event(virtualKey, scanCode, flags, UIntPtr.Zero);

    private static void PumpDispatcher(
        System.Windows.Threading.Dispatcher dispatcher,
        System.Windows.Threading.DispatcherPriority priority) =>
        dispatcher.Invoke(() => { }, priority);

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
