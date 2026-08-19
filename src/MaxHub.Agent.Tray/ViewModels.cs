using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using MaxHub.Agent.Core.Detection;
using MaxHub.Agent.Core.Install;
using MaxHub.Agent.Core.Paths;
using MaxHub.Agent.Core.Remote;
using MaxHub.Core.Ledger;

namespace MaxHub.Agent.Tray;

/// <summary>托盘应用共享服务：会话、HubClient、安装引擎与本地服务生命周期。</summary>
public sealed class AppServices
{
    public string AgentRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MaxHub");

    public string ServerUrl { get; } =
        Environment.GetEnvironmentVariable("MAXHUB_SERVER") ?? "http://10.2.13.8:5100";

    public AgentSessionStore SessionStore { get; }
    public HubClient Hub { get; }
    public DefaultMaxPathResolver Resolver { get; } = new();
    public LedgerStore Ledger { get; }

    private Microsoft.AspNetCore.Builder.WebApplication? _localServer;

    public AppServices()
    {
        SessionStore = new AgentSessionStore(Path.Combine(AgentRoot, "agent-settings.json"));
        Ledger = new LedgerStore(Path.Combine(AgentRoot, "installed.json"));
        var http = new HttpClient(new SessionRefreshHandler(() => SessionStore.ForceRefresh(ServerUrl)))
        {
            BaseAddress = new Uri(ServerUrl),
        };
        Hub = new HubClient(http);
    }

    /// <summary>启动时恢复会话；刷新失败视为未登录并清理本地凭据。</summary>
    public bool TryRestoreSession()
    {
        if (!SessionStore.HasSession)
            return false;
        try
        {
            Hub.UseToken(SessionStore.LoadAccessToken(ServerUrl));
            return true;
        }
        catch (InvalidOperationException)
        {
            SessionStore.Clear();
            return false;
        }
    }

    public void StartLocalServer()
    {
        if (_localServer is not null)
            return;
        _localServer = MaxHub.Agent.Service.AgentLocalServer.Build(AgentRoot, Resolver, Hub);
        _ = _localServer.RunAsync();
    }

    public async Task StopLocalServerAsync()
    {
        if (_localServer is null)
            return;
        await _localServer.StopAsync();
        _localServer = null;
    }
}

public enum LoginState { LoggedOut, WaitingAuth, LoggedIn }

