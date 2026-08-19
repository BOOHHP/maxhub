using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace MaxHub.Agent.Tray;

public partial class MainWindow : Window
{
    private readonly AccountViewModel _account;
    private readonly ConnectorsViewModel _connectors;
    private readonly ToolsViewModel _tools;
    private bool _balloonShown;

    public MainWindow(AccountViewModel account, ConnectorsViewModel connectors, ToolsViewModel tools)
    {
        InitializeComponent();
        // 无边框窗口最大化默认盖住任务栏；+12 补偿 WindowChrome 6px 边框外扩
        MaxHeight = SystemParameters.WorkArea.Height + 12;
        MaxWidth = SystemParameters.WorkArea.Width + 12;
        _account = account;
        _connectors = connectors;
        _tools = tools;
        VersionText.Text = "v" + (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?");

        AccountPage.DataContext = account;
        ConnectorsPage.DataContext = connectors;
        ToolsPage.DataContext = tools;
        account.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AccountViewModel.StateName))
                Dispatcher.Invoke(UpdateAccountPanels);
        };
        UpdateAccountPanels();

        // 默认落地页：已登录 → Connector 管理；未登录 → 账号
        if (account.IsLoggedIn)
            NavConnectors.IsChecked = true;
        else
            NavAccount.IsChecked = true;
    }

    private void UpdateAccountPanels()
    {
        LoggedOutPanel.Visibility = _account.State == LoginState.LoggedOut ? Visibility.Visible : Visibility.Collapsed;
        WaitingPanel.Visibility = _account.State == LoginState.WaitingAuth ? Visibility.Visible : Visibility.Collapsed;
        LoggedInPanel.Visibility = _account.State == LoginState.LoggedIn ? Visibility.Visible : Visibility.Collapsed;
    }

    private void NavAccount_Checked(object sender, RoutedEventArgs e)
    {
        ConnectorsPage.Visibility = Visibility.Collapsed;
        ToolsPage.Visibility = Visibility.Collapsed;
        ShowPageWithFade(AccountPage);
    }

    private void NavConnectors_Checked(object sender, RoutedEventArgs e)
    {
        AccountPage.Visibility = Visibility.Collapsed;
        ToolsPage.Visibility = Visibility.Collapsed;
        ShowPageWithFade(ConnectorsPage);
        _ = _connectors.RefreshAsync();
    }

    private void NavTools_Checked(object sender, RoutedEventArgs e)
    {
        AccountPage.Visibility = Visibility.Collapsed;
        ConnectorsPage.Visibility = Visibility.Collapsed;
        ShowPageWithFade(ToolsPage);
        _ = _tools.RefreshAsync();
    }

    private static void ShowPageWithFade(UIElement page)
    {
        page.Visibility = Visibility.Visible;
        page.BeginAnimation(OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e) => DragMove();

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "❏" : "▢";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>关闭 = 隐藏到托盘，进程常驻（本地服务 47810 供 Max 面板使用）。</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
        if (!_balloonShown)
        {
            _balloonShown = true;
            ((App)Application.Current).ShowBalloon("MaxHub Agent 仍在后台运行", "可从托盘图标重新打开");
        }
    }

    public void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }
}
