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
    ["tools", var server, var year] => await Tools(server, int.Parse(year)),
    ["sync-connectors", var server] => await SyncConnectors(server),
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
    Console.WriteLine($"请用飞书扫码授权: {qr.AuthorizeUrl}");
    for (var i = 0; i < 60; i++)
    {
        await Task.Delay(TimeSpan.FromSeconds(2));
        if (await hub.PollQrAsync(qr.SessionId) is { } session)
        {
            SaveToken(session.AccessToken);
            Console.WriteLine($"登录成功：{session.Username} ({session.EmployeeId})");
            return 0;
        }
    }
    Console.WriteLine("扫码超时。");
    return 1;
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
          tools <server> <maxYear>                   列出兼容工具
          sync-connectors <server>                   为本机所有 Max 安装匹配的 Connector
          install <server> <toolId> <version> <maxYear>
          uninstall <toolId> <maxYear>
          rollback <toolId> <maxYear>
          status                                     查看安装账本
        """);
    return 1;
}

HttpClient CreateHttp(string server, bool withToken)
{
    var http = new HttpClient { BaseAddress = new Uri(server) };
    if (withToken)
        http.DefaultRequestHeaders.Authorization = new("Bearer", LoadToken());
    return http;
}

void SaveToken(string token)
{
    Directory.CreateDirectory(agentRoot);
    var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser);
    File.WriteAllText(settingsPath, JsonSerializer.Serialize(new { accessToken = Convert.ToBase64String(protectedBytes) }));
}

string LoadToken()
{
    if (!File.Exists(settingsPath))
        throw new InvalidOperationException("尚未登录，请先执行 login。");
    var json = JsonDocument.Parse(File.ReadAllText(settingsPath));
    var protectedBytes = Convert.FromBase64String(json.RootElement.GetProperty("accessToken").GetString()!);
    return Encoding.UTF8.GetString(ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser));
}