public sealed class AccountViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private LoginState _state;
    private string _username = "";
    private string _employeeId = "";
    private string _notice = "";
    private bool _confirmingLogout;
    private CancellationTokenSource? _loginCts;

    public event Action? LoggedInChanged;

    public AccountViewModel(AppServices services)
    {
        _services = services;
        LoginCommand = new RelayCommand(LoginAsync);
        CancelLoginCommand = new RelayCommand(() => _loginCts?.Cancel());
        LogoutCommand = new RelayCommand(() => ConfirmingLogout = true);
        ConfirmLogoutCommand = new RelayCommand(Logout);
        CancelLogoutCommand = new RelayCommand(() => ConfirmingLogout = false);
        if (services.TryRestoreSession())
        {
            var user = services.SessionStore.ReadUser();
            _state = LoginState.LoggedIn;
            _username = user?.Username ?? "已登录";
            _employeeId = user?.EmployeeId ?? "";
        }
    }

    public LoginState State { get => _state; private set { Set(ref _state, value); Raise(nameof(StateName)); } }
    public string StateName => State.ToString();
    public string Username { get => _username; private set => Set(ref _username, value); }
    public string EmployeeId { get => _employeeId; private set => Set(ref _employeeId, value); }
    public string Notice { get => _notice; private set => Set(ref _notice, value); }
    public string ServerUrl => _services.ServerUrl;
    public bool IsLoggedIn => State == LoginState.LoggedIn;
    public bool ConfirmingLogout { get => _confirmingLogout; private set => Set(ref _confirmingLogout, value); }

    public RelayCommand LoginCommand { get; }
    public RelayCommand CancelLoginCommand { get; }
    public RelayCommand LogoutCommand { get; }
    public RelayCommand ConfirmLogoutCommand { get; }
    public RelayCommand CancelLogoutCommand { get; }

    private async Task LoginAsync()
    {
        Notice = "";
        State = LoginState.WaitingAuth;
        _loginCts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            var qr = await _services.Hub.CreateQrSessionAsync();
            if (qr.AuthorizeUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                using var listener = LocalCallbackListener.Start();
                Process.Start(new ProcessStartInfo(qr.AuthorizeUrl) { UseShellExecute = true });
                var callback = await listener.WaitForCallbackAsync(TimeSpan.FromMinutes(3));
                if (callback is null || callback.State != qr.SessionId)
                {
                    Fail("授权超时或校验失败，请重试");
                    return;
                }
                await _services.Hub.CompleteQrAsync(qr.SessionId, callback.Code, callback.State);
            }

            for (var i = 0; i < 60 && !_loginCts.IsCancellationRequested; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), _loginCts.Token);
                if (await _services.Hub.PollQrAsync(qr.SessionId) is { } session)
                {
                    _services.SessionStore.Save(session.AccessToken, session.RefreshToken, session.ExpiresAtUtc,
                        new AgentUser(session.EmployeeId, session.Username));
                    Username = session.Username;
                    EmployeeId = session.EmployeeId;
                    State = LoginState.LoggedIn;
                    _services.StartLocalServer();
                    LoggedInChanged?.Invoke();
                    return;
                }
            }
            Fail("授权超时，请重试");
        }
        catch (OperationCanceledException)
        {
            Fail("");
        }
        catch (Exception ex)
        {
            Fail($"登录失败：{Brief(ex)}");
        }
    }

    private void Fail(string notice)
    {
        Notice = notice;
        State = LoginState.LoggedOut;
    }

    private void Logout()
    {
        ConfirmingLogout = false;
        _services.SessionStore.Clear();
        State = LoginState.LoggedOut;
        Username = "";
        EmployeeId = "";
        LoggedInChanged?.Invoke();
    }

    internal static string Brief(Exception ex) =>
        ex is HttpRequestException ? "无法连接服务器，请检查网络" : ex.Message;
}

public enum ConnectorStatus { NotInstalled, Installed, UpdateAvailable, Installing, Failed }

public sealed class ConnectorRowViewModel : ViewModelBase
{
    private readonly ConnectorsViewModel _owner;
    private ConnectorStatus _status;
    private string _statusText = "";
    private string _errorMessage = "";
    private double _progress;

    public ConnectorRowViewModel(ConnectorsViewModel owner, MaxInstallation installation)
    {
        _owner = owner;
        Installation = installation;
        ActionCommand = new RelayCommand(RunActionAsync, () => Status != ConnectorStatus.Installing);
    }

    public MaxInstallation Installation { get; }
    public string DisplayName => $"3ds Max {Installation.Year}";
    public string InstallDir => Installation.InstallDir;

    public ConnectorStatus Status
    {
        get => _status;
        set { Set(ref _status, value); Raise(nameof(StatusName)); Raise(nameof(ActionText)); Raise(nameof(HasError)); ActionCommand.RaiseCanExecuteChanged(); }
    }

