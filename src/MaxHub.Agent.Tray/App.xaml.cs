using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;

namespace MaxHub.Agent.Tray;

public partial class App : Application
{
    private TaskbarIcon? _trayIcon;
    private AppServices? _services;
    private MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _services = new AppServices();
        var account = new AccountViewModel(_services);
        var connectors = new ConnectorsViewModel(_services, account);
        _mainWindow = new MainWindow(account, connectors);

        if (account.IsLoggedIn)
            _services.StartLocalServer();

        var loginStatus = new MenuItem { IsEnabled = false };
        void UpdateLoginStatus() =>
            loginStatus.Header = account.IsLoggedIn ? $"✓ 已登录：{account.Username}" : "未登录";
        account.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(AccountViewModel.StateName))
                Dispatcher.Invoke(UpdateLoginStatus);
        };
        UpdateLoginStatus();

        var openItem = new MenuItem { Header = "打开 MaxHub Agent", FontWeight = FontWeights.Bold };
        openItem.Click += (_, _) => _mainWindow.ShowFromTray();
        var exitItem = new MenuItem { Header = "退出程序" };
        exitItem.Click += async (_, _) =>
        {
            _trayIcon?.Dispose();
            if (_services is not null)
                await _services.StopLocalServerAsync();
            Shutdown();
        };

        var menu = new ContextMenu();
        menu.Items.Add(openItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(loginStatus);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);

        _trayIcon = new TaskbarIcon
        {
            Icon = CreateTrayIcon(),
            ToolTipText = "MaxHub Agent",
            ContextMenu = menu,
        };
        _trayIcon.TrayMouseDoubleClick += (_, _) => _mainWindow.ShowFromTray();

        _mainWindow.ShowFromTray();
    }

    public void ShowBalloon(string title, string message) =>
        _trayIcon?.ShowBalloonTip(title, message, BalloonIcon.Info);

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        base.OnExit(e);
    }

    /// <summary>运行时绘制托盘图标，避免仓库携带二进制资源。</summary>
    private static Icon CreateTrayIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(Color.FromArgb(0x4C, 0x9F, 0xE0));
            g.FillEllipse(brush, 2, 2, 28, 28);
            using var font = new Font("Segoe UI", 15, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
            g.DrawString("M", font, Brushes.White, 7, 7);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
