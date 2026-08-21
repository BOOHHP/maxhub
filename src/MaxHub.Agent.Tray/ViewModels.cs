using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
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
    private string _updateStatus = "";
    private bool _checkingUpdate;
    private bool _downloadingUpdate;
    private double _updateProgress;
    private AgentReleaseInfo? _pendingUpdate;
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
        CheckUpdateCommand = new RelayCommand(CheckUpdateAsync, () => !_checkingUpdate && !_downloadingUpdate);
        UpdateCommand = new RelayCommand(UpdateAsync, () => _pendingUpdate is not null && !_downloadingUpdate);
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
    public RelayCommand CheckUpdateCommand { get; }
    public RelayCommand UpdateCommand { get; }

    public string UpdateStatus { get => _updateStatus; private set { Set(ref _updateStatus, value); Raise(nameof(HasUpdateStatus)); } }
    public bool HasUpdateStatus => !string.IsNullOrEmpty(UpdateStatus);
    public bool HasAvailableUpdate => _pendingUpdate is not null;
    public bool IsDownloadingUpdate { get => _downloadingUpdate; private set => Set(ref _downloadingUpdate, value); }
    public double UpdateProgress { get => _updateProgress; private set => Set(ref _updateProgress, value); }
    public string UpdateButtonText => _pendingUpdate is null ? "立即更新" : $"更新到 v{_pendingUpdate.Version}";

    /// <summary>手动检查更新：服务器 latest 优先，GitHub 直连回退；有新版则下载并退出交给替换脚本重启。</summary>
    private async Task CheckUpdateAsync()
    {
        _checkingUpdate = true;
        CheckUpdateCommand.RaiseCanExecuteChanged();
        try
        {
            var current = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3);
            SetPendingUpdate(null);
            UpdateProgress = 0;
            UpdateStatus = "◌ 正在检查更新…";
            var updater = new SelfUpdater(_services.Hub) { CurrentVersion = current };
            var release = await updater.CheckForUpdateAsync();
            if (release is null)
            {
                UpdateStatus = $"✓ 当前已是最新版本（v{current}）";
                return;
            }
            SetPendingUpdate(release);
            UpdateStatus = $"发现新版本 v{release.Version}，点击按钮开始更新";
        }
        catch (Exception ex)
        {
            UpdateStatus = $"✗ 检查失败（{Brief(ex)}）";
        }
        finally
        {
            _checkingUpdate = false;
            CheckUpdateCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task UpdateAsync()
    {
        if (_pendingUpdate is not { } release)
            return;
        IsDownloadingUpdate = true;
        UpdateProgress = 0;
        CheckUpdateCommand.RaiseCanExecuteChanged();
        UpdateCommand.RaiseCanExecuteChanged();
        try
        {
            var updater = new SelfUpdater(_services.Hub);
            var progress = new Progress<double>(value =>
            {
                UpdateProgress = value;
                UpdateStatus = $"正在下载 v{release.Version}… {(int)value}%";
            });
            await updater.DownloadAndInstallAsync(release, progress);
            UpdateProgress = 100;
            UpdateStatus = "下载完成，正在关闭并覆盖旧版本…";
            await ((App)System.Windows.Application.Current).ExitCleanlyAsync();
        }
        catch (Exception ex)
        {
            UpdateStatus = $"✗ 更新失败（{Brief(ex)}），可重试";
        }
        finally
        {
            IsDownloadingUpdate = false;
            CheckUpdateCommand.RaiseCanExecuteChanged();
            UpdateCommand.RaiseCanExecuteChanged();
        }
    }

    private void SetPendingUpdate(AgentReleaseInfo? release)
    {
        _pendingUpdate = release;
        Raise(nameof(HasAvailableUpdate));
        Raise(nameof(UpdateButtonText));
        UpdateCommand.RaiseCanExecuteChanged();
    }

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
        OpenCommand = new RelayCommand(OpenMax);
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

    public RelayCommand OpenCommand { get; }
    public RelayCommand ActionCommand { get; }

    private void OpenMax()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Installation.ExePath,
                WorkingDirectory = Installation.InstallDir,
                UseShellExecute = false,
            })?.Dispose();
            ErrorMessage = "";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"⚠ 无法启动 {DisplayName}（{AccountViewModel.Brief(ex)}）";
        }
    }

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

/// <summary>本机已安装工具的账本条目行。</summary>
public sealed class InstalledToolRowViewModel : ViewModelBase
{
    private readonly ToolsViewModel _owner;

    public InstalledToolRowViewModel(ToolsViewModel owner, LedgerEntry entry, string displayName)
    {
        _owner = owner;
        Entry = entry;
        DisplayName = displayName;
        UninstallCommand = new RelayCommand(() => owner.UninstallAsync(this));
    }