    public string StatusName => Status.ToString();
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
    public string ErrorMessage { get => _errorMessage; set { Set(ref _errorMessage, value); Raise(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    public double Progress { get => _progress; set => Set(ref _progress, value); }

    public string ActionText => Status switch
    {
        ConnectorStatus.NotInstalled => "安装",
        ConnectorStatus.Installed => "卸载",
        ConnectorStatus.UpdateAvailable => "更新",
        ConnectorStatus.Failed => "重试",
        _ => "",
    };

    public RelayCommand ActionCommand { get; }

    private Task RunActionAsync() => Status == ConnectorStatus.Installed
        ? _owner.UninstallAsync(this)
        : _owner.InstallAsync(this);
}

public sealed class ConnectorsViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private readonly AccountViewModel _account;
    private string _banner = "";
    private string _installAllText = "全部安装";
    private bool _busy;
    private bool _isBatchInstalling;
    private double _batchProgress;

    /// <summary>可更新数量变化，供托盘图标叠加提醒点。</summary>
    public event Action<int>? UpdateCountChanged;

    public ConnectorsViewModel(AppServices services, AccountViewModel account)
    {
        _services = services;
        _account = account;
        RefreshCommand = new RelayCommand(RefreshAsync, () => !_busy);
        InstallAllCommand = new RelayCommand(InstallAllAsync, () => !_busy && _account.IsLoggedIn);
        account.LoggedInChanged += () => _ = RefreshAsync();
    }

    public ObservableCollection<ConnectorRowViewModel> Rows { get; } = [];
    public string Banner { get => _banner; private set { Set(ref _banner, value); Raise(nameof(HasBanner)); } }
    public bool HasBanner => !string.IsNullOrEmpty(Banner);
    public string InstallAllText { get => _installAllText; private set => Set(ref _installAllText, value); }
    public bool IsBatchInstalling { get => _isBatchInstalling; private set => Set(ref _isBatchInstalling, value); }
    public double BatchProgress { get => _batchProgress; private set => Set(ref _batchProgress, value); }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand InstallAllCommand { get; }

    public async Task RefreshAsync()
    {
        Banner = _account.IsLoggedIn ? "" : "请先在「账号」页登录，登录后可安装与检查更新";
        var installations = await Task.Run(() => new MaxInstallationDetector(new WindowsMaxRegistryReader()).Detect());

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            Rows.Clear();
            foreach (var max in installations)
                Rows.Add(new ConnectorRowViewModel(this, max));
        });
        if (installations.Count == 0)
        {
            Banner = "未检测到受支持的 3ds Max（2019-2026）安装";
            return;
        }

        foreach (var row in Rows.ToList())
            await RefreshRowStatusAsync(row);
        UpdateCountChanged?.Invoke(Rows.Count(r => r.Status == ConnectorStatus.UpdateAvailable));
    }

    private async Task RefreshRowStatusAsync(ConnectorRowViewModel row)
    {
        var installed = _services.Ledger.Find(ConnectorInstaller.ArtifactId, row.Installation.Year);
        if (installed is null)
        {
            row.Status = ConnectorStatus.NotInstalled;
            row.StatusText = "未安装";
        }
        else
        {
            row.Status = ConnectorStatus.Installed;
            row.StatusText = $"已安装 v{installed.Version}";
        }

        if (!_account.IsLoggedIn)
            return;
        try
        {
            var candidates = await _services.Hub.GetConnectorsAsync(row.Installation.Year);
            var latest = candidates.OrderByDescending(c => Version.Parse(c.Version)).FirstOrDefault();
            if (latest is not null && installed is not null && latest.Version != installed.Version)
            {
                row.Status = ConnectorStatus.UpdateAvailable;
                row.StatusText = $"可更新 → v{latest.Version}";
            }
        }
        catch
        {
            // 更新检查失败不阻塞本地状态展示
        }
    }

