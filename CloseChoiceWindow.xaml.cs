using System.Windows;

namespace DeskTwin;

internal enum CloseChoice
{
    Cancel,
    Tray,
    ExitKeepSession,
    ExitAndEndSession
}

/// <summary>
/// 关闭窗口时的三选一：挂托盘 / 退出保留分身 / 退出并结束分身。
/// 用自定义卡片按钮而不是 MessageBox，是因为 Yes/No/Cancel 表达不了三种语义。
/// </summary>
public partial class CloseChoiceWindow : Window
{
    internal CloseChoice Choice { get; private set; } = CloseChoice.Cancel;

    internal CloseChoiceWindow()
    {
        InitializeComponent();
        TrayButton.Click += (_, _) => Pick(CloseChoice.Tray);
        KeepButton.Click += (_, _) => Pick(CloseChoice.ExitKeepSession);
        EndButton.Click += (_, _) => Pick(CloseChoice.ExitAndEndSession);
        CancelButton.Click += (_, _) => Pick(CloseChoice.Cancel);
        Loaded += (_, _) => TrayButton.Focus();
    }

    private void Pick(CloseChoice choice)
    {
        Choice = choice;
        DialogResult = choice != CloseChoice.Cancel;
    }
}
