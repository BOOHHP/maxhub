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

        app.MapPost("/max/install", async (string toolId, string version, int maxYear) =>
        {
            try
            {
                var plan = await hub.GetInstallPlanAsync(toolId, version);
                var zipPath = Path.Combine(agentRoot, "cache", $"{toolId}-{version}.zip");
                await hub.DownloadToolAsync(toolId, version, zipPath);

                var outcome = engine.Install(zipPath, plan.Sha256, maxYear);
                if (!outcome.Success)
                    return Results.Text($"error {outcome.Error}", statusCode: 409);

                await hub.PostInstallEventAsync(Guid.NewGuid().ToString("N"), "install", $"{toolId}@{version}", "agent-service/0.1");
                return Results.Text($"ok {toolId} {version}");
            }
            catch (Exception ex)
            {
                return Results.Text($"error {ex.Message}", statusCode: 502);
            }
        });

        return app;
    }

    /// <summary>测试用：获取实际绑定地址（端口 0 时由 Kestrel 分配）。</summary>
    public static string GetBoundAddress(WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
}
