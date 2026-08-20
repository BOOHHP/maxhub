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

            // 安装为异步任务：返回 job|{id}，轮询状态直到终态
            static async Task<string> InstallAndWaitAsync(HttpClient panel, string query)
            {
                var install = await panel.PostAsync($"/max/install?{query}", null);
                Assert.Equal(HttpStatusCode.OK, install.StatusCode);
                var body = await install.Content.ReadAsStringAsync();
                Assert.StartsWith("job|", body);
                var jobId = body.Split('|')[1];
                for (var i = 0; i < 100; i++)
                {
                    var status = await (await panel.GetAsync($"/max/install-status?jobId={jobId}")).Content.ReadAsStringAsync();
                    if (!status.StartsWith("running", StringComparison.Ordinal))
                        return status;
                    await Task.Delay(100);
                }
                return "error|轮询超时";
            }

            var result = await InstallAndWaitAsync(panel, "toolId=com.company.scene-batch-renamer&version=1.4.0&maxYear=2024");
            Assert.StartsWith("ok|", result);

            var resolver = new DefaultMaxPathResolver(Path.Combine(agentRoot, "maxuser"));
            Assert.True(File.Exists(Path.Combine(resolver.Resolve(2024, "userScripts"), "SceneBatchRenamer.ms")));

            // 兼容旧账本：移除 DisplayName 后仍应从市场补全名称，而不是暴露内部 ID
            var ledgerPath = Path.Combine(agentRoot, "installed.json");
            var ledgerJson = await File.ReadAllTextAsync(ledgerPath);
            ledgerJson = System.Text.RegularExpressions.Regex.Replace(
                ledgerJson, "\\s*\"displayName\"\\s*:\\s*\"[^\"]*\",?", "");
            await File.WriteAllTextAsync(ledgerPath, ledgerJson);
            var installed = await panel.GetStringAsync("/max/installed?maxYear=2024");
            Assert.Contains("com.company.scene-batch-renamer|1.4.0|Scene Batch Renamer", installed);

            // 运行入口：返回脚本类型与绝对路径
            var runInfo = await panel.GetStringAsync("/max/run-info?artifactId=com.company.scene-batch-renamer&maxYear=2024");
            var runParts = runInfo.Split('|');
            Assert.Equal("ok", runParts[0]);
            Assert.Equal("ms", runParts[1]);
            Assert.True(File.Exists(runParts[2]));

            var noRunInfo = await panel.GetAsync("/max/run-info?artifactId=com.x.none&maxYear=2024");
            Assert.Equal(HttpStatusCode.NotFound, noRunInfo.StatusCode);

            // 不存在的工具：任务终态为 error
            var badResult = await InstallAndWaitAsync(panel, "toolId=com.x.none&version=1.0.0&maxYear=2024");
            Assert.StartsWith("error|", badResult);
        }
        finally
        {
            await app.StopAsync();
            Directory.Delete(agentRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Panel_protocol_uninstall_rollback_and_updates()
    {
        // 服务端准备：发布 v1.0.0 并上架（复制样本并改写 manifest 版本号）
        var publisher = await LoginAsync("emp-pub", "发布者");
        var packDir = Directory.CreateTempSubdirectory("maxhub-svc-pack2").FullName;
        string releaseV1;
        try
        {
            var v1Dir = Path.Combine(packDir, "v1");
            CopyDir(Path.Combine(RepoRoot, "samples", "tools", "scene-batch-renamer"), v1Dir);
            RewriteManifestVersion(Path.Combine(v1Dir, "manifest.json"), "1.0.0");
            var packed = ToolPackage.Pack(v1Dir, Path.Combine(packDir, "pkg-v1.zip"));
            var outcome = await publisher.PublishAsync(packed.ZipPath);
            Assert.True(outcome.Success, string.Join(";", outcome.Errors));
            releaseV1 = outcome.ReleaseId!;
        }
        finally
        {
            Directory.Delete(packDir, recursive: true);
        }
        var reviewer = await LoginAsync("emp-rev", "审核者");
        await reviewer.ReviewAsync(releaseV1, approve: true, "stable");

        var agentRoot = Directory.CreateTempSubdirectory("maxhub-svc-agent2").FullName;
        var artistHub = await LoginAsync("emp-artist-svc2", "美术小赵");
        var app = AgentLocalServer.Build(agentRoot, new DefaultMaxPathResolver(Path.Combine(agentRoot, "maxuser")), artistHub, port: 0);
        await app.StartAsync();
        try
        {
            var baseUrl = AgentLocalServer.GetBoundAddress(app);
            using var panel = new HttpClient { BaseAddress = new Uri(baseUrl) };

            // 安装 v1.0.0
            var install = await panel.PostAsync("/max/install?toolId=com.company.scene-batch-renamer&version=1.0.0&maxYear=2024", null);
            var jobId = (await install.Content.ReadAsStringAsync()).Split('|')[1];
            for (var i = 0; i < 100; i++)
            {
                var status = await (await panel.GetAsync($"/max/install-status?jobId={jobId}")).Content.ReadAsStringAsync();
                if (!status.StartsWith("running", StringComparison.Ordinal)) break;
                await Task.Delay(100);
            }

            // 已安装列表包含 v1.0.0
            var installed = await panel.GetStringAsync("/max/installed?maxYear=2024");
            Assert.Contains("com.company.scene-batch-renamer|1.0.0", installed);

            // 尚无更新（服务器只有 1.0.0）
            var updates = await panel.GetStringAsync("/max/updates?maxYear=2024");
            Assert.DoesNotContain("com.company.scene-batch-renamer", updates);

            // 发布 v1.1.0 并上架 → 更新检测应出现
            var packDir2 = Directory.CreateTempSubdirectory("maxhub-svc-pack3").FullName;
            string releaseV2;
            try
            {
                var v2Dir = Path.Combine(packDir2, "v2");
                CopyDir(Path.Combine(RepoRoot, "samples", "tools", "scene-batch-renamer"), v2Dir);
                RewriteManifestVersion(Path.Combine(v2Dir, "manifest.json"), "1.1.0");
                var packed2 = ToolPackage.Pack(v2Dir, Path.Combine(packDir2, "pkg-v2.zip"));
                var outcome2 = await publisher.PublishAsync(packed2.ZipPath);
                Assert.True(outcome2.Success, string.Join(";", outcome2.Errors));
                releaseV2 = outcome2.ReleaseId!;
            }
            finally
            {
                Directory.Delete(packDir2, recursive: true);
            }
            await reviewer.ReviewAsync(releaseV2, approve: true, "stable");

            updates = await panel.GetStringAsync("/max/updates?maxYear=2024");
            Assert.Contains("com.company.scene-batch-renamer|1.0.0|1.1.0", updates);

            // 回滚：当前是 1.0.0（首次安装无上一版），回滚等价于卸载
            var rollback = await panel.PostAsync("/max/rollback?artifactId=com.company.scene-batch-renamer&maxYear=2024", null);
            Assert.StartsWith("ok|", await rollback.Content.ReadAsStringAsync());
            installed = await panel.GetStringAsync("/max/installed?maxYear=2024");
            Assert.DoesNotContain("com.company.scene-batch-renamer", installed);

            // 重新安装 1.0.0，再升级到 1.1.0，验证更新安装
            install = await panel.PostAsync("/max/install?toolId=com.company.scene-batch-renamer&version=1.0.0&maxYear=2024", null);
            jobId = (await install.Content.ReadAsStringAsync()).Split('|')[1];
            for (var i = 0; i < 100; i++)
            {
                var status = await (await panel.GetAsync($"/max/install-status?jobId={jobId}")).Content.ReadAsStringAsync();
                if (!status.StartsWith("running", StringComparison.Ordinal)) break;
                await Task.Delay(100);
            }
            install = await panel.PostAsync("/max/install?toolId=com.company.scene-batch-renamer&version=1.1.0&maxYear=2024", null);
            jobId = (await install.Content.ReadAsStringAsync()).Split('|')[1];
            for (var i = 0; i < 100; i++)
            {
                var status = await (await panel.GetAsync($"/max/install-status?jobId={jobId}")).Content.ReadAsStringAsync();
                if (!status.StartsWith("running", StringComparison.Ordinal)) break;
                await Task.Delay(100);
            }
            installed = await panel.GetStringAsync("/max/installed?maxYear=2024");
            Assert.Contains("com.company.scene-batch-renamer|1.1.0", installed);

            // 升级后无更新
            updates = await panel.GetStringAsync("/max/updates?maxYear=2024");
            Assert.DoesNotContain("com.company.scene-batch-renamer", updates);

            // 卸载
            var uninstall = await panel.PostAsync("/max/uninstall?artifactId=com.company.scene-batch-renamer&maxYear=2024", null);
            Assert.StartsWith("ok|", await uninstall.Content.ReadAsStringAsync());
            installed = await panel.GetStringAsync("/max/installed?maxYear=2024");
            Assert.DoesNotContain("com.company.scene-batch-renamer", installed);
        }
        finally
        {
            await app.StopAsync();
            Directory.Delete(agentRoot, recursive: true);
        }
    }

    private static void CopyDir(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var sourcePath in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(source, sourcePath);
            var destinationPath = Path.Combine(dest, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath);
        }
    }

    private static void RewriteManifestVersion(string manifestPath, string version)
    {
        var json = File.ReadAllText(manifestPath);
        json = System.Text.RegularExpressions.Regex.Replace(json, "\"version\"\\s*:\\s*\"[^\"]*\"", $"\"version\": \"{version}\"");
        File.WriteAllText(manifestPath, json);
    }
}
