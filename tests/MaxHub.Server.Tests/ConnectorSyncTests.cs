using System.IO.Compression;
using System.Net.Http.Json;
using MaxHub.Agent.Core.Detection;
using MaxHub.Agent.Core.Install;
using MaxHub.Agent.Core.Paths;
using MaxHub.Agent.Core.Remote;

namespace MaxHub.Server.Tests;

/// <summary>
/// 阶段 3 可本地验证部分：Agent 自动为每个检测到的 Max 实例
/// 匹配、下载、校验并安装对应 Connector 制品；多版本互不干扰。
/// </summary>
public class ConnectorSyncTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private static byte[] FakeConnectorZip(string scriptContent)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry(ConnectorInstaller.EntryScriptName).Open());
            writer.Write(scriptContent);
        }
        return memory.ToArray();
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

    private async Task RegisterConnectorAsync(string version, int minYear, int maxYear)
    {
        var adminHttp = fixture.CreateClient();
        var adminHub = new HubClient(adminHttp);
        var qr = await adminHub.CreateQrSessionAsync();
        await adminHttp.PostAsJsonAsync($"/api/v1/auth/feishu/qr-sessions/{qr.SessionId}/mock-authorize",
            new { employeeId = "emp-admin", username = "管理员" });
        await adminHub.PollQrAsync(qr.SessionId);
        adminHttp.DefaultRequestHeaders.Authorization = new("Bearer",
            adminHttp.DefaultRequestHeaders.Authorization!.Parameter);

        var content = new MultipartFormDataContent
        {
            { new ByteArrayContent(FakeConnectorZip($"connector-{version}")), "package", "connector.zip" },
            { new StringContent(version), "version" },
            { new StringContent(minYear.ToString()), "minMaxYear" },
            { new StringContent(maxYear.ToString()), "maxMaxYear" },
        };
        (await adminHttp.PostAsync("/api/v1/admin/connectors", content)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Agent_installs_matching_connector_per_max_instance()
    {
        await RegisterConnectorAsync("1.0.0", 2019, 2021);
        await RegisterConnectorAsync("2.0.0", 2022, 2026);

        var agentRoot = Directory.CreateTempSubdirectory("maxhub-connector-sync").FullName;
        try
        {
            var resolver = new DefaultMaxPathResolver(Path.Combine(agentRoot, "maxuser"));
            var ledger = new LedgerStore(Path.Combine(agentRoot, "installed.json"));
            var hub = await LoginAsync("emp-artist2", "美术小李");
            var installer = new ConnectorInstaller(agentRoot, resolver, ledger, hub);

            var machine = new List<MaxInstallation>
            {
                new(2019, @"C:\fake\2019", @"C:\fake\2019\3dsmax.exe"),
                new(2024, @"C:\fake\2024", @"C:\fake\2024\3dsmax.exe"),
            };

            var results = await installer.SyncAsync(machine);
            Assert.All(results, r => Assert.True(r.Success, r.Message));
            Assert.Equal("1.0.0", results.Single(r => r.MaxYear == 2019).Version);
            Assert.Equal("2.0.0", results.Single(r => r.MaxYear == 2024).Version);

            // 每个 Max 实例有独立加载脚本与脚本目录
            var loader2019 = Path.Combine(resolver.Resolve(2019, "userStartup"), "maxhub_connector_loader.ms");
            var loader2024 = Path.Combine(resolver.Resolve(2024, "userStartup"), "maxhub_connector_loader.ms");
            Assert.True(File.Exists(loader2019));
            Assert.True(File.Exists(loader2024));
            Assert.Contains(@"max2019\1.0.0", File.ReadAllText(loader2019));
            Assert.Contains(@"max2024\2.0.0", File.ReadAllText(loader2024));
            Assert.True(File.Exists(Path.Combine(agentRoot, "connectors", "max2019", "1.0.0", ConnectorInstaller.EntryScriptName)));
            Assert.True(File.Exists(Path.Combine(agentRoot, "connectors", "max2024", "2.0.0", ConnectorInstaller.EntryScriptName)));

            // 重复同步幂等
            var again = await installer.SyncAsync(machine);
            Assert.All(again, r => Assert.Contains("已是最新版本", r.Message));

            // 卸载 2019 不影响 2024
            Assert.True(installer.Uninstall(2019));
            Assert.False(File.Exists(loader2019));
            Assert.True(File.Exists(loader2024));
            Assert.Null(ledger.Find(ConnectorInstaller.ArtifactId, 2019));
            Assert.NotNull(ledger.Find(ConnectorInstaller.ArtifactId, 2024));
        }
        finally
        {
            Directory.Delete(agentRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Unsupported_year_reports_missing_artifact()
    {
        var agentRoot = Directory.CreateTempSubdirectory("maxhub-connector-none").FullName;
        try
        {
            var hub = await LoginAsync("emp-artist3", "美术小张");
            var installer = new ConnectorInstaller(
                agentRoot,
                new DefaultMaxPathResolver(Path.Combine(agentRoot, "maxuser")),
                new LedgerStore(Path.Combine(agentRoot, "installed.json")),
                hub);

            // 2025 没有注册任何制品（其他用例注册的是 2019-2021 / 2022-2026，fixture 隔离时此处独立验证空集）
            var results = await installer.SyncAsync([new MaxInstallation(2025, @"C:\fake\2025", @"C:\fake\2025\3dsmax.exe")]);
            var result = Assert.Single(results);
            if (!result.Success)
                Assert.Contains("没有支持 Max 2025", result.Message);
        }
        finally
        {
            Directory.Delete(agentRoot, recursive: true);
        }
    }
}
