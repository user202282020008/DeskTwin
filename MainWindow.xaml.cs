using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DeskTwin.Core;
using DrawingSize = System.Drawing.Size;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace DeskTwin;

public partial class MainWindow : Window
{
    private readonly RdpClientHost _rdp = new();
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly DispatcherTimer _resizeFitTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _aspectTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _applyingAspect;
    private DesktopSettings _settings = new();
    private bool _suppressEvents = true;
    private bool _connecting;
    private bool _busy;
    private bool _preflightAcknowledged;
    private bool _closeInProgress;
    private bool _closeApproved;
    private CredentialInput? _sessionCredential;
    private bool _credentialDialogOpen;
    private readonly FullscreenController _fullscreen;
    private FullscreenExitWindow? _fullscreenExitWindow;
    private IntPtr _windowHandle;
    private HwndSource? _windowSource;
    private bool _screenshotHotKeyRegistered;
    private const int WmDisplayChange = 0x007E;
    private const int WmSizing = 0x0214;
    private const int WmSysCommand = 0x0112;
    private const int WmHotKey = 0x0312;
    private const int ScMaximize = 0xF030;
    private const int ScreenshotHotKeyId = 0x4455;
    private const int MinSizingWidth = 640;
    private DrawingSize _activeDesktopSize = new(1920, 1080);
    private DrawingSize? _resolutionOptionsMonitor;

    public MainWindow()
    {
        InitializeComponent();
        _fullscreen = new FullscreenController(this);
        _settings = SettingsService.Load();
        _activeDesktopSize = new DrawingSize(_settings.DesktopWidth, _settings.DesktopHeight);
        RdpFormsHost.Child = _rdp;
        _rdp.LoginCompleted += OnLoginCompleted;
        _rdp.ConnectionFailed += OnConnectionFailed;
        _rdp.FullscreenRequested += (_, _) => EnterFullscreen();
        _rdp.FullscreenExitRequested += (_, _) => ExitFullscreen();

        StartButton.Click += async (_, _) => await StartAsync();
        OverlayStartButton.Click += async (_, _) => await StartAsync();
        DisconnectMenuItem.Click += (_, _) => Disconnect();
        MinimizeButton.Click += (_, _) => WindowState = WindowState.Minimized;
        MaximizeButton.Click += (_, _) => ToggleFullscreen();
        CloseButton.Click += (_, _) => Close();
        EndMenuItem.Click += async (_, _) =>
        {
            try
            {
                await EndSessionAsync(true);
            }
            catch
            {
                // 错误已在 EndSessionAsync 中展示，保持应用运行。
            }
        };
        LaunchMenuItem.Click += async (_, _) => await LaunchExecutableAsync();
        CredentialsMenuItem.Click += (_, _) => ShowCredentialsDialog();
        TaskViewMenuItem.Click += (_, _) => SendShortcut(_rdp.SendTaskViewShortcut, "Win+Tab");
        ShowDesktopMenuItem.Click += (_, _) => SendShortcut(_rdp.SendShowDesktopShortcut, "Win+D");
        ScreenshotMenuItem.Click += (_, _) => CaptureScreenshot();
        AdaptiveModeMenuItem.Click += OnResolutionModeChanged;
        ScaledModeMenuItem.Click += OnResolutionModeChanged;
        WatchMenuItem.Click += OnWatchToggled;
        MuteMenuItem.Click += OnMuteToggled;
        TopmostMenuItem.Click += OnTopmostToggled;
        ShortcutsMenuItem.Click += OnShortcutsToggled;
        StartupWarningMenuItem.Click += OnStartupWarningToggled;
        PreflightMenuItem.Click += (_, _) => ShowEnvironmentReport();
        HelpMenuItem.Click += (_, _) => ShowHelp();
        AboutMenuItem.Click += (_, _) => ShowAbout();

        Closing += OnWindowClosing;
        Closed += OnWindowClosed;
        PreviewKeyDown += OnPreviewKeyDown;
        DpiChanged += (_, _) => OnDisplayGeometryChanged();
        StateChanged += (_, _) => OnWindowStateChanged();
        Activated += (_, _) => UpdateGlobalHotKeys();
        Deactivated += (_, _) => UpdateGlobalHotKeys();
        ViewportContainer.SizeChanged += (_, _) =>
        {
            UpdateViewportLayout();
            ScheduleFitSync();
        };
        SizeChanged += (_, _) => ScheduleAspectFix();
        _aspectTimer.Tick += (_, _) =>
        {
            _aspectTimer.Stop();
            ApplyAspectToWindow();
        };
        _statusTimer.Tick += (_, _) => RefreshState();
        _resizeFitTimer.Tick += (_, _) =>
        {
            _resizeFitTimer.Stop();
            SyncDesktopSize("窗口尺寸变化");
        };

        Loaded += (_, _) =>
        {
            UpdateViewportLayout();
            ApplyAspectToWindow();
        };
        ApplySettingsToControls();
        UpdateCredentialIndicator();
        using var identity = WindowsIdentity.GetCurrent();
        HeaderBar.ToolTip = $"账户：{identity.Name}";
        _suppressEvents = false;
        _statusTimer.Start();
        RefreshState();
    }

    private void ApplySettingsToControls()
    {
        AdaptiveModeMenuItem.IsChecked = _settings.FitToWindow;
        ScaledModeMenuItem.IsChecked = !_settings.FitToWindow;
        WatchMenuItem.IsChecked = _settings.WatchMode;
        MuteMenuItem.IsChecked = _settings.AudioMuted;
        TopmostMenuItem.IsChecked = _settings.Topmost;
        ShortcutsMenuItem.IsChecked = _settings.SendSystemShortcuts;
        StartupWarningMenuItem.IsChecked = !_settings.SkipStartupWarning;
        Topmost = _settings.Topmost;
        // 缩放模式靠 SmartSizing 做等比例缩放；自适应模式下尺寸本来就一致，开启只用于吸收 1px 取整误差。
        _rdp.SetSmartSizing(true);
        _rdp.SetSendSystemShortcutsToRemote(_settings.SendSystemShortcuts);
        _rdp.SetAudioMuted(_settings.AudioMuted);
        RefreshResolutionOptions();
        UpdateViewportLayout();
        ApplyAspectToWindow();
    }

