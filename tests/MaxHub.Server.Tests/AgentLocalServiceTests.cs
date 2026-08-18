using System.Net;
using System.Net.Http.Json;
using MaxHub.Agent.Core.Paths;
using MaxHub.Agent.Core.Remote;
using MaxHub.Agent.Service;
using MaxHub.Core.Packaging;

namespace MaxHub.Server.Tests;

/// <summary>Agent 本地服务：Max 面板协议（/health、/max/tools、/max/install）的端到端验证。</summary>
public class AgentLocalServiceTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private static string RepoRoot { get; } = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MaxHub.sln")))
            dir = dir.Parent!;
        return dir!.FullName;
    }

    private async Task<HubClient> LoginAsync(string employeeId, string username)
    {
        var http = fixture.CreateClient();
        var hub = new HubClient(http);
        var qr = await hub.CreateQrSessionAsync();
        await http.PostAsJsonAsync($"/api/v1/auth/feishu/qr-sessions/{qr.SessionId}/mock-authorize", new { employeeId, username });
        Assert.NotNull(await hub.PollQrAsync(qr.SessionId));
        return hub;
    }

    [Fact]
    public async Task Panel_protocol_lists_and_installs_tools()
    {
        // 服务端准备：发布并上架样本工具
        var publisher = await LoginAsync("emp-pub", "发布者");
        var packDir = Directory.CreateTempSubdirectory("maxhub-svc-pack").FullName;
        string releaseId;
        try
        {
            var packed = ToolPackage.Pack(Path.Combine(RepoRoot, "samples", "tools", "scene-batch-renamer"), Path.Combine(packDir, "pkg.zip"));
            var outcome = await publisher.PublishAsync(packed.ZipPath);
            Assert.True(outcome.Success, string.Join(";", outcome.Errors));
            releaseId = outcome.ReleaseId!;
        }
        finally
        {
            Directory.Delete(packDir, recursive: true);
        }
        var reviewer = await LoginAsync("emp-rev", "审核者");
        await reviewer.ReviewAsync(releaseId, approve: true, "stable");

        // Agent 本地服务：端口 0 由 Kestrel 分配，仅回环
        var agentRoot = Directory.CreateTempSubdirectory("maxhub-svc-agent").FullName;
        var artistHub = await LoginAsync("emp-artist-svc", "美术小赵");
        var app = AgentLocalServer.Build(agentRoot, new DefaultMaxPathResolver(Path.Combine(agentRoot, "maxuser")), artistHub, port: 0);
        await app.StartAsync();
        try
        {
            var baseUrl = AgentLocalServer.GetBoundAddress(app);
            using var panel = new HttpClient { BaseAddress = new Uri(baseUrl) };

            Assert.Equal("ok", await panel.GetStringAsync("/health"));

            var tools = await panel.GetStringAsync("/max/tools?maxYear=2024");
            Assert.Contains("com.company.scene-batch-renamer|1.4.0|Scene Batch Renamer", tools);

            var install = await panel.PostAsync("/max/install?toolId=com.company.scene-batch-renamer&version=1.4.0&maxYear=2024", null);
            Assert.Equal(HttpStatusCode.OK, install.StatusCode);
            Assert.StartsWith("ok", await install.Content.ReadAsStringAsync());

            var resolver = new DefaultMaxPathResolver(Path.Combine(agentRoot, "maxuser"));
            Assert.True(File.Exists(Path.Combine(resolver.Resolve(2024, "userScripts"), "SceneBatchRenamer.ms")));

            var installed = await panel.GetStringAsync("/max/installed?maxYear=2024");
            Assert.Contains("com.company.scene-batch-renamer|1.4.0", installed);

            // 不存在的工具：错误以文本返回而非 500
            var bad = await panel.PostAsync("/max/install?toolId=com.x.none&version=1.0.0&maxYear=2024", null);
            Assert.NotEqual(HttpStatusCode.OK, bad.StatusCode);
            Assert.StartsWith("error", await bad.Content.ReadAsStringAsync());
        }
        finally
        {
            await app.StopAsync();
            Directory.Delete(agentRoot, recursive: true);
        }
    }
}
