using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;
using MaxHub.Agent.Core.Remote;

namespace MaxHub.Agent.Tray;

public partial class App : Application
{
    private TaskbarIcon? _trayIcon;
    private AppServices? _services;
    private MainWindow? _mainWindow;
    private bool _iconLoggedIn;
    private bool _iconHasUpdate;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _services = new AppServices();
        var account = new AccountViewModel(_services);
        var connectors = new ConnectorsViewModel(_services, account);
        var tools = new ToolsViewModel(_services, account);
        _mainWindow = new MainWindow(account, connectors, tools);

        if (account.IsLoggedIn)
            _services.StartLocalServer();

        var loginStatus = new MenuItem { IsEnabled = false };
        void UpdateLoginStatus()
        {
            loginStatus.Header = account.IsLoggedIn ? $"✓ 已登录：{account.Username}" : "未登录";
            _iconLoggedIn = account.IsLoggedIn;
            RefreshTrayIcon();
        }
        account.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(AccountViewModel.StateName))
                Dispatcher.Invoke(UpdateLoginStatus);
        };
        connectors.UpdateCountChanged += count => Dispatcher.Invoke(() =>
        {
            _iconHasUpdate = count > 0;
            RefreshTrayIcon();
        });
        UpdateLoginStatus();

        var openItem = new MenuItem { Header = "打开 MaxHub Agent", FontWeight = FontWeights.Bold };
        openItem.Click += (_, _) => _mainWindow.ShowFromTray();
        var autoStartItem = new MenuItem { Header = "开机自启", IsCheckable = true, IsChecked = IsAutoStartEnabled() };
        autoStartItem.Click += (_, _) => SetAutoStart(autoStartItem.IsChecked);
        var exitItem = new MenuItem { Header = "退出程序" };
        exitItem.Click += async (_, _) => await ExitCleanlyAsync();

        var menu = new ContextMenu();
        menu.Items.Add(openItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(loginStatus);
        menu.Items.Add(autoStartItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);

        _trayIcon = new TaskbarIcon
        {
            Icon = CreateTrayIcon(_iconLoggedIn, _iconHasUpdate),
            ToolTipText = "MaxHub Agent",
            ContextMenu = menu,
        };
        _trayIcon.TrayMouseDoubleClick += (_, _) => _mainWindow.ShowFromTray();
        UpdateLoginStatus();

        _mainWindow.ShowFromTray();
        CheckForAgentUpdate();
    }

    /// <summary>释放托盘与本地服务后退出；自更新替换前也走这里释放 exe 文件锁。</summary>
    public async Task ExitCleanlyAsync()
    {
        _trayIcon?.Dispose();
        if (_services is not null)
            await _services.StopLocalServerAsync();
        Shutdown();
    }

    /// <summary>启动后静默检查 Agent 新版本；下载完成后必须退出当前进程，释放 exe 文件锁让替换脚本接管。</summary>
    private async void CheckForAgentUpdate()
    {
        try
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3);
            var updater = new SelfUpdater(_services!.Hub) { CurrentVersion = version };
            var release = await updater.CheckForUpdateAsync();
            if (release is null)
                return;
            ShowBalloon("发现新版本", $"MaxHub Agent v{release.Version} 已可用，正在后台下载并自动更新…");
            await updater.DownloadAndInstallAsync(release);
            // 退出当前进程：替换脚本等待文件锁释放后 move 新 exe 并重新拉起
            await ExitCleanlyAsync();
        }
        catch (Exception ex)
        {
            ShowBalloon("更新失败", ex.Message);
        }
    }

    private const string AutoStartKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AutoStartName = "MaxHubAgent";

    private static bool IsAutoStartEnabled()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AutoStartKey);
        return key?.GetValue(AutoStartName) is string;
    }

    private static void SetAutoStart(bool enable)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(AutoStartKey);
        if (enable && Environment.ProcessPath is { } exePath)
            key.SetValue(AutoStartName, '"' + exePath + '"');
        else
            key.DeleteValue(AutoStartName, throwOnMissingValue: false);
    }

    private void RefreshTrayIcon()
    {
        if (_trayIcon is null)
            return;
        var old = _trayIcon.Icon;
        _trayIcon.Icon = CreateTrayIcon(_iconLoggedIn, _iconHasUpdate);
        old?.Dispose();
    }

    public void ShowBalloon(string title, string message) =>
        _trayIcon?.ShowBalloonTip(title, message, BalloonIcon.Info);

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        base.OnExit(e);
    }

    /// <summary>运行时绘制 MaxHub 分发中枢图标：未登录=灰，已登录=蓝，有可更新=右下角橙点。</summary>
    private static Icon CreateTrayIcon(bool loggedIn, bool hasUpdate)
    {
        using var bmp = new Bitmap(64, 64);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.ScaleTransform(2, 2);

            using var background = new SolidBrush(Color.FromArgb(0x1E, 0x20, 0x23));
            using var link = new SolidBrush(loggedIn ? Color.FromArgb(0x4C, 0x9F, 0xE0) : Color.FromArgb(0x5F, 0x63, 0x68));
            using var center = new SolidBrush(Color.FromArgb(0xE8, 0xEA, 0xED));
            FillRoundedRectangle(g, background, new RectangleF(2, 2, 28, 28), 7);

            using (var pathPen = new Pen(link, 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(pathPen, 16, 16, 8, 8);
                g.DrawLine(pathPen, 16, 16, 8, 24);
                g.DrawLine(pathPen, 16, 16, 24, 16);
            }
            FillRoundedRectangle(g, link, new RectangleF(5, 5, 6, 6), 1.5f);
            FillRoundedRectangle(g, link, new RectangleF(5, 21, 6, 6), 1.5f);
            FillRoundedRectangle(g, link, new RectangleF(21, 13, 6, 6), 1.5f);
            FillRoundedRectangle(g, center, new RectangleF(12, 12, 8, 8), 1.5f);
            if (hasUpdate)
            {
                using var dot = new SolidBrush(Color.FromArgb(0xF0, 0xB4, 0x29));
                g.FillEllipse(dot, 20, 20, 11, 11);
            }
        }
        var iconHandle = bmp.GetHicon();
        try
        {
            using var nativeIcon = Icon.FromHandle(iconHandle);
            return (Icon)nativeIcon.Clone();
        }
        finally
        {
            DestroyIcon(iconHandle);
        }
    }

    private static void FillRoundedRectangle(Graphics graphics, Brush brush, RectangleF bounds, float radius)
    {
        using var path = CreateRoundedRectanglePath(bounds, radius);
        graphics.FillPath(brush, path);
    }

    private static GraphicsPath CreateRoundedRectanglePath(RectangleF bounds, float radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
