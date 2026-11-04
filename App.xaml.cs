using System.IO;
using System.Windows;
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
            var probeState = SingleInstance.TryAcquire(out var probeMessage, out _);
            Console.WriteLine($"{probeState}|{probeMessage}");
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
        var state = SingleInstance.TryAcquire(out var message, out var activated);
        if (state != SingleInstanceState.Acquired)
        {
            if (state == SingleInstanceState.AlreadyRunning && activated)
            {
                // 已把旧窗口唤到前台，静默退出，不再打扰用户。
                Environment.Exit(0);
                return;
            }

            if (!string.IsNullOrEmpty(message))
            {
                MessageBox.Show(
                    message,
                    state == SingleInstanceState.InsideChildSession ? "不能嵌套打开桌面分身" : "DeskTwin 已在运行",
                    MessageBoxButton.OK,
                    state == SingleInstanceState.InsideChildSession
                        ? MessageBoxImage.Warning
                        : MessageBoxImage.Information);
            }

            Environment.Exit(state == SingleInstanceState.InsideChildSession ? 3 : 0);
            return;
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