    public LedgerEntry Entry { get; }
    public string DisplayName { get; }
    public string ArtifactId => Entry.ArtifactId;
    public string Version => Entry.Version;
    public int MaxVersion => Entry.MaxVersion;
    public string InstalledAt => Entry.InstalledAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string Display => $"{DisplayName}  v{Entry.Version}  · Max {Entry.MaxVersion}  · {InstalledAt}";
    public RelayCommand UninstallCommand { get; }
}

/// <summary>服务器工具市场的索引行。</summary>
public sealed class MarketToolRowViewModel : ViewModelBase
{
    private readonly ToolsViewModel _owner;
    private string _statusText = "未安装";

    public MarketToolRowViewModel(ToolsViewModel owner, ToolIndexItem item)
    {
        _owner = owner;
        Item = item;
        InstallCommand = new RelayCommand(() => owner.InstallFromMarketAsync(this));
    }

    public ToolIndexItem Item { get; }
    public string Name => Item.Name;
    public string Description => Item.Description ?? "";
    public string LatestVersion => Item.LatestVersion;
    public string Channel => Item.Channel;
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
    public RelayCommand InstallCommand { get; }
}

/// <summary>工具管理页：本机工具 / 市场安装 / 脚本上传。</summary>
public sealed class ToolsViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private readonly AccountViewModel _account;
    private string _banner = "";
    private int _selectedMaxYear;
    private string _uploadFileName = "";
    private string _uploadName = "";
    private string _uploadDescription = "";
    private string _uploadVersion = "1.0.0";
    private int _uploadMinMaxYear = 2019;
    private int _uploadMaxMaxYear = 2026;
    private string _uploadStatus = "";
    private bool _uploadBusy;
    private bool _busy;
    private string? _pendingUploadContent;

    public ToolsViewModel(AppServices services, AccountViewModel account)
    {
        _services = services;
        _account = account;
        RefreshCommand = new RelayCommand(RefreshAsync, () => !_busy);
        RefreshInstalledCommand = new RelayCommand(RefreshInstalledAsync, () => !_busy);
        UninstallAllCommand = new RelayCommand(UninstallAllAsync, () => !_busy);
        PickFileCommand = new RelayCommand(PickFileAsync, () => !_uploadBusy);
        SubmitUploadCommand = new RelayCommand(SubmitUploadAsync, () => !_uploadBusy && _account.IsLoggedIn);
        account.LoggedInChanged += () => _ = RefreshAsync();
    }

    public ObservableCollection<InstalledToolRowViewModel> InstalledTools { get; } = [];
    public ObservableCollection<MarketToolRowViewModel> MarketTools { get; } = [];
    public ObservableCollection<int> MaxYears { get; } = [];
    /// <summary>上传兼容范围使用全量年份（2019-2026），而非仅本机安装的版本。</summary>
    public ObservableCollection<int> AllMaxYears { get; } = [.. Enumerable.Range(2019, 2026 - 2019 + 1)];

    public string Banner { get => _banner; private set { Set(ref _banner, value); Raise(nameof(HasBanner)); } }
    public bool HasBanner => !string.IsNullOrEmpty(Banner);

    public int SelectedMaxYear
    {
        get => _selectedMaxYear;
        set
        {
            if (Set(ref _selectedMaxYear, value))
                _ = LoadMarketAsync();
        }
    }

    public string UploadFileName { get => _uploadFileName; private set { Set(ref _uploadFileName, value); Raise(nameof(HasFile)); Raise(nameof(PickFileText)); } }
    public bool HasFile => !string.IsNullOrEmpty(UploadFileName);
    public string PickFileText => HasFile ? $"📁 {UploadFileName}" : "📁 选择脚本文件";
    public string UploadName { get => _uploadName; set => Set(ref _uploadName, value); }
    public string UploadDescription { get => _uploadDescription; set => Set(ref _uploadDescription, value); }
    public string UploadVersion { get => _uploadVersion; set => Set(ref _uploadVersion, value); }
    public int UploadMinMaxYear { get => _uploadMinMaxYear; set => Set(ref _uploadMinMaxYear, value); }
    public int UploadMaxMaxYear { get => _uploadMaxMaxYear; set => Set(ref _uploadMaxMaxYear, value); }
    public string UploadStatus { get => _uploadStatus; private set { Set(ref _uploadStatus, value); Raise(nameof(HasUploadStatus)); } }
    public bool HasUploadStatus => !string.IsNullOrEmpty(UploadStatus);
    public bool IsUploading => _uploadBusy;

    public RelayCommand RefreshCommand { get; }
    public RelayCommand RefreshInstalledCommand { get; }
    public RelayCommand UninstallAllCommand { get; }
    public RelayCommand PickFileCommand { get; }
    public RelayCommand SubmitUploadCommand { get; }

    public async Task RefreshAsync()
    {
        var installations = await Task.Run(() => new MaxInstallationDetector(new WindowsMaxRegistryReader()).Detect());
        Application.Current.Dispatcher.Invoke(() =>
        {
            MaxYears.Clear();
            foreach (var max in installations.OrderBy(i => i.Year))
                MaxYears.Add(max.Year);
        });
        if (MaxYears.Count > 0)
            SelectedMaxYear = MaxYears.Max();
        await RefreshInstalledAsync();
        await LoadMarketAsync();
    }

    public async Task RefreshInstalledAsync()
    {
        var entries = await Task.Run(() =>
            _services.Ledger.Load().Entries
                .Where(e => e.ArtifactType == "tool" && e.Active)
                .OrderBy(e => e.MaxVersion)
                .ThenBy(e => e.ArtifactId)
                .ToList());
        var names = entries
            .Where(e => !string.IsNullOrWhiteSpace(e.DisplayName))
            .GroupBy(e => e.ArtifactId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().DisplayName!, StringComparer.Ordinal);
        foreach (var year in entries.Where(e => !names.ContainsKey(e.ArtifactId)).Select(e => e.MaxVersion).Distinct())
        {
            try
            {
                foreach (var tool in await _services.Hub.GetToolsAsync(year))
                    names[tool.ToolId] = tool.Name;
            }
            catch
            {
                // 离线时仍展示账本中的名称；历史账本无名称则显示通用占位文本
            }
        }
        Application.Current.Dispatcher.Invoke(() =>
        {
            InstalledTools.Clear();
            foreach (var e in entries)
                InstalledTools.Add(new InstalledToolRowViewModel(
                    this, e, names.GetValueOrDefault(e.ArtifactId) ?? "未命名工具"));
        });
        Banner = entries.Count == 0
            ? _account.IsLoggedIn ? "本机尚未安装任何工具，可在「市场」中安装或从「上传」提交脚本" : "请先在「账号」页登录"
            : "";
    }

    private async Task LoadMarketAsync()
    {
        if (MaxYears.Count == 0 || SelectedMaxYear == 0)
            return;
        if (!_account.IsLoggedIn)
            return;
        try
        {
            var tools = await _services.Hub.GetToolsAsync(SelectedMaxYear);
            Application.Current.Dispatcher.Invoke(() =>
            {
                MarketTools.Clear();
                foreach (var t in tools)
                {
                    var row = new MarketToolRowViewModel(this, t);
                    row.StatusText = IsInstalled(t.ToolId) ? "已安装" : "未安装";
                    MarketTools.Add(row);
                }
            });
        }
        catch (Exception ex)
        {
            Banner = $"加载工具市场失败（{AccountViewModel.Brief(ex)}）";
        }
    }

    private bool IsInstalled(string toolId) =>
        _services.Ledger.Load().Entries.Any(e => e.ArtifactId == toolId && e.Active);

    /// <summary>从市场安装：拉取安装计划 → 下载包 → 事务安装。</summary>
    internal async Task InstallFromMarketAsync(MarketToolRowViewModel row)
    {
        if (!_account.IsLoggedIn)
        {
            row.StatusText = "请先登录";
            return;
        }
        row.StatusText = "◌ 获取安装计划…";
        try
        {
            var plan = await _services.Hub.GetInstallPlanAsync(row.Item.ToolId, row.Item.LatestVersion);
            var zipPath = Path.Combine(Path.GetTempPath(), $"maxhub-{row.Item.ToolId}-{row.Item.LatestVersion}.zip");
            row.StatusText = "◌ 下载中…";
            await _services.Hub.DownloadToolAsync(row.Item.ToolId, row.Item.LatestVersion, zipPath);

            var engine = new InstallEngine(_services.AgentRoot, _services.Resolver, _services.Ledger);
            row.StatusText = "◌ 安装中…";
            var outcome = await Task.Run(() => engine.Install(zipPath, plan.Sha256, SelectedMaxYear));
            File.Delete(zipPath);
            if (outcome.Success)
            {
                row.StatusText = "✓ 已安装";
                await RefreshInstalledAsync();
            }
            else
            {
                row.StatusText = "✗ 安装失败";
                Banner = $"工具 {row.Item.Name} 安装失败：{outcome.Error}";
            }
        }
        catch (Exception ex)
        {
            row.StatusText = "✗ 安装失败";
            Banner = $"工具 {row.Item.Name} 安装失败（{AccountViewModel.Brief(ex)}）";
        }
    }

    internal async Task UninstallAsync(InstalledToolRowViewModel row)
    {
        try
        {
            var engine = new InstallEngine(_services.AgentRoot, _services.Resolver, _services.Ledger);
            var outcome = await Task.Run(() => engine.Uninstall(row.ArtifactId, row.MaxVersion));
            if (!outcome.Success)
                Banner = $"卸载失败：{outcome.Error}";
            await RefreshInstalledAsync();
        }
        catch (Exception ex)
        {
            Banner = $"卸载失败（{AccountViewModel.Brief(ex)}）";
        }
    }

    private async Task UninstallAllAsync()
    {
        _busy = true;
        try
        {
            var engine = new InstallEngine(_services.AgentRoot, _services.Resolver, _services.Ledger);
            foreach (var row in InstalledTools.ToList())
            {
                await Task.Run(() => engine.Uninstall(row.ArtifactId, row.MaxVersion));
                await RefreshInstalledAsync();
            }
        }
        finally
        {
            _busy = false;
            RefreshInstalledCommand.RaiseCanExecuteChanged();
            UninstallAllCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>选择本地脚本文件：读取内容 → 服务端自动识别预填名称/描述。</summary>
    private async Task PickFileAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要上传的 3ds Max 脚本",
            Filter = "MaxScript 脚本 (*.ms)|*.ms|Python 脚本 (*.py)|*.py|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true)
            return;
        UploadFileName = Path.GetFileName(dialog.FileName);
        UploadStatus = "◌ 正在自动识别脚本信息…";
        try
        {
            _pendingUploadContent = await File.ReadAllTextAsync(dialog.FileName);
            var result = await _services.Hub.AnalyzeScriptAsync(UploadFileName, _pendingUploadContent);
            if (result is not null)
            {
                UploadName = result.Value.Name;
                UploadDescription = result.Value.Description;
            }
            UploadStatus = "✓ 已识别，请核对信息后提交（提交后进入审核）";
        }
        catch (Exception ex)
        {
            UploadStatus = $"自动识别失败（{AccountViewModel.Brief(ex)}），请手动填写";
        }
    }

    private async Task SubmitUploadAsync()
    {
        if (string.IsNullOrWhiteSpace(UploadFileName) || string.IsNullOrWhiteSpace(UploadName) ||
            string.IsNullOrWhiteSpace(UploadVersion))
        {
            UploadStatus = "请先选择脚本并填写名称与版本";
            return;
        }
        if (_pendingUploadContent is null)
        {
            UploadStatus = "请先选择脚本文件";
            return;
        }
        _uploadBusy = true;
        SubmitUploadCommand.RaiseCanExecuteChanged();
        try
        {
            UploadStatus = "◌ 提交中…";
            var outcome = await _services.Hub.PublishScriptAsync(
                UploadFileName, _pendingUploadContent, UploadName, UploadDescription,
                UploadVersion, UploadMinMaxYear, UploadMaxMaxYear);
            UploadStatus = outcome.Success
                ? $"✓ 已提交审核（Release {outcome.ReleaseId}），审核通过后即可在市场中安装"
                : $"✗ 提交失败：{string.Join("；", outcome.Errors)}";
        }
        catch (Exception ex)
        {
            UploadStatus = $"✗ 提交失败（{AccountViewModel.Brief(ex)}）";
        }
        finally
        {
            _uploadBusy = false;
            SubmitUploadCommand.RaiseCanExecuteChanged();
        }
    }
}

