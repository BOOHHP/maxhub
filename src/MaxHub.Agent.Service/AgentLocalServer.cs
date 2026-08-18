using System.Net;
using MaxHub.Agent.Core.Install;
using MaxHub.Agent.Core.Paths;
using MaxHub.Agent.Core.Remote;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MaxHub.Agent.Service;

/// <summary>
/// Agent 本地接口：仅绑定 127.0.0.1，供 Max 内 Connector 调用。
/// /max/* 端点返回竖线分隔的纯文本，便于 MaxScript 解析，不构成对外 API。
/// </summary>
public static class AgentLocalServer
{
    public const int DefaultPort = 47810;

    public static WebApplication Build(string agentRoot, IMaxPathResolver pathResolver, HubClient hub, int port = DefaultPort)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
        builder.Logging.ClearProviders();
        var app = builder.Build();

        var ledger = new LedgerStore(Path.Combine(agentRoot, "installed.json"));
        var engine = new InstallEngine(agentRoot, pathResolver, ledger);

        app.MapGet("/health", () => Results.Text("ok"));

        app.MapGet("/max/tools", async (int maxYear) =>
        {
            try
            {
                var tools = await hub.GetToolsAsync(maxYear);
                var lines = tools.Select(t => $"{t.ToolId}|{t.LatestVersion}|{t.Name}");
                return Results.Text(string.Join("\n", lines));
            }
            catch (Exception ex)
            {
                return Results.Text($"error {ex.Message}", statusCode: 502);
            }
        });

        app.MapGet("/max/installed", (int maxYear) =>
        {
            var lines = ledger.Load().Entries
                .Where(e => e.MaxVersion == maxYear && e.Active && e.ArtifactType == "tool")
                .Select(e => $"{e.ArtifactId}|{e.Version}");
            return Results.Text(string.Join("\n", lines));
        });

        // 安装改为异步任务：POST 返回 job|{id}，面板轮询 /max/install-status 获取进度
        var jobs = new System.Collections.Concurrent.ConcurrentDictionary<string, InstallJob>();

        app.MapPost("/max/install", (string toolId, string version, int maxYear) =>
        {
            var jobId = Guid.NewGuid().ToString("N");
            var job = new InstallJob();
            jobs[jobId] = job;

            _ = Task.Run(async () =>
            {
                // 模拟层：小包秒装时仍有平滑推进感（与托盘同策略：50ms 随机 +0.5~1.5%，封顶 95%）
                var ticker = Task.Run(async () =>
                {
                    while (job.State == "running" && job.Progress < 95)
                    {
                        await Task.Delay(50);
                        job.Progress = Math.Min(95, job.Progress + 0.5 + Random.Shared.NextDouble());
                    }
                });
                try
                {
                    var plan = await hub.GetInstallPlanAsync(toolId, version);
                    var zipPath = Path.Combine(agentRoot, "cache", $"{toolId}-{version}.zip");
                    // 真实下载字节映射到 0-80%，与模拟值取大
                    var download = new Progress<double>(p => job.Progress = Math.Max(job.Progress, Math.Min(80, p * 0.8)));
                    await hub.DownloadToolAsync(toolId, version, zipPath, download);

                    var publicKey = await TrustedKeyStore.GetOrPinAsync(hub, agentRoot);
                    if (!MaxHub.Core.Packaging.PackageSignature.Verify(publicKey, plan.Sha256, plan.Signature))
                    {
                        File.Delete(zipPath);
                        job.Message = "制品签名校验失败，拒绝安装。";
                        job.State = "error";
                        return;
                    }

                    var outcome = engine.Install(zipPath, plan.Sha256, maxYear);
                    if (!outcome.Success)
                    {
                        job.Message = outcome.Error ?? "安装失败";
                        job.State = "error";
                        return;
                    }
                    await hub.PostInstallEventAsync(Guid.NewGuid().ToString("N"), "install", $"{toolId}@{version}", "agent-service/0.1");
                    job.Progress = 100;
                    job.Message = $"{toolId} {version}";
                    job.State = "ok";
                }
                catch (Exception ex)
                {
                    job.Message = ex.Message;
                    job.State = "error";
                }
                finally
                {
                    await ticker;
                }
            });

            return Results.Text($"job|{jobId}");
        });

        app.MapGet("/max/install-status", (string jobId) =>
        {
            if (!jobs.TryGetValue(jobId, out var job))
                return Results.Text("error|未知任务", statusCode: 404);
            if (job.State == "running")
                return Results.Text($"running|{(int)job.Progress}");
            jobs.TryRemove(jobId, out _); // 终态只投递一次
            return Results.Text($"{job.State}|{job.Message}");
        });

        return app;
    }

    private sealed class InstallJob
    {
        public volatile string State = "running"; // running | ok | error
        public double Progress;
        public string Message = "";
    }

    /// <summary>测试用：获取实际绑定地址（端口 0 时由 Kestrel 分配）。</summary>
    public static string GetBoundAddress(WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
}
