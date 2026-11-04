using System.Windows;
using DeskTwin.Core;
using MessageBox = System.Windows.MessageBox;

namespace DeskTwin;

public partial class CredentialWindow : Window
{
    public CredentialWindow(string defaultUserName)
    {
        InitializeComponent();
        UserNameBox.Text = defaultUserName;

        var saved = CredentialStore.Load();
        if (saved is not null)
        {
            UserNameBox.Text = saved.UserName;
            PasswordBox.Password = saved.Password;
            RememberCheck.IsChecked = true;
            SavedHint.Text = "已载入保存的凭据。";
        }
        else
        {
            RememberCheck.IsChecked = true;
            SavedHint.Text = "尚未保存登录凭据。";
        }

        Loaded += (_, _) =>
        {
            UserNameBox.Focus();
            UserNameBox.SelectAll();
        };
    }

    public CredentialInput? Result { get; private set; }

    public bool CredentialsCleared { get; private set; }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var userName = UserNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(userName))
        {
            MessageBox.Show("请输入 Windows 登录账户。", "登录凭据", MessageBoxButton.OK, MessageBoxImage.Warning);
            UserNameBox.Focus();
            return;
        }

        var password = PasswordBox.Password;
        var remember = RememberCheck.IsChecked == true;
        if (remember && string.IsNullOrEmpty(password))
        {
            MessageBox.Show("勾选“记住密码”时必须填写密码。", "登录凭据", MessageBoxButton.OK, MessageBoxImage.Warning);
            PasswordBox.Focus();
            return;
        }

        try
        {
            if (remember)
            {
                CredentialStore.Save(userName, password);
            }
            else
            {
                CredentialStore.Delete();
            }

            Result = new CredentialInput(userName, password, remember);
            DialogResult = true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                "无法保存登录凭据：" + exception.GetBaseException().Message,
                "登录凭据",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        try
        {
            CredentialStore.Delete();
            PasswordBox.Clear();
            RememberCheck.IsChecked = false;
            SavedHint.Text = "已清除保存的登录凭据。";
            Result = null;
            CredentialsCleared = true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                "无法清除登录凭据：" + exception.GetBaseException().Message,
                "登录凭据",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