    /// <summary>自检用：切换分辨率模式并立即应用（不会写配置）。</summary>
    internal void SetResolutionModeForTest(bool fitToWindow)
    {
        _settings.FitToWindow = fitToWindow;
        ApplySettingsToControls();
    }

    /// <summary>
    /// 缩放模式下把窗口锁成分身分辨率的宽高比，画面正好铺满、四周没有黑边。
    /// </summary>
    private void ApplyAspectToWindow()
    {
        if (_settings.FitToWindow || _fullscreen.IsFullscreen)
        {
            return;
        }

        var reference = new DrawingSize(_settings.DesktopWidth, _settings.DesktopHeight);
        if (!AdaptiveResolution.IsUsable(reference))
        {
            return;
        }

        // 最大化会让窗口比例和画面不一致（出现黑边），这里退回普通窗口并按比例铺到工作区。
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            if (GetCurrentWorkAreaDips() is { } work)
            {
                var fitted = FitInside(work.Width - 16, work.Height - 16, reference);
                SetWindowRect(
                    work.X + (work.Width - fitted.Width) / 2,
                    work.Y + (work.Height - fitted.Height) / 2,
                    fitted.Width,
                    fitted.Height);
                return;
            }
        }

        var width = double.IsNaN(Width) || Width <= 0 ? ActualWidth : Width;
        if (width <= 0)
        {
            return;
        }

        var height = width * reference.Height / reference.Width;
        if (height < MinHeight)
        {
            height = MinHeight;
            width = height * reference.Width / reference.Height;
        }

        if (Math.Abs(Height - height) < 0.5 && Math.Abs(Width - width) < 0.5)
        {
            return;
        }

