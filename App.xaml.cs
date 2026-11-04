using System.IO;
using System.Windows;
using System.Threading;
using System.Windows.Threading;
using DeskTwin.Core;
using MessageBox = System.Windows.MessageBox;

namespace DeskTwin;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var selfTestIndex = Array.FindIndex(e.Args, arg =>
            string.Equals(arg, "--self-test", StringComparison.OrdinalIgnoreCase));
        if (selfTestIndex >= 0)
        {
            var outputPath = selfTestIndex + 1 < e.Args.Length
                ? Path.GetFullPath(e.Args[selfTestIndex + 1])
                : Path.Combine(AppContext.BaseDirectory, "self-test.json");
            var passed = SelfTest.Run(outputPath);
            Environment.Exit(passed ? 0 : 2);
            return;
        }



        // 自检用：一个故意挂住、不创建窗口的子进程，用来验证探针的超时保护。
        if (e.Args.Any(arg => string.Equals(arg, "--probe-hang", StringComparison.OrdinalIgnoreCase)))
        {
            Thread.Sleep(5000);
            Environment.Exit(0);
            return;
        }

        // 自检用的诊断入口：走真实启动路径（StartupUri 创建的窗口），2 秒后关闭，
        // 用来验证“关窗后进程能不能自己退出”。能退出则进程以 0 结束，挂住则被外部超时杀掉。
        if (e.Args.Any(arg => string.Equals(arg, "--probe-close-exit", StringComparison.OrdinalIgnoreCase)))
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (MainWindow is not { } window)
                {
                    Environment.Exit(9);
                    return;
                }

                if (window is MainWindow main)
                {
                    main.SuppressDialogsForTest = true;
                    main.HasSessionForTest = false;
                }

                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    window.Close();
                };
                timer.Start();
            }), DispatcherPriority.ApplicationIdle);
            return;
        }

        // 自检用的探针：不弹任何框，只把单实例判定结果通过退出码返回。
        if (e.Args.Any(arg => string.Equals(arg, "--probe-instance", StringComparison.OrdinalIgnoreCase)))
        {
            var probeState = SingleInstance.TryAcquire(out var probeMessage, out var probeActivated);
            Console.WriteLine($"{probeState}|activated={probeActivated}|{probeMessage}");
            Environment.Exit(probeState switch
            {
                SingleInstanceState.Acquired => 0,
                SingleInstanceState.AlreadyRunning => 4,
                _ => 3
            });
            return;
        }

        // 整机只允许一个 DeskTwin；在桌面分身里再启动会被判定为嵌套并拒绝。
        // 必须在 StartupUri 创建主窗口之前判断，否则窗口已经出来了。
        var state = SingleInstance.TryAcquire(out _, out var activated);
        if (state == SingleInstanceState.InsideChildSession)
        {
            MessageBox.Show(
                "当前已经在桌面分身里，不能再打开一个 DeskTwin，否则会形成嵌套分身。请回到主桌面运行。",
                "不能嵌套打开桌面分身",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Environment.Exit(3);
            return;
        }

        if (state == SingleInstanceState.AlreadyRunning)
        {
            if (activated)
            {
                // 已把旧窗口唤到前台，静默退出，用户会看到那个窗口跳出来。
                Environment.Exit(0);
                return;
            }

            // 锁被占着，却找不到任何可见窗口：多半是上次异常退出留下的残留进程。
            // 以前这里静默退出，用户看到的就是“双击没反应/打不开”。
            var answer = MessageBox.Show(
                "已经有一个 DeskTwin 进程在后台运行，但它没有可见窗口（通常是上次没有正常退出留下的）。\n\n"
                + "是否结束这个残留进程并重新启动？",
                "DeskTwin 无法显示窗口",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.Yes);
            if (answer != MessageBoxResult.Yes)
            {
                Environment.Exit(0);
                return;
            }

            SingleInstance.KillStaleInstances();
            var retried = SingleInstanceState.AlreadyRunning;
            for (var attempt = 0; attempt < 25 && retried != SingleInstanceState.Acquired; attempt++)
            {
                Thread.Sleep(100);
                retried = SingleInstance.TryAcquire(out _, out _);
            }

            if (retried != SingleInstanceState.Acquired)
            {
                MessageBox.Show(
                    "仍然无法取得实例锁。请打开任务管理器结束所有 DeskTwin 进程后再试。",
                    "DeskTwin 启动失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Environment.Exit(0);
                return;
            }
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    private static void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            e.Exception.GetBaseException().Message,
            "DeskTwin 遇到错误",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
