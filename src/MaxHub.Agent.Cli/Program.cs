using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MaxHub.Agent.Core.Detection;
using MaxHub.Agent.Core.Install;
using MaxHub.Agent.Core.Paths;
using MaxHub.Agent.Core.Remote;

var agentRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MaxHub");
var settingsPath = Path.Combine(agentRoot, "agent-settings.json");
var resolver = new DefaultMaxPathResolver();
var engine = new InstallEngine(agentRoot, resolver, new LedgerStore(Path.Combine(agentRoot, "installed.json")));

return args switch
{
    ["detect"] => Detect(),
    ["login", var server] => await Login(server),
    ["pack", var sourceDir, var outputZip] => Pack(sourceDir, outputZip),
    ["publish", var server, var zipPath] => await Publish(server, zipPath),
    ["review", var server, var releaseId, var action, var channel] => await Review(server, releaseId, action, channel),
    ["register-connector", var server, var zipPath, var version, var minYear, var maxYear] => await RegisterConnector(server, zipPath, version, int.Parse(minYear), int.Parse(maxYear)),
    ["tools", var server, var year] => await Tools(server, int.Parse(year)),
    ["sync-connectors", var server] => await SyncConnectors(server),
    ["uninstall-connector", var year] => UninstallConnector(int.Parse(year)),
    ["serve", var server] => await Serve(server),
    ["install", var server, var toolId, var version, var year] => await Install(server, toolId, version, int.Parse(year)),
    ["uninstall", var toolId, var year] => Uninstall(toolId, int.Parse(year)),
    ["rollback", var toolId, var year] => Rollback(toolId, int.Parse(year)),
    ["status"] => Status(),
    _ => Usage(),
};

int Detect()
{
    var installations = new MaxInstallationDetector(new WindowsMaxRegistryReader()).Detect();
    if (installations.Count == 0)
    {
        Console.WriteLine("未检测到受支持的 3ds Max (2019-2026) 安装。");
        return 1;
    }
    foreach (var max in installations)
        Console.WriteLine($"3ds Max {max.Year}  {max.InstallDir}");
    return 0;
}

async Task<int> Login(string server)
{
    var hub = new HubClient(CreateHttp(server, withToken: false));
    var qr = await hub.CreateQrSessionAsync();

    if (qr.AuthorizeUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
    {
        // 真实飞书：打开浏览器扫码，本机监听重定向回调，把 code 回传服务端换码
        using var listener = LocalCallbackListener.Start();
        Console.WriteLine("正在打开浏览器，请用飞书扫码授权…");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(qr.AuthorizeUrl) { UseShellExecute = true });

        var callback = await listener.WaitForCallbackAsync(TimeSpan.FromMinutes(3));
        if (callback is null)
        {
            Console.WriteLine("授权超时或回调参数缺失。");
            return 1;
        }
        if (callback.State != qr.SessionId)
        {
            Console.WriteLine("state 校验失败，已终止登录。");
            return 1;
        }
        await hub.CompleteQrAsync(qr.SessionId, callback.Code, callback.State);
    }
    else
    {
        Console.WriteLine($"请用飞书扫码授权: {qr.AuthorizeUrl}");
    }

    for (var i = 0; i < 60; i++)
    {
        await Task.Delay(TimeSpan.FromSeconds(2));
        if (await hub.PollQrAsync(qr.SessionId) is { } session)
        {
            SaveTokens(session.AccessToken, session.RefreshToken, session.ExpiresAtUtc);
            Console.WriteLine($"登录成功：{session.Username} ({session.EmployeeId})");
            return 0;
        }
    }
    Console.WriteLine("扫码超时。");
    return 1;
}

int Pack(string sourceDir, string outputZip)
{
    var result = MaxHub.Core.Packaging.ToolPackage.Pack(sourceDir, outputZip);
    Console.WriteLine($"已打包: {result.ZipPath}");
    Console.WriteLine($"SHA-256: {result.Sha256}");
    return 0;
}

async Task<int> Publish(string server, string zipPath)
{
    var hub = new HubClient(CreateHttp(server, withToken: true));
    var outcome = await hub.PublishAsync(zipPath);
    if (!outcome.Success)
    {
        Console.WriteLine("发布被拒绝：");
        foreach (var error in outcome.Errors)
            Console.WriteLine($"  - {error}");
        return 1;
    }
    Console.WriteLine($"已提交待审核，releaseId: {outcome.ReleaseId}");
    return 0;
}

async Task<int> Review(string server, string releaseId, string action, string channel)
{
    var hub = new HubClient(CreateHttp(server, withToken: true));
    await hub.ReviewAsync(releaseId, action == "approve", channel);
    Console.WriteLine(action == "approve" ? $"已发布到 {channel}" : "已退回");
    return 0;
}

