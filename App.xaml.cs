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
