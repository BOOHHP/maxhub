using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
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
    private Mutex? _singleInstanceMutex;

    private const string SingleInstanceMutexName = @"Local\MaxHubAgent.SingleInstance";
    private const string ShowMainWindowMessageName = "MaxHubAgent.ShowMain";
    private static readonly uint ShowMainWindowMessage = RegisterWindowMessage(ShowMainWindowMessageName);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (SelfUpdater.TryNormalizeVersionedExecutableName(CurrentVersion))
        {
            Shutdown();
            return;
        }

        // 单实例：已有 Agent 运行时把其主窗口带到前台并立即退出，
        // 避免多开抢占托盘、本地服务端口与自更新文件锁
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            BringExistingInstanceToFront();
            Shutdown();
            return;
        }

        _services = new AppServices();
        var account = new AccountViewModel(_services);
        var connectors = new ConnectorsViewModel(_services, account);
        var tools = new ToolsViewModel(_services, account);
        var currentVersion = CurrentVersion;
        var updatedVersion = GetAfterUpdateVersion(e.Args, currentVersion);
        var statePath = Path.Combine(_services.AgentRoot, "release-notes-state.json");
        var existingInstallation = Directory.Exists(_services.AgentRoot) &&
            Directory.EnumerateFileSystemEntries(_services.AgentRoot)
                .Any(path => !string.Equals(path, statePath, StringComparison.OrdinalIgnoreCase));
        var releaseNotesState = new ReleaseNotesStateStore(statePath);
        var shouldAutoShow = releaseNotesState.ShouldAutoShow(
            currentVersion,
            launchedAfterUpdate: updatedVersion is not null,
            existingInstallation);
        var releaseNotes = new ReleaseNotesViewModel(
            currentVersion,
            shouldAutoShow ? currentVersion : null);
        _mainWindow = new MainWindow(account, connectors, tools, releaseNotes, shouldAutoShow);

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
    }

    private static string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";

    private static string? GetAfterUpdateVersion(string[] args, string currentVersion)
    {
        for (var index = 0; index < args.Length - 1; index++)
            if (args[index] == "--after-update" && args[index + 1] == currentVersion)
                return currentVersion;
        return null;
    }

    /// <summary>释放托盘与本地服务后退出；自更新替换前也走这里释放 exe 文件锁。</summary>
    public async Task ExitCleanlyAsync()
    {
        _trayIcon?.Dispose();
        if (_services is not null)
            await _services.StopLocalServerAsync();
        Shutdown();
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
        if (_singleInstanceMutex is not null)
        {
            _singleInstanceMutex.ReleaseMutex();
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }
        base.OnExit(e);
    }

    /// <summary>向已有实例广播自定义消息，由其主窗口 Show+Activate 到前台。
    /// 必须用 PostMessage：广播 SendMessage 会被锁屏等不响应窗口阻塞，导致第二实例挂起无法退出。</summary>
    private static void BringExistingInstanceToFront() =>
        PostMessage(HWND_BROADCAST, ShowMainWindowMessage, UIntPtr.Zero, UIntPtr.Zero);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string messageName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, UIntPtr wParam, UIntPtr lParam);

    private static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

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