async Task<int> RegisterConnector(string server, string zipPath, string version, int minYear, int maxYear)
{
    var hub = new HubClient(CreateHttp(server, withToken: true));
    await hub.RegisterConnectorAsync(zipPath, version, minYear, maxYear);
    Console.WriteLine($"Connector {version} 已注册（Max {minYear}-{maxYear}）");
    return 0;
}

async Task<int> Tools(string server, int year)
{
    var hub = new HubClient(CreateHttp(server, withToken: true));
    foreach (var tool in await hub.GetToolsAsync(year))
        Console.WriteLine($"{tool.ToolId}  {tool.LatestVersion}  [{tool.Channel}]  {tool.Name}");
    return 0;
}

async Task<int> SyncConnectors(string server)
{
    var installations = new MaxInstallationDetector(new WindowsMaxRegistryReader()).Detect();
    if (installations.Count == 0)
    {
        Console.WriteLine("未检测到受支持的 3ds Max 安装，无需安装 Connector。");
        return 1;
    }
    var hub = new HubClient(CreateHttp(server, withToken: true));
    var installer = new ConnectorInstaller(agentRoot, resolver, new LedgerStore(Path.Combine(agentRoot, "installed.json")), hub);
    var results = await installer.SyncAsync(installations);
    foreach (var result in results)
        Console.WriteLine($"Max {result.MaxYear}: {(result.Success ? result.Version : "失败")} - {result.Message}");
    return results.All(r => r.Success) ? 0 : 1;
}

async Task<int> Serve(string server)
{
    var hub = new HubClient(CreateHttp(server, withToken: true));
    var app = MaxHub.Agent.Service.AgentLocalServer.Build(agentRoot, resolver, hub);
    Console.WriteLine($"MaxHub Agent 本地服务已启动: http://127.0.0.1:{MaxHub.Agent.Service.AgentLocalServer.DefaultPort}（Ctrl+C 停止）");
    await app.RunAsync();
    return 0;
}

int UninstallConnector(int year)
{
    var installer = new ConnectorInstaller(agentRoot, resolver, new LedgerStore(Path.Combine(agentRoot, "installed.json")), null!);
    var removed = installer.Uninstall(year);
    Console.WriteLine(removed ? $"已卸载 Max {year} 的 Connector" : "账本中无该年份的 Connector 记录");
    return removed ? 0 : 1;
}

async Task<int> Install(string server, string toolId, string version, int year)
{
    var hub = new HubClient(CreateHttp(server, withToken: true));
    var plan = await hub.GetInstallPlanAsync(toolId, version);
    Console.WriteLine($"风险等级 {plan.RiskLevel}，重启要求 {plan.RestartRequired}，大小 {plan.SizeBytes} 字节");

    var zipPath = Path.Combine(agentRoot, "cache", $"{toolId}-{version}.zip");
    await hub.DownloadToolAsync(toolId, version, zipPath);
    var outcome = engine.Install(zipPath, plan.Sha256, year);
    Console.WriteLine(outcome.Success ? $"已安装 {toolId} {version} 到 Max {year}" : $"失败：{outcome.Error}");
    if (outcome.Success)
        await hub.PostInstallEventAsync(Guid.NewGuid().ToString("N"), "install", $"{toolId}@{version}", "agent-cli/0.1");
    return outcome.Success ? 0 : 1;
}

int Uninstall(string toolId, int year)
{
    var outcome = engine.Uninstall(toolId, year);
    Console.WriteLine(outcome.Success ? $"已卸载 {toolId}" : $"失败：{outcome.Error}");
    foreach (var conflict in outcome.Conflicts)
        Console.WriteLine($"保留用户改动文件: {conflict}");
    return outcome.Success ? 0 : 1;
}

int Rollback(string toolId, int year)
{
    var outcome = engine.Rollback(toolId, year);
    Console.WriteLine(outcome.Success ? $"已回滚 {toolId} 至 {outcome.Entry?.Version ?? "已卸载"}" : $"失败：{outcome.Error}");
    return outcome.Success ? 0 : 1;
}

int Status()
{
    var ledger = new LedgerStore(Path.Combine(agentRoot, "installed.json")).Load();
    if (ledger.Entries.Count == 0)
    {
        Console.WriteLine("没有受管理的安装。");
        return 0;
    }
    foreach (var entry in ledger.Entries)
        Console.WriteLine($"{entry.ArtifactId}  {entry.Version}  Max {entry.MaxVersion}  文件 {entry.Files.Count}  {(entry.Active ? "激活" : "停用")}");
    return 0;
}

