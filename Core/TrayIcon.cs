using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace DeskTwin.Core;

/// <summary>
/// 系统托盘图标：分身会话还在运行时，关掉主窗口后挂在这里继续跑，
/// 恢复时不重建窗口（避免重复连接 RDP）。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _disconnectItem;
    private bool _disposed;

    /// <summary>最近一次设置可见性时的错误（用于自检/诊断）。</summary>
    internal static string? LastError { get; private set; }

    internal event Action? OpenRequested;
    internal event Action? DisconnectRequested;
    internal event Action? EndSessionRequested;
    internal event Action? ExitRequested;

    internal TrayIcon()
    {
        _disconnectItem = new Forms.ToolStripMenuItem("断开画面");
        _disconnectItem.Click += (_, _) => DisconnectRequested?.Invoke();

        var openItem = new Forms.ToolStripMenuItem("打开桌面分身");
        openItem.Click += (_, _) => OpenRequested?.Invoke();

        var endItem = new Forms.ToolStripMenuItem("结束分身并退出");
        endItem.Click += (_, _) => EndSessionRequested?.Invoke();

        var exitItem = new Forms.ToolStripMenuItem("退出（保留分身）");
        exitItem.Click += (_, _) => ExitRequested?.Invoke();

        var menu = new Forms.ContextMenuStrip { ShowImageMargin = false };
        menu.Items.Add(openItem);
        menu.Items.Add(_disconnectItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(endItem);
        menu.Items.Add(exitItem);

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "桌面分身",
            Visible = false,
            ContextMenuStrip = menu
        };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
    }

    internal bool Visible
    {
        get => _icon.Visible;
        set
        {
            try
            {
                _icon.Visible = value;
            }
            catch (Exception exception)
            {
                // 资源管理器重启等情况下设置可见性可能失败，忽略即可，但要留下痕迹。
                LastError = exception.GetBaseException().Message;
            }
        }
    }

    internal string ToolTip
    {
        set => _icon.Text = value.Length > 63 ? value[..63] : value;
    }

    internal void SetConnected(bool connected) => _disconnectItem.Enabled = connected;

    /// <summary>挂托盘时提示一次：Windows 11 默认把新图标收进 ^ 折叠区，用户容易找不到。</summary>
    internal void ShowHint(string title, string text)
    {
        try
        {
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = text;
            _icon.BalloonTipIcon = Forms.ToolTipIcon.Info;
            _icon.ShowBalloonTip(5000);
        }
        catch (Exception)
        {
            // 通知被系统策略禁用时不值得打断用户。
        }
    }

    private static Icon LoadIcon()
    {
        // 优先用嵌入资源：单文件发布或 dotnet 宿主下 ProcessPath 并不可靠。
        try
        {
            var resource = System.Windows.Application.GetResourceStream(
                new Uri("app.ico", UriKind.Relative));
            if (resource is not null)
            {
                using var stream = resource.Stream;
                return new Icon(stream);
            }
        }
        catch (Exception)
        {
            // 退回可执行文件图标。
        }

        try
        {
            if (Environment.ProcessPath is { } path)
            {
                return Icon.ExtractAssociatedIcon(path) ?? SystemIcons.Application;
            }
        }
        catch (Exception)
        {
            // 再退回系统默认图标。
        }

        return SystemIcons.Application;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _icon.Visible = false;
            _icon.Dispose();
        }
        catch (Exception)
        {
            // 退出时不必因为托盘清理失败而报错。
        }
    }
}
