using System.Net.Http.Json;
using MaxHub.Agent.Core.Install;
using MaxHub.Agent.Core.Paths;
using MaxHub.Agent.Core.Remote;
using MaxHub.Core.Packaging;

namespace MaxHub.Server.Tests;

/// <summary>
/// 阶段 2 端到端验收：Agent 通过飞书扫码登录 → 同步索引 → 取安装计划 →
/// 下载 → 哈希校验 → 事务安装 → 上报安装事件，全程走真实 HTTP 协议。
/// </summary>
public class AgentEndToEndTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private static string RepoRoot { get; } = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MaxHub.sln")))
            dir = dir.Parent!;
        return dir!.FullName;
    }

    [Fact]
    public async Task Agent_full_flow_from_login_to_installed_tool()
    {
        // --- 服务端准备：发布并审核通过样本工具 ---
        var publisherHttp = fixture.CreateClient();
        var publisher = new HubClient(publisherHttp);
        var pubQr = await publisher.CreateQrSessionAsync();
        await publisherHttp.PostAsJsonAsync($"/api/v1/auth/feishu/qr-sessions/{pubQr.SessionId}/mock-authorize",
            new { employeeId = "emp-pub", username = "发布者" });
        Assert.NotNull(await publisher.PollQrAsync(pubQr.SessionId));

        var sampleDir = Path.Combine(RepoRoot, "samples", "tools", "scene-batch-renamer");
        var packDir = Directory.CreateTempSubdirectory("maxhub-e2e-pack").FullName;
        string releaseId;
        try
        {
            var packed = ToolPackage.Pack(sampleDir, Path.Combine(packDir, "pkg.zip"));
            var content = new MultipartFormDataContent
            {
                { new ByteArrayContent(File.ReadAllBytes(packed.ZipPath)), "package", "pkg.zip" },
            };
            var upload = await publisherHttp.PostAsync("/api/v1/publish/releases", content);
            upload.EnsureSuccessStatusCode();
            releaseId = (await upload.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("releaseId").GetString()!;
        }
        finally
        {
            Directory.Delete(packDir, recursive: true);
        }

        var reviewerHttp = fixture.CreateClient();
        var reviewer = new HubClient(reviewerHttp);
        var revQr = await reviewer.CreateQrSessionAsync();
        await reviewerHttp.PostAsJsonAsync($"/api/v1/auth/feishu/qr-sessions/{revQr.SessionId}/mock-authorize",
            new { employeeId = "emp-rev", username = "审核者" });
        await reviewer.PollQrAsync(revQr.SessionId);
        (await reviewerHttp.PostAsJsonAsync($"/api/v1/releases/{releaseId}/review", new { approve = true, channel = "stable" }))
            .EnsureSuccessStatusCode();

        // --- Agent 侧：美术用户扫码登录并安装 ---
        var agentHttp = fixture.CreateClient();
        var hub = new HubClient(agentHttp);
        var qr = await hub.CreateQrSessionAsync();
        Assert.StartsWith("maxhub-mock://qr/", qr.AuthorizeUrl);
        Assert.Null(await hub.PollQrAsync(qr.SessionId)); // 未扫码前 pending

        await agentHttp.PostAsJsonAsync($"/api/v1/auth/feishu/qr-sessions/{qr.SessionId}/mock-authorize",
            new { employeeId = "emp-artist", username = "美术小王" });
        var session = await hub.PollQrAsync(qr.SessionId);
        Assert.NotNull(session);
        Assert.Equal("美术小王", session.Username);

        var tools = await hub.GetToolsAsync(2024);
        var tool = Assert.Single(tools, t => t.ToolId == "com.company.scene-batch-renamer");

        var plan = await hub.GetInstallPlanAsync(tool.ToolId, tool.LatestVersion);
        Assert.Equal("low", plan.RiskLevel);

        var agentRoot = Directory.CreateTempSubdirectory("maxhub-e2e-agent").FullName;
        try
        {
            var zipPath = Path.Combine(agentRoot, "cache", $"{tool.ToolId}-{plan.Version}.zip");
            await hub.DownloadToolAsync(tool.ToolId, plan.Version, zipPath);

            var resolver = new DefaultMaxPathResolver(Path.Combine(agentRoot, "maxuser"));
            var engine = new InstallEngine(agentRoot, resolver, new LedgerStore(Path.Combine(agentRoot, "installed.json")));
            var outcome = engine.Install(zipPath, plan.Sha256, 2024);

            Assert.True(outcome.Success, outcome.Error);
            Assert.True(File.Exists(Path.Combine(resolver.Resolve(2024, "userScripts"), "SceneBatchRenamer.ms")));

            await hub.PostInstallEventAsync(Guid.NewGuid().ToString("N"), "install", $"{tool.ToolId}@{plan.Version}", "agent-e2e/0.1");
        }
        finally
        {
            Directory.Delete(agentRoot, recursive: true);
        }
    }
}