int Usage()
{
    Console.WriteLine("""
        MaxHub Agent CLI
          detect                                     检测本机 3ds Max 安装
          login <server>                             飞书扫码登录
          pack <sourceDir> <outputZip>               打包工具目录为 .dccc-tool.zip
          publish <server> <zipPath>                 上传待审核发布（需 publisher 角色）
          review <server> <releaseId> <approve|reject> <channel>  审核（需 reviewer 角色）
          register-connector <server> <zip> <version> <minYear> <maxYear>  注册 Connector（需 admin）
          tools <server> <maxYear>                   列出兼容工具
          sync-connectors <server>                   为本机所有 Max 安装匹配的 Connector
          uninstall-connector <maxYear>              卸载指定 Max 年份的 Connector
          serve <server>                             启动本地 Agent 服务（供 Max 内面板连接）
          install <server> <toolId> <version> <maxYear>
          uninstall <toolId> <maxYear>
          rollback <toolId> <maxYear>
          status                                     查看安装账本
        """);
    return 1;
}

HttpClient CreateHttp(string server, bool withToken)
{
    if (!withToken)
        return new HttpClient { BaseAddress = new Uri(server) };
    // 服务端重启会使内存中的访问令牌失效（本地未到期），收到 401 时强制刷新并重试一次
    var http = new HttpClient(new SessionRefreshHandler(() => RefreshSession(server))) { BaseAddress = new Uri(server) };
    http.DefaultRequestHeaders.Authorization = new("Bearer", LoadToken(server));
    return http;
}

void SaveTokens(string accessToken, string refreshToken, DateTimeOffset expiresAtUtc)
{
    Directory.CreateDirectory(agentRoot);
    File.WriteAllText(settingsPath, JsonSerializer.Serialize(new
    {
        accessToken = Protect(accessToken),
        refreshToken = Protect(refreshToken),
        expiresAtUtc,
    }));
}

/// <remarks>访问令牌临近到期时用持久化刷新令牌自动续期，服务端重启后无需重新扫码。</remarks>
string LoadToken(string server)
{
    if (!File.Exists(settingsPath))
        throw new InvalidOperationException("尚未登录，请先执行 login。");
    var json = JsonDocument.Parse(File.ReadAllText(settingsPath));
    var expiresAtUtc = json.RootElement.TryGetProperty("expiresAtUtc", out var exp)
        ? exp.GetDateTimeOffset()
        : DateTimeOffset.MinValue;
    if (expiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(1))
        return Unprotect(json.RootElement.GetProperty("accessToken").GetString()!);
    return RefreshSession(server);
}

string RefreshSession(string server)
{
    if (!File.Exists(settingsPath))
        throw new InvalidOperationException("尚未登录，请先执行 login。");
    var json = JsonDocument.Parse(File.ReadAllText(settingsPath));
    if (!json.RootElement.TryGetProperty("refreshToken", out var rt))
        throw new InvalidOperationException("会话已过期，请重新执行 login。");
    using var http = new HttpClient { BaseAddress = new Uri(server) };
    var response = http.PostAsJsonAsync("/api/v1/auth/sessions/refresh", new { refreshToken = Unprotect(rt.GetString()!) })
        .GetAwaiter().GetResult();
    if (!response.IsSuccessStatusCode)
        throw new InvalidOperationException("会话已过期，请重新执行 login。");
    var session = response.Content.ReadFromJsonAsync<JsonElement>().GetAwaiter().GetResult();
    var accessToken = session.GetProperty("accessToken").GetString()!;
    SaveTokens(accessToken, session.GetProperty("refreshToken").GetString()!, session.GetProperty("expiresAtUtc").GetDateTimeOffset());
    return accessToken;
}

string Protect(string value) =>
    Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));

string Unprotect(string value) =>
    Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser));

/// <summary>401 时用刷新令牌强制续期并重试一次（覆盖服务端重启使未到期访问令牌失效的场景）。</summary>
sealed class SessionRefreshHandler : DelegatingHandler
{
    private readonly Func<string> _forceRefresh;

    public SessionRefreshHandler(Func<string> forceRefresh) : base(new HttpClientHandler()) => _forceRefresh = forceRefresh;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode != System.Net.HttpStatusCode.Unauthorized)
            return response;

        string newToken;
        try { newToken = _forceRefresh(); }
        catch { return response; }

        response.Dispose();
        var retry = new HttpRequestMessage(request.Method, request.RequestUri) { Content = request.Content };
        retry.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", newToken);
        return await base.SendAsync(retry, cancellationToken);
    }
}