/// <summary>平台反馈页：内容由服务端路由给平台负责人并抄送管理员。</summary>
public sealed class FeedbackViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private readonly AccountViewModel _account;
    private string _message = "";
    private string _status = "";
    private bool _busy;

    public FeedbackViewModel(AppServices services, AccountViewModel account)
    {
        _services = services;
        _account = account;
        SubmitCommand = new RelayCommand(SubmitAsync, () => !_busy && _account.IsLoggedIn);
    }

    public string Message { get => _message; set { Set(ref _message, value); SubmitCommand.RaiseCanExecuteChanged(); } }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public RelayCommand SubmitCommand { get; }

    private async Task SubmitAsync()
    {
        var text = Message.Trim();
        if (text.Length < 5)
        {
            Status = "✗ 反馈内容至少 5 个字。";
            return;
        }
        _busy = true;
        SubmitCommand.RaiseCanExecuteChanged();
        Status = "◌ 提交中…";
        try
        {
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3);
            var outcome = await _services.Hub.SubmitFeedbackAsync("platform", null, text, "agent", version, null);
            Status = outcome.Success
                ? outcome.DeliveryStatus == "delivered"
                    ? "✓ 已提交并通过飞书送达，感谢反馈！"
                    : $"✓ 已保存（飞书通知状态：{outcome.DeliveryStatus}），管理员可在后台查看。"
                : $"✗ {outcome.Error}";
            if (outcome.Success)
                Message = "";
        }
        catch (Exception ex)
        {
            Status = $"✗ 提交失败（{AccountViewModel.Brief(ex)}）";
        }
        finally
        {
            _busy = false;
            SubmitCommand.RaiseCanExecuteChanged();
        }
    }
}