        SetWindowRect(Left, Top, width, height);
    }

    private void SetWindowRect(double left, double top, double width, double height)
    {
        _applyingAspect = true;
        try
        {
            if (!double.IsNaN(left) && !double.IsNaN(top))
            {
                Left = left;
                Top = top;
            }

            Width = width;
            Height = height;
        }
        finally
        {
            _applyingAspect = false;
        }
    }

    /// <summary>在给定盒子内放下指定比例的矩形（先试按宽、不行再按高）。</summary>
    private static DrawingSize FitInside(double boxWidth, double boxHeight, DrawingSize reference)
    {
        var byWidth = AdaptiveResolution.FitToAspect(
            (int)Math.Max(1, boxWidth), (int)Math.Max(1, boxHeight), reference, keepWidth: true);
        return byWidth.Height <= boxHeight
            ? byWidth
            : AdaptiveResolution.FitToAspect(
                (int)Math.Max(1, boxWidth), (int)Math.Max(1, boxHeight), reference, keepWidth: false);
    }

    /// <summary>当前窗口所在显示器的工作区（DIP）。</summary>
    private Rect? GetCurrentWorkAreaDips()
    {
        var handle = _windowHandle != IntPtr.Zero
            ? _windowHandle
            : new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var screen = System.Windows.Forms.Screen.FromHandle(handle);
        var scale = VisualTreeHelper.GetDpi(this);
        var area = screen.WorkingArea;
        return new Rect(
            area.X / scale.DpiScaleX,
            area.Y / scale.DpiScaleY,
            area.Width / scale.DpiScaleX,
            area.Height / scale.DpiScaleY);
    }

    /// <summary>窗口尺寸变化后（拖拽、贴边、程序改动）延时把比例拉回画面比例。</summary>
    private void ScheduleAspectFix()
    {
        if (_settings.FitToWindow || _fullscreen.IsFullscreen || _applyingAspect)
        {
            return;
        }

        _aspectTimer.Stop();
        _aspectTimer.Start();
    }

    /// <summary>
    /// 拖动边框时按画面比例校正建议尺寸（WM_SIZING），保证任何时刻都没有黑边。
    /// </summary>
    private bool EnforceAspectWhileSizing(int edge, IntPtr lParam)
    {
        var reference = new DrawingSize(_settings.DesktopWidth, _settings.DesktopHeight);
        if (!AdaptiveResolution.IsUsable(reference))
        {
            return false;
        }

        var rect = Marshal.PtrToStructure<NativeRect>(lParam);
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        // 1 左 2 右 3 上 6 下 4 左上 5 右上 7 左下 8 右下
        var keepWidth = edge is not (3 or 6);
        var size = AdaptiveResolution.FitToAspect(width, height, reference, keepWidth);
        if (size.Width < MinSizingWidth)
        {
            size = AdaptiveResolution.FitToAspect(MinSizingWidth, size.Height, reference, keepWidth: true);
        }

        switch (edge)
        {
            case 1:
                rect.Left = rect.Right - size.Width;
                rect.Bottom = rect.Top + size.Height;
                break;
            case 2:
                rect.Right = rect.Left + size.Width;
                rect.Bottom = rect.Top + size.Height;
                break;
            case 3:
                rect.Top = rect.Bottom - size.Height;
                rect.Right = rect.Left + size.Width;
                break;
            case 6:
                rect.Bottom = rect.Top + size.Height;
                rect.Right = rect.Left + size.Width;
                break;
            case 4:
                rect.Left = rect.Right - size.Width;
                rect.Top = rect.Bottom - size.Height;
                break;
            case 5:
                rect.Right = rect.Left + size.Width;
                rect.Top = rect.Bottom - size.Height;
                break;
            case 7:
                rect.Left = rect.Right - size.Width;
                rect.Bottom = rect.Top + size.Height;
                break;
            default:
                rect.Right = rect.Left + size.Width;
                rect.Bottom = rect.Top + size.Height;
                break;
        }

        Marshal.StructureToPtr(rect, lParam, false);
        return true;
    }

    /// <summary>
    /// 分辨率子菜单：常用档位 + 当前显示器实际分辨率 + 用户已选值。
    /// </summary>
    internal void RefreshResolutionOptions()
    {
        var wasSuppressed = _suppressEvents;
        _suppressEvents = true;
        try
        {
            var native = GetCurrentMonitorSize();
            _resolutionOptionsMonitor = native;

            var entries = new List<(int Width, int Height, string Label)>
            {
                (1280, 720, "1280 × 720"),
                (1600, 900, "1600 × 900"),
                (1920, 1080, "1920 × 1080"),
                (2560, 1440, "2560 × 1440")
            };

            if (native is { } monitor)
            {
                var index = entries.FindIndex(entry =>
                    entry.Width == monitor.Width && entry.Height == monitor.Height);
                var label = $"{monitor.Width} × {monitor.Height}（本机）";
                if (index >= 0)
                {
                    entries[index] = (monitor.Width, monitor.Height, label);
                }
                else
                {
                    entries.Add((monitor.Width, monitor.Height, label));
                }
            }

            if (entries.TrueForAll(entry => entry.Width != _settings.DesktopWidth
                                            || entry.Height != _settings.DesktopHeight))
            {
                entries.Add((_settings.DesktopWidth, _settings.DesktopHeight,
                    $"{_settings.DesktopWidth} × {_settings.DesktopHeight}（自定义）"));
            }

            entries.Sort((left, right) =>
                (left.Width * left.Height).CompareTo(right.Width * right.Height));

            ResolutionMenu.Items.Clear();
            foreach (var entry in entries)
            {
                var item = new MenuItem
                {
                    Header = entry.Label,
                    Tag = $"{entry.Width}x{entry.Height}",
                    IsCheckable = true,
                    IsChecked = entry.Width == _settings.DesktopWidth
                                && entry.Height == _settings.DesktopHeight
                };
                item.Click += OnResolutionMenuItemClick;
                ResolutionMenu.Items.Add(item);
            }
        }
        finally
        {
            _suppressEvents = wasSuppressed;
        }
    }

    private void OnResolutionMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || sender is not MenuItem item)
        {
            return;
        }

        var parts = (item.Tag as string)?.Split('x');
        if (parts?.Length != 2
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height))
        {
            return;
        }

        if (_settings.DesktopWidth == width && _settings.DesktopHeight == height)
        {
            RefreshResolutionOptions();
            return;
        }

        _settings.DesktopWidth = width;
        _settings.DesktopHeight = height;
        var switchToScaled = _settings.FitToWindow;
        _settings.FitToWindow = false;
        ApplySettingsToControls();
        SaveSettings();
        SyncDesktopSize($"分辨率已设为 {width} × {height}");
        SetStatus(switchToScaled
            ? $"已固定为 {width} × {height}，并切换到缩放模式（等比例显示，不改变分辨率）"
            : $"分辨率已设为 {width} × {height}");
    }

    private void OnResolutionModeChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        var adaptive = ReferenceEquals(sender, AdaptiveModeMenuItem);
        if (_settings.FitToWindow == adaptive)
        {
            ApplySettingsToControls();
            return;
        }

        _settings.FitToWindow = adaptive;
        ApplySettingsToControls();
        SaveSettings();
        SyncDesktopSize(adaptive ? "已切换为自适应模式" : "已切换为缩放模式");
        SetStatus(adaptive
            ? "自适应：分身分辨率随窗口/全屏大小改变，点对点显示。"
            : $"缩放：固定 {_settings.DesktopWidth} × {_settings.DesktopHeight}，窗口内等比例缩放（四周留黑边，不变形）。");
    }

    /// <summary>
    /// 缩放模式下把画面控件按分身分辨率等比缩放并居中（黑边在四周），
    /// 自适应模式下直接铺满，由分辨率跟随保证 1:1。
    /// </summary>
    private void UpdateViewportLayout()
    {
        if (_settings.FitToWindow)
        {
            RdpFormsHost.ClearValue(FrameworkElement.WidthProperty);
            RdpFormsHost.ClearValue(FrameworkElement.HeightProperty);
            RdpFormsHost.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
            RdpFormsHost.VerticalAlignment = System.Windows.VerticalAlignment.Stretch;
            return;
        }

        var viewWidth = ViewportContainer.ActualWidth;
        var viewHeight = ViewportContainer.ActualHeight;
        if (viewWidth <= 0
            || viewHeight <= 0
            || _activeDesktopSize.Width <= 0
            || _activeDesktopSize.Height <= 0)
        {
            return;
        }

        // 窗口比例已经校正过时直接铺满，避免取整后残留一条黑边。
        var viewAspect = viewWidth / viewHeight;
        var desktopAspect = (double)_activeDesktopSize.Width / _activeDesktopSize.Height;
        if (Math.Abs(viewAspect - desktopAspect) < 0.01)
        {
            RdpFormsHost.ClearValue(FrameworkElement.WidthProperty);
            RdpFormsHost.ClearValue(FrameworkElement.HeightProperty);
            RdpFormsHost.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
            RdpFormsHost.VerticalAlignment = System.Windows.VerticalAlignment.Stretch;
            return;
        }

        var scale = Math.Min(
            viewWidth / _activeDesktopSize.Width,
            viewHeight / _activeDesktopSize.Height);
        RdpFormsHost.Width = Math.Max(1, Math.Round(_activeDesktopSize.Width * scale));
        RdpFormsHost.Height = Math.Max(1, Math.Round(_activeDesktopSize.Height * scale));
        RdpFormsHost.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        RdpFormsHost.VerticalAlignment = System.Windows.VerticalAlignment.Center;
    }

    private DrawingSize? GetCurrentMonitorSize()
    {
        try
        {
            return AdaptiveResolution.ForMonitor(
                FullscreenController.GetMonitorBounds(_windowHandle));
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>画面区当前大小（物理像素），用于自适应模式下跟随窗口。</summary>
    private DrawingSize? GetViewportSize()
    {
        var width = ViewportContainer.ActualWidth;
        var height = ViewportContainer.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        return AdaptiveResolution.Normalize(
            (int)Math.Round(width * dpi.DpiScaleX),
            (int)Math.Round(height * dpi.DpiScaleY));
    }

    /// <summary>窗口拖到另一块屏幕或显示器分辨率变化后，及时更新可选分辨率。</summary>
    private void RefreshResolutionOptionsIfMonitorChanged()
    {
        var current = GetCurrentMonitorSize();
        if (current is null || current == _resolutionOptionsMonitor)
        {
            return;
        }

        RefreshResolutionOptions();
    }

    private async Task StartAsync()
    {
        if (_busy || _connecting || _rdp.ConnectedState == 1)
        {
            return;
        }

        if (!_preflightAcknowledged && !_settings.SkipStartupWarning)
        {
            var issues = EnvironmentCheck.Collect();
            if (issues.Count > 0)
            {
                var notice = new NoticeWindow(
                    "启动前环境检测",
                    EnvironmentCheck.BuildSummary(issues)
                    + "\n\n这些只是风险提示，可以继续启动。勾选“不再显示此提示”后，以后启动将直接跳过此窗口。")
                {
                    Owner = this
                };
                var accepted = notice.ShowDialog() == true;
                if (notice.DontShowAgain)
                {
                    _settings.SkipStartupWarning = true;
                    StartupWarningMenuItem.IsChecked = false;
                    SaveSettings();
                }

                if (!accepted)
                {
                    return;
                }
            }

            _preflightAcknowledged = true;
        }

        _busy = true;
        _connecting = true;
        SetStatus("正在启用 Child Session 并建立本机 RDP 连接…");
        RefreshState();
        try
        {
            await Task.Yield();
            if (!EnvironmentCheck.IsAdministrator())
            {
                throw new InvalidOperationException("启动分身需要管理员权限，请运行发布目录中的 DeskTwin.exe。");
            }

            ApplyCredentialsForConnection();
            ChildSessionNative.EnsureEnabled();
            ApplyDisplayScale();
            _activeDesktopSize = ComputeTargetSize();
            _rdp.ConnectToChildSession(_activeDesktopSize);
            SetStatus("RDP 正在连接；如出现登录窗口，请输入 Windows 账户密码（不是 PIN）。");
        }
        catch (Exception exception)
        {
            _connecting = false;
            ShowError("无法启动桌面分身", exception);
        }
        finally
        {
            _busy = false;
            RefreshState();
        }
    }

    private void Disconnect()
    {
        try
        {
            _connecting = false;
            ExitFullscreen(restoreResolution: false);
            _rdp.DisconnectSession();
            SetStatus(ChildSessionNative.GetSessionId() is not null
                ? "已断开画面，分身和其中的程序仍在后台运行。"
                : "RDP 已断开。");
        }
        catch (Exception exception)
        {
            ShowError("断开失败", exception);
        }
        finally
        {
            RefreshState();
        }
    }

    private async Task EndSessionAsync(bool requireConfirmation)
    {
        if (_busy)
        {
            return;
        }

        uint? sessionId;
        try
        {
            sessionId = ChildSessionNative.GetSessionId();
        }
        catch (Exception exception)
        {
            ShowError("无法读取分身状态", exception);
            return;
        }

        if (sessionId is null)
        {
            _connecting = false;
            _rdp.DisconnectSession();
            RefreshState();
            return;
        }

        if (requireConfirmation)
        {
            var result = MessageBox.Show(
                $"将注销桌面分身会话 {sessionId}。\n\n分身中的所有程序都会关闭，未保存的工作会丢失。主桌面不会注销。\n\n确定结束分身吗？",
                "结束桌面分身",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (result != MessageBoxResult.Yes)
            {
                return;
            }
        }

        _busy = true;
        _connecting = false;
        ExitFullscreen(restoreResolution: false);
        SetStatus($"正在注销桌面分身会话 {sessionId}…");
        RefreshState();
        try
        {
            await Task.Yield();
            TryDisconnect();
            await Task.Run(() => ChildSessionNative.Logoff(sessionId));
            SetStatus($"桌面分身会话 {sessionId} 已注销。");
        }
        catch (Exception exception)
        {
            ShowError("结束分身失败", exception);
            throw;
        }
        finally
        {
            _busy = false;
            RefreshState();
        }
    }

    private async Task LaunchExecutableAsync()
    {
        if (_busy)
        {
            return;
        }

        var sessionId = ChildSessionNative.GetSessionId();
        if (sessionId is null)
        {
            MessageBox.Show("当前没有可用的分身会话，请先启动并连接。", "DeskTwin");
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "选择要在桌面分身中以管理员权限启动的程序",
            Filter = "Windows 程序 (*.exe)|*.exe",
            CheckFileExists = true,
            CheckPathExists = true,
            DereferenceLinks = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _busy = true;
        SetStatus($"正在分身中启动 {System.IO.Path.GetFileName(dialog.FileName)}…");
        RefreshState();
        try
        {
            await ChildSessionProcessLauncher.LaunchAsync(sessionId.Value, dialog.FileName);
            SetStatus($"已在分身会话 {sessionId} 中启动 {System.IO.Path.GetFileName(dialog.FileName)}。");
        }
        catch (Exception exception)
        {
            ShowError("程序启动失败", exception);
        }
        finally
        {
            _busy = false;
            RefreshState();
        }
    }

    private void SendShortcut(Action action, string name)
    {
        try
        {
            action();
            SetStatus($"已向桌面分身发送 {name}。");
        }
        catch (Exception exception)
        {
            ShowError($"发送 {name} 失败", exception);
        }
    }

    /// <summary>
    /// 截取当前 DeskTwin 画面（全屏时即整个显示器）保存为 PNG 并复制到剪贴板。
    /// RDP 会把 PrintScreen 转发给分身，所以这里由本机接管，保证截图可用。
    /// </summary>
    private void CaptureScreenshot()
    {
        try
        {
            var bounds = _windowHandle == IntPtr.Zero
                ? FullscreenController.GetMonitorBounds(IntPtr.Zero)
                : FullscreenController.GetWindowBounds(_windowHandle);
            using var bitmap = new System.Drawing.Bitmap(
                bounds.Width,
                bounds.Height,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(
                    bounds.Left,
                    bounds.Top,
                    0,
                    0,
                    new System.Drawing.Size(bounds.Width, bounds.Height),
                    System.Drawing.CopyPixelOperation.SourceCopy);
            }

            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                "DeskTwin");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"DeskTwin-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);

            string clipboardNote;
            try
            {
                using var stream = new MemoryStream();
                bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                stream.Position = 0;
                var image = BitmapFrame.Create(
                    stream,
                    BitmapCreateOptions.None,
                    BitmapCacheOption.OnLoad);
                System.Windows.Clipboard.SetImage(image);
                clipboardNote = "，已复制到剪贴板";
            }
            catch (Exception exception) when (exception is COMException or ExternalException)
            {
                clipboardNote = "（剪贴板被其他程序占用，未复制）";
            }

            SetStatus($"已截图{clipboardNote}：{path}");
        }
        catch (Exception exception)
        {
            ShowError("截图失败", exception);
        }
    }

    private void ShowCredentialsDialog()
    {
        if (_credentialDialogOpen || _busy)
        {
            return;
        }

        _credentialDialogOpen = true;
        UpdateGlobalHotKeys();
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var dialog = new CredentialWindow(identity.Name) { Owner = this };
            var accepted = dialog.ShowDialog() == true;
            if (dialog.CredentialsCleared)
            {
                _sessionCredential = null;
                _rdp.ClearCredentials();
            }

            if (accepted && dialog.Result is { } credential)
            {
                _sessionCredential = credential;
                _rdp.SetCredentials(credential.UserName, credential.Password);
                SetStatus(credential.Remember
                    ? "登录凭据已加密保存，下次连接会自动使用。"
                    : "登录凭据仅在本次运行中使用，不会写入磁盘。");
            }

            UpdateCredentialIndicator();
        }
        catch (Exception exception)
        {
            ShowError("登录凭据设置失败", exception);
        }
        finally
        {
            _credentialDialogOpen = false;
            UpdateGlobalHotKeys();
        }
    }

    private void ApplyCredentialsForConnection()
    {
        var sessionCredential = _sessionCredential;
        var savedCredential = sessionCredential is null ? CredentialStore.Load() : null;
        var userName = sessionCredential?.UserName ?? savedCredential?.UserName;
        var password = sessionCredential?.Password ?? savedCredential?.Password;
        if (string.IsNullOrWhiteSpace(userName) && string.IsNullOrEmpty(password))
        {
            _rdp.ClearCredentials();
            return;
        }

        _rdp.SetCredentials(userName, password);
        if (string.IsNullOrEmpty(password))
        {
            SetStatus("已应用登录账户，密码仍由 Windows 登录框提示输入。");
        }
        else
        {
            SetStatus(sessionCredential?.Remember == false
                ? "已应用本次运行的登录凭据。"
                : "已应用记住的登录凭据。");
        }
    }

    private void UpdateCredentialIndicator()
    {
        if (_sessionCredential is { Password.Length: > 0 })
        {
            CredentialsMenuItem.Header = _sessionCredential.Remember
                ? "登录凭据 · 已保存"
                : "登录凭据 · 本次有效";
        }
        else if (CredentialStore.HasSavedCredential)
        {
            CredentialsMenuItem.Header = "登录凭据 · 已保存";
        }
        else
        {
            CredentialsMenuItem.Header = "登录凭据…";
        }
    }

    private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            e.Handled = true;
            if (IsConnectedForFullscreen())
            {
                EnterFullscreen();
            }
            else
            {
                WindowState = WindowState.Maximized;
            }

            return;
        }

        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标已在拖动开始前释放，忽略即可。
        }
    }

    private void ToggleFullscreen()
    {
        if (_fullscreen.IsFullscreen)
        {
            ExitFullscreen();
        }
        else
        {
            EnterFullscreen();
        }
    }

    private void EnterFullscreen()
    {
        if (_fullscreen.IsFullscreen)
        {
            return;
        }

        if (_rdp.ConnectedState != 1)
        {
            SetStatus("连接完成后才能进入全屏。");
            return;
        }

        try
        {
            HeaderMenu.IsOpen = false;
            _fullscreen.Enter();
            FullscreenRow.Height = new GridLength(0);
            HeaderRow.Height = new GridLength(0);
            HeaderBar.Visibility = Visibility.Collapsed;
            ViewportContainer.Margin = new Thickness(0);
            ShowFullscreenExitWindow();
            UpdateViewportLayout();
            SyncDesktopSize("进入全屏");
        }
        catch (Exception exception)
        {
            if (_fullscreen.IsFullscreen)
            {
                ExitFullscreen(restoreResolution: false);
            }

            ShowError("无法进入全屏", exception);
        }
    }

    private void ShowFullscreenExitWindow()
    {
        var monitor = FullscreenController.GetMonitorBounds(_windowHandle);
        var transform = _windowSource?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var exitWindow = new FullscreenExitWindow(this, monitor, transform);
        exitWindow.ExitRequested += (_, _) => ExitFullscreen();
        _fullscreenExitWindow = exitWindow;
        exitWindow.Show();
    }

    private void ExitFullscreen(bool restoreResolution = true)
    {
        if (!_fullscreen.IsFullscreen)
        {
            return;
        }

        var exitWindow = _fullscreenExitWindow;
        _fullscreenExitWindow = null;
        exitWindow?.Close();
        _fullscreen.Exit();
        FullscreenRow.Height = new GridLength(0);
        HeaderRow.Height = new GridLength(24);
        HeaderBar.Visibility = Visibility.Visible;
        ViewportContainer.Margin = new Thickness(0);
        SetStatus("已退出全屏。");
        UpdateViewportLayout();
        ApplyAspectToWindow();

        if (restoreResolution)
        {
            SyncDesktopSize("退出全屏");
        }
    }

    /// <summary>当前应使用的远程分辨率：自适应时跟随画面区/显示器，缩放时用固定值。</summary>
    private DrawingSize ComputeTargetSize()
    {
        if (_settings.FitToWindow)
        {
            if (_fullscreen.IsFullscreen && GetCurrentMonitorSize() is { } monitor)
            {
                return monitor;
            }

            if (GetViewportSize() is { } viewport)
            {
                return viewport;
            }
        }

        return new DrawingSize(_settings.DesktopWidth, _settings.DesktopHeight);
    }

    /// <summary>把远程会话分辨率同步到当前应有的尺寸（自适应模式）。</summary>
    private void SyncDesktopSize(string reason)
    {
        if (_rdp.ConnectedState != 1 || _connecting || _busy)
        {
            return;
        }

        var target = ComputeTargetSize();
        if (target == _activeDesktopSize || !AdaptiveResolution.IsUsable(target))
        {
            return;
        }

        ReconnectAtSize(target, $"{reason}：分身分辨率 → {target.Width} × {target.Height}");
    }

    private void ScheduleFitSync()
    {
        if (!_settings.FitToWindow)
        {
            return;
        }

        _resizeFitTimer.Stop();
        _resizeFitTimer.Start();
    }

    private void ReconnectAtSize(DrawingSize size, string message)
    {
        if (!AdaptiveResolution.IsUsable(size))
        {
            SetStatus("目标分辨率超出 RDP 支持范围，保持当前画面。");
            return;
        }

        try
        {
            ApplyDisplayScale();
            _activeDesktopSize = size;
            _connecting = true;
            _rdp.ReconnectToChildSession(size);
            UpdateViewportLayout();
            SetStatus(message
                      + (HasSavedPassword ? string.Empty : "（未保存密码，可能出现登录提示）")
                      + "…");
        }
        catch (Exception exception)
        {
            _connecting = false;
            ShowError("切换分身分辨率失败", exception);
        }
        finally
        {
            RefreshState();
        }
    }

    /// <summary>是否已有可直接复用的密码（切换分辨率会自动重连一次）。</summary>
    private bool HasSavedPassword =>
        _sessionCredential is { Password.Length: > 0 } || CredentialStore.HasSavedCredential;

    /// <summary>
    /// 按当前显示器 DPI 设置远程会话缩放，避免高 DPI 屏幕上文字过小。
    /// </summary>
    private void ApplyDisplayScale()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var desktopScale = AdaptiveResolution.ToDesktopScaleFactor(dpi.DpiScaleX);
        _rdp.SetDisplayScale(desktopScale, AdaptiveResolution.ToDeviceScaleFactor(desktopScale));
    }

    /// <summary>显示器分辨率或 DPI 变化时重新贴合全屏边界并同步分辨率。</summary>
    private void OnDisplayGeometryChanged()
    {
        RefreshResolutionOptions();
        UpdateViewportLayout();

        if (!_fullscreen.IsFullscreen)
        {
            ScheduleFitSync();
            return;
        }

        _fullscreen.Reapply();
        SyncDesktopSize("显示器分辨率变化");
    }

    /// <summary>点系统的最大化（或双击标题栏、Win+↑）时改为进入应用全屏。</summary>
    private void OnWindowStateChanged()
    {
        if (WindowState != WindowState.Maximized || _fullscreen.IsFullscreen)
        {
            return;
        }

        if (!IsConnectedForFullscreen())
        {
            return;
        }

        WindowState = WindowState.Normal;
        EnterFullscreen();
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _fullscreen.IsFullscreen)
        {
            ExitFullscreen();
            e.Handled = true;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _windowHandle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(_windowHandle);
        _windowSource?.AddHook(OnWindowMessage);
        RefreshResolutionOptions();
        UpdateGlobalHotKeys();
    }

    private IntPtr OnWindowMessage(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message == WmHotKey && wParam.ToInt32() == ScreenshotHotKeyId)
        {
            CaptureScreenshot();
            handled = true;
        }
        else if (message == WmDisplayChange)
        {
            // 分辨率切换、显示器插拔、远程桌面会话介入都会触发，重新贴合边界。
            OnDisplayGeometryChanged();
        }
        else if (message == WmSizing && !_settings.FitToWindow && !_fullscreen.IsFullscreen)
        {
            if (EnforceAspectWhileSizing(wParam.ToInt32(), lParam))
            {
                handled = true;
                return new IntPtr(1);
            }
        }
        else if (message == WmSysCommand && (wParam.ToInt32() & 0xFFF0) == ScMaximize)
        {
            // 系统的“最大化”按钮/双击标题栏/Win+↑ 统一改为应用全屏。
            if (!_fullscreen.IsFullscreen && IsConnectedForFullscreen())
            {
                handled = true;
                EnterFullscreen();
            }
        }

        return IntPtr.Zero;
    }

    private bool IsConnectedForFullscreen()
    {
        try
        {
            return _rdp.ConnectedState == 1;
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>只有 DeskTwin 是前台窗口时才占用 PrintScreen，其余时间照常给系统用。</summary>
    private void UpdateGlobalHotKeys()
    {
        if (_windowHandle == IntPtr.Zero)
        {
            return;
        }

        var shouldRegister = IsActive && !_credentialDialogOpen && !_closeInProgress;
        if (shouldRegister && !_screenshotHotKeyRegistered)
        {
            _screenshotHotKeyRegistered =
                ChildSessionNative.RegisterScreenshotHotKey(_windowHandle, ScreenshotHotKeyId);
        }
        else if (!shouldRegister && _screenshotHotKeyRegistered)
        {
            ChildSessionNative.UnregisterScreenshotHotKey(_windowHandle, ScreenshotHotKeyId);
            _screenshotHotKeyRegistered = false;
        }
    }

    private void OnWatchToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.WatchMode = WatchMenuItem.IsChecked;
        _rdp.SetInputBlocked(_settings.WatchMode);
        SetStatus(_settings.WatchMode
            ? "观看模式已开启，本机键鼠不会进入分身。"
            : "观看模式已关闭，可以操作分身。");
        SaveSettings();
    }

    private void OnTopmostToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.Topmost = TopmostMenuItem.IsChecked;
        Topmost = _settings.Topmost;
        SetStatus(_settings.Topmost ? "窗口已置顶。" : "窗口已取消置顶。");
        SaveSettings();
    }

    private void OnMuteToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.AudioMuted = MuteMenuItem.IsChecked;
        _rdp.SetAudioMuted(_settings.AudioMuted);
        SaveSettings();
        ReconnectForSettings(_settings.AudioMuted ? "分身音频已设为静音" : "分身音频已开启");
    }

    private void OnShortcutsToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.SendSystemShortcuts = ShortcutsMenuItem.IsChecked;
        _rdp.SetSendSystemShortcutsToRemote(_settings.SendSystemShortcuts);
        SaveSettings();
        ReconnectForSettings(_settings.SendSystemShortcuts
            ? "系统组合键将发送到分身"
            : "系统组合键将在本机生效");
    }

    private void OnStartupWarningToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.SkipStartupWarning = !StartupWarningMenuItem.IsChecked;
        SaveSettings();
        SetStatus(_settings.SkipStartupWarning
            ? "启动时不再提示环境风险。"
            : "启动时会提示环境风险。");
    }

    private void ReconnectForSettings(string message)
    {
        try
        {
            if (_rdp.ConnectedState != 0)
            {
                _connecting = true;
                ApplyDisplayScale();
                _activeDesktopSize = ComputeTargetSize();
                _rdp.ReconnectToChildSession(_activeDesktopSize);
                SetStatus($"{message}，正在自动重连 RDP…");
            }
            else
            {
                SetStatus($"{message}，将在下次连接时生效。");
            }
        }
        catch (Exception exception)
        {
            _connecting = false;
            ShowError("设置已保存，但自动重连失败", exception);
        }
        finally
        {
            RefreshState();
        }
    }

    private void OnLoginCompleted(object? sender, EventArgs e)
    {
        _connecting = false;
        _rdp.SetInputBlocked(_settings.WatchMode);
        SetStatus("桌面分身已连接，可以直接操作。断开后程序仍会运行。");
        RefreshState();
        SyncDesktopSize("连接完成");
    }

    private void OnConnectionFailed(object? sender, ChildSessionConnectionFailedEventArgs e)
    {
        _connecting = false;
        ExitFullscreen(restoreResolution: false);
        var issues = EnvironmentCheck.Collect();
        var diagnosis = issues.Count > 0
            ? "\n\n环境检测：\n" + EnvironmentCheck.BuildSummary(issues)
            : string.Empty;
        SetStatus("RDP 连接失败，请查看错误详情。");
        RefreshState();
        MessageBox.Show(
            e.Message + diagnosis,
            "桌面分身连接失败",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private void RefreshState()
    {
        try
        {
            var state = _rdp.ConnectedState;
            var sessionId = ChildSessionNative.GetSessionId();
            var stateText = state switch
            {
                0 => "未连接",
                1 => "已连接",
                2 => "正在连接",
                _ => $"状态 {state}"
            };
            SessionInfoMenuItem.Header = sessionId is null
                ? $"分身会话：无 · RDP：{stateText}"
                : $"分身会话：{sessionId} · RDP：{stateText}";
            StatusBadgeText.Text = _connecting && state != 1 ? "正在连接" : stateText;
            var (color, background) = state == 1
                ? ("#1D9B6C", "#E7F7F0")
                : _connecting || state == 2
                    ? ("#D18B16", "#FFF5DF")
                    : ("#98A2B3", "#EEF1F5");
            StatusDot.Fill = Brush(color);
            StatusBadge.Background = Brush(background);

            RdpFormsHost.Visibility = _connecting || state != 0 ? Visibility.Visible : Visibility.Collapsed;
            OverlayPanel.Visibility = RdpFormsHost.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
            if (OverlayPanel.Visibility == Visibility.Visible)
            {
                OverlayTitle.Text = sessionId is null ? "你的第二个 Windows 桌面" : "分身仍在后台运行";
                OverlayDescription.Text = sessionId is null
                    ? "程序在独立会话中运行，隐藏或断开画面也不会中断任务。主桌面可以继续处理其他工作。"
                    : $"分身会话 {sessionId} 和其中的程序仍在运行。点击“连接”恢复画面，或选择“结束分身”彻底注销。";
            }

            StartButton.IsEnabled = !_busy && !_connecting && state == 0;
            OverlayStartButton.IsEnabled = StartButton.IsEnabled;
            OverlayStartButton.Content = sessionId is null ? "启动并连接桌面分身" : "重新连接现有分身";
            OverlayStartButton.Visibility = sessionId is null
                ? Visibility.Collapsed
                : Visibility.Visible;
            DisconnectMenuItem.IsEnabled = !_busy && state != 0;
            LaunchMenuItem.IsEnabled = !_busy && sessionId is not null;
            TaskViewMenuItem.IsEnabled = !_busy && state == 1;
            ShowDesktopMenuItem.IsEnabled = !_busy && state == 1;
            ScreenshotMenuItem.IsEnabled = !_busy && state != 0;
            ResolutionMenu.IsEnabled = !_busy;
            ResolutionModeMenu.IsEnabled = !_busy;
            CredentialsMenuItem.IsEnabled = !_busy;
            EndMenuItem.IsEnabled = !_busy && (sessionId is not null || state != 0);
            RefreshResolutionOptionsIfMonitorChanged();
            UpdateGlobalHotKeys();
        }
        catch (Exception exception) when (exception is COMException
                                          or InvalidOperationException
                                          or Win32Exception
                                          or TargetInvocationException)
        {
            SessionInfoMenuItem.Header = "分身会话：状态读取失败";
        }
    }

    private async void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_closeApproved)
        {
            return;
        }

        e.Cancel = true;
        if (_closeInProgress)
        {
            return;
        }

        _closeInProgress = true;
        try
        {
            HeaderMenu.IsOpen = false;
            var hasSession = ChildSessionNative.GetSessionId() is not null || _rdp.ConnectedState != 0;
            if (!hasSession)
            {
                _closeApproved = true;
                Close();
                return;
            }

            ExitFullscreen(restoreResolution: false);
            var wasTopmost = Topmost;
            Topmost = false;
            var result = MessageBoxResult.Cancel;
            try
            {
                result = MessageBox.Show(
                    this,
                    "退出前请选择如何处理桌面分身：\n\n“是”：注销分身并退出（分身内程序全部关闭）\n“否”：仅断开并退出（分身内程序继续运行）\n“取消”：返回应用\n\n保留分身可能影响 Windows 关机或重启。",
                    "退出 DeskTwin",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Warning,
                    MessageBoxResult.Cancel);
            }
            finally
            {
                if (result == MessageBoxResult.Cancel && !_closeApproved)
                {
                    Topmost = wasTopmost;
                }
            }

            if (result == MessageBoxResult.Cancel)
            {
                return;
            }

            if (result == MessageBoxResult.Yes)
            {
                await EndSessionAsync(false);
            }
            else
            {
                Disconnect();
            }

            _closeApproved = true;
            Close();
        }
        catch (Exception exception)
        {
            _closeApproved = false;
            ShowError("无法关闭 DeskTwin", exception);
        }
        finally
        {
            _closeInProgress = false;
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _statusTimer.Stop();
        _resizeFitTimer.Stop();
        _aspectTimer.Stop();
        if (_screenshotHotKeyRegistered)
        {
            ChildSessionNative.UnregisterScreenshotHotKey(_windowHandle, ScreenshotHotKeyId);
            _screenshotHotKeyRegistered = false;
        }

        _windowSource?.RemoveHook(OnWindowMessage);
        _windowSource = null;
        _rdp.LoginCompleted -= OnLoginCompleted;
        _rdp.ConnectionFailed -= OnConnectionFailed;
        _rdp.ClearCredentials();
        RdpFormsHost.Child = null;
        _rdp.Dispose();
        SaveSettings();
    }

    private void ShowEnvironmentReport()
    {
        try
        {
            var issues = EnvironmentCheck.Collect();
            var session = ChildSessionNative.GetSessionId();
            var report = $"系统：{RuntimeInformation.OSDescription}\n"
                         + $"管理员权限：{(EnvironmentCheck.IsAdministrator() ? "是" : "否")}\n"
                         + $"Child Session 已启用：{(ChildSessionNative.IsEnabled() ? "是" : "否")}\n"
                         + $"当前分身会话：{session?.ToString() ?? "无"}\n"
                         + $"RDP 端口：{ChildSessionNative.GetRdpPort()}\n"
                         + $"分辨率模式：{(_settings.FitToWindow ? "自适应" : "缩放")}\n"
                         + $"当前分身分辨率：{_activeDesktopSize.Width} × {_activeDesktopSize.Height}\n\n"
                         + (issues.Count == 0
                             ? "未发现已知兼容性问题。"
                             : EnvironmentCheck.BuildSummary(issues));
            MessageBox.Show(report, "DeskTwin 环境检测", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            ShowError("环境检测失败", exception);
        }
    }

    private void ShowHelp()
    {
        MessageBox.Show(
            "1. 点击“连接”，首次初始化可能需要等待。\n"
            + "2. 若 Windows 弹出登录框，输入当前账户的用户名和密码，不要使用 PIN。\n"
            + "3. 先在“登录凭据”中保存账户和密码，之后切换分辨率、修改显示设置都不再要求重新输入密码；密码由 Windows DPAPI 按当前用户加密。\n"
            + "4. 顶部连接条同时是标题栏：可拖动、双击进入全屏；在它上面点右键（或点“选项”）可打开全部设置。\n"
            + "5. 分辨率有两种模式：“自适应”随窗口/全屏改变分身分辨率（点对点、不缩放）；“缩放”保持固定分辨率，窗口比例会自动锁成该分辨率，画面正好铺满、不变形（全屏时若显示器比例不同，仍会有黑边）。\n"
            + "6. 点全屏按钮、系统最大化按钮、双击标题栏或 Win+↑ 都会进入真全屏（覆盖任务栏）；进入全屏先显示 3 秒，之后只有把鼠标移到屏幕右上角才出现，移开约 1 秒自动隐藏。F11 仍照常发给分身内的程序。\n"
            + "7. PrintScreen 在 DeskTwin 窗口内会截取当前分身画面，保存到“图片\\DeskTwin”并复制到剪贴板。\n"
            + "8. 启动时的环境风险提示可勾选“不再显示此提示”；之后可在右键菜单“启动时提示环境风险”里恢复。\n"
            + "9. “断开”只关闭画面，程序继续运行；“结束分身”会注销会话并关闭其中程序。\n"
            + "10. 分身内需要管理员权限的程序，请通过本应用启动。\n"
            + "11. 一个 Windows 最多一个 Child Session；部分软件不支持在两个桌面同时运行。\n"
            + "12. 关机或重启前请先结束分身。\n\n"
            + "此工具使用普通 RDP 鼠标，不包含 BetterGI 针对原神定制的 Raw Input 相对鼠标转发。",
            "桌面分身 · 使用帮助",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private static void ShowAbout()
    {
        var result = MessageBox.Show(
            "DeskTwin 1.3.4\n\n独立桌面分身工具，基于 Windows Child Session 与本机 RDP ActiveX。\n\n"
            + "核心 RDP 封装和会话启动方式参考并修改自 BetterGI（babalae/better-genshin-impact），"
            + "参考源码提交 9e3a0b008b063e144982818d46f00c00081ffbd2。\n\n"
            + "本修改版按 GPL-3.0 发布，无担保。源代码与 LICENSE.txt 随应用提供。\n\n是否打开 BetterGI 官方桌面分身文档？",
            "关于 DeskTwin",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information,
            MessageBoxResult.No);
        if (result == MessageBoxResult.Yes)
        {
            Process.Start(new ProcessStartInfo("https://www.bettergi.com/feats/command/session.html")
            {
                UseShellExecute = true
            });
        }
    }

    private void SaveSettings()
    {
        try
        {
            SettingsService.Save(_settings);
        }
        catch (Exception exception)
        {
            SetStatus("配置保存失败：" + exception.GetBaseException().Message);
        }
    }

    private void TryDisconnect()
    {
        try
        {
            _rdp.DisconnectSession();
        }
        catch (Exception exception) when (exception is COMException or TargetInvocationException)
        {
            // ActiveX 已在断开时仍可继续注销会话。
        }
    }

    private void SetStatus(string message) => StatusMessage.Text = message;

    private void ShowError(string title, Exception exception)
    {
        var actual = exception.GetBaseException();
        SetStatus($"{title}：{actual.Message}");
        MessageBox.Show(actual.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static SolidColorBrush Brush(string hex) =>
        new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }
}
