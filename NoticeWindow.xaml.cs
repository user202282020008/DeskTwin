using System.Windows;

namespace DeskTwin;

/// <summary>
/// 带“不再显示此提示”的确认框，用于可抑制的启动风险提示。
/// </summary>
public partial class NoticeWindow : Window
{
    internal NoticeWindow(string title, string message)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        ContinueButton.Click += (_, _) => DialogResult = true;
        CancelButton.Click += (_, _) => DialogResult = false;
        Loaded += (_, _) => ContinueButton.Focus();
    }

    internal bool DontShowAgain => DontShowCheck.IsChecked == true;
}