    internal async Task InstallAsync(ConnectorRowViewModel row)
    {
        if (!_account.IsLoggedIn)
        {
            row.ErrorMessage = "请先在「账号」页登录";
            return;
        }
        row.ErrorMessage = "";
        row.Status = ConnectorStatus.Installing;
        row.Progress = 0;
        row.StatusText = "◌ 安装中 0%";
        using var animCts = new CancellationTokenSource();
        var animation = AnimateProgressAsync(row, animCts.Token);
        // 真实下载进度映射到 0-80%，与模拟值取大，包体变大时进度仍真实可信
        var download = new Progress<double>(p => row.Progress = Math.Max(row.Progress, Math.Min(80, p * 0.8)));
        try
        {
            var installer = new ConnectorInstaller(_services.AgentRoot, _services.Resolver, _services.Ledger, _services.Hub);
            var result = (await Task.Run(() => installer.SyncAsync([row.Installation], download))).Single();
            animCts.Cancel();
            await animation;
            if (result.Success)
            {
                await CompleteProgressAsync(row);
                await RefreshRowStatusAsync(row);
                UpdateCountChanged?.Invoke(Rows.Count(r => r.Status == ConnectorStatus.UpdateAvailable));
            }
            else
            {
                row.Status = ConnectorStatus.Failed;
                row.StatusText = "安装失败";
                row.ErrorMessage = $"⚠ {result.Message}";
            }
        }
        catch (IOException)
        {
            row.Status = ConnectorStatus.Failed;
            row.StatusText = "安装失败";
            row.ErrorMessage = $"⚠ 3ds Max {row.Installation.Year} 可能正在运行，请先关闭 Max 再点击重试";
        }
        catch (Exception ex)
        {
            row.Status = ConnectorStatus.Failed;
            row.StatusText = "安装失败";
            row.ErrorMessage = $"⚠ 安装失败（{AccountViewModel.Brief(ex)}），可点击重试或联系 TA 组";
        }
        finally
        {
            animCts.Cancel();
        }
    }

    /// <summary>模拟进度：每 50ms 随机 +0.5～1.5%，封顶 95%，真实完成后由 CompleteProgress 冲刺到 100%。</summary>
    private static async Task AnimateProgressAsync(ConnectorRowViewModel row, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && row.Progress < 95)
            {
                await Task.Delay(50, ct);
                row.Progress = Math.Min(95, row.Progress + 0.5 + Random.Shared.NextDouble());
                row.StatusText = $"◌ 安装中 {(int)row.Progress}%";
            }
        }
        catch (TaskCanceledException) { }
    }

    private static async Task CompleteProgressAsync(ConnectorRowViewModel row)
    {
        while (row.Progress < 100)
        {
            row.Progress = Math.Min(100, row.Progress + 6);
            row.StatusText = $"◌ 安装中 {(int)row.Progress}%";
            await Task.Delay(25);
        }
    }

    internal async Task UninstallAsync(ConnectorRowViewModel row)
    {
        row.ErrorMessage = "";
        try
        {
            var installer = new ConnectorInstaller(_services.AgentRoot, _services.Resolver, _services.Ledger, _services.Hub);
            await Task.Run(() => installer.Uninstall(row.Installation.Year));
            await RefreshRowStatusAsync(row);
        }
        catch (Exception ex)
        {
            row.ErrorMessage = $"⚠ 卸载失败（{AccountViewModel.Brief(ex)}）";
        }
    }

    private async Task InstallAllAsync()
    {
        var pending = Rows.Where(r => r.Status is ConnectorStatus.NotInstalled or ConnectorStatus.UpdateAvailable or ConnectorStatus.Failed).ToList();
        if (pending.Count == 0)
            return;
        _busy = true;
        IsBatchInstalling = true;
        BatchProgress = 0;
        InstallAllCommand.RaiseCanExecuteChanged();
        try
        {
            for (var i = 0; i < pending.Count; i++)
            {
                InstallAllText = $"全部安装中… ({i + 1}/{pending.Count})";
                var row = pending[i];
                var completedBase = i * 100.0 / pending.Count;
                void OnRowProgress(object? _, System.ComponentModel.PropertyChangedEventArgs e)
                {
                    if (e.PropertyName == nameof(ConnectorRowViewModel.Progress))
                        BatchProgress = completedBase + row.Progress / pending.Count;
                }
                row.PropertyChanged += OnRowProgress;
                try
                {
                    await InstallAsync(row); // 串行执行，避免并发写 agentRoot
                }
                finally
                {
                    row.PropertyChanged -= OnRowProgress;
                }
                BatchProgress = (i + 1) * 100.0 / pending.Count;
            }
        }
        finally
        {
            _busy = false;
            IsBatchInstalling = false;
            BatchProgress = 0;
            InstallAllText = "全部安装";
            InstallAllCommand.RaiseCanExecuteChanged();
        }
    }
}
