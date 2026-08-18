using System.IO.Compression;
using MaxHub.Agent.Core.Detection;
using MaxHub.Agent.Core.Paths;
using MaxHub.Agent.Core.Remote;
using MaxHub.Core.Ledger;
using MaxHub.Core.Packaging;

namespace MaxHub.Agent.Core.Install;

public sealed record ConnectorSyncResult(int MaxYear, bool Success, string? Version, string Message);

/// <summary>
/// Connector 是 MaxHub 平台组件（MaxScript 脚本制品，无 SDK 依赖），使用专用安装布局：
/// 脚本落在 Agent 管理的 connectors/max{year}/{version}/，
/// userStartup 写入自动生成的加载脚本。多 Max 版本各自记账互不覆盖。
/// </summary>
public sealed class ConnectorInstaller(string agentRoot, IMaxPathResolver pathResolver, LedgerStore ledgerStore, HubClient hub)
{
    public const string ArtifactId = "com.maxhub.connector";
    public const string EntryScriptName = "maxhub_connector.ms";
    // 0_ 前缀使 loader 在启动队列中靠前执行：第三方启动脚本抛异常会中断后续队列（真机回归实测）
    private const string LoaderFileName = "0_maxhub_connector_loader.ms";

    /// <summary>为每个检测到的 Max 实例安装或更新匹配的 Connector。downloadProgress 报告当前制品的下载百分比。</summary>
    public async Task<IReadOnlyList<ConnectorSyncResult>> SyncAsync(IReadOnlyList<MaxInstallation> installations, IProgress<double>? downloadProgress = null)
    {
        var results = new List<ConnectorSyncResult>();
        foreach (var max in installations)
            results.Add(await SyncOneAsync(max.Year, downloadProgress));
        return results;
    }

    private async Task<ConnectorSyncResult> SyncOneAsync(int maxYear, IProgress<double>? downloadProgress = null)
    {
        var candidates = await hub.GetConnectorsAsync(maxYear);
        if (candidates.Length == 0)
            return new ConnectorSyncResult(maxYear, false, null, $"服务端没有支持 Max {maxYear} 的 Connector 制品。");

        var release = candidates.OrderByDescending(c => Version.Parse(c.Version)).First();
        var existing = ledgerStore.Find(ArtifactId, maxYear);
        if (existing?.Version == release.Version)
            return new ConnectorSyncResult(maxYear, true, release.Version, "已是最新版本。");

        var cachePath = Path.Combine(agentRoot, "cache", $"connector-{release.Version}-max{maxYear}.zip");
        await hub.DownloadConnectorAsync(maxYear, release.Version, cachePath, downloadProgress);
        if (!string.Equals(ToolPackage.ComputeSha256(cachePath), release.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(cachePath);
            return new ConnectorSyncResult(maxYear, false, release.Version, "Connector 制品哈希与服务端声明不一致，拒绝安装。");
        }

        var versionDir = Path.Combine(agentRoot, "connectors", $"max{maxYear}", release.Version);
        if (Directory.Exists(versionDir))
            Directory.Delete(versionDir, recursive: true);
        ZipFile.ExtractToDirectory(cachePath, versionDir);

        var startupDir = pathResolver.Resolve(maxYear, "userStartup");
        Directory.CreateDirectory(startupDir);
        var loaderPath = Path.Combine(startupDir, LoaderFileName);
        File.WriteAllText(loaderPath, BuildLoaderScript(versionDir));

        var files = new List<LedgerFile>
        {
            new() { Destination = "userStartup", RelativePath = LoaderFileName, Sha256 = ToolPackage.ComputeSha256(loaderPath) },
        };
        foreach (var file in Directory.EnumerateFiles(versionDir, "*", SearchOption.AllDirectories))
            files.Add(new LedgerFile
            {
                Destination = "connectorHome",
                RelativePath = Path.Combine(release.Version, Path.GetRelativePath(versionDir, file)).Replace('\\', '/'),
                Sha256 = ToolPackage.ComputeSha256(file),
            });

        var ledger = ledgerStore.Load();
        ledger.Entries.RemoveAll(e => e.ArtifactId == ArtifactId && e.MaxVersion == maxYear);
        ledger.Entries.Add(new LedgerEntry
        {
            ArtifactId = ArtifactId,
            ArtifactType = "connector",
            Version = release.Version,
            MaxVersion = maxYear,
            Files = files,
            InstalledAtUtc = DateTimeOffset.UtcNow,
            BackupId = existing?.Version, // 上一版本目录保留，供回退
            Active = true,
        });
        ledgerStore.Save(ledger);

        return new ConnectorSyncResult(maxYear, true, release.Version,
            existing is null ? "安装完成。" : $"已从 {existing.Version} 更新，Max 重启后生效。");
    }

    /// <summary>卸载指定 Max 年份的 Connector：删除加载脚本与受管脚本目录。</summary>
    public bool Uninstall(int maxYear)
    {
        var entry = ledgerStore.Find(ArtifactId, maxYear);
        if (entry is null)
            return false;

        var loaderPath = Path.Combine(pathResolver.Resolve(maxYear, "userStartup"), LoaderFileName);
        if (File.Exists(loaderPath))
            File.Delete(loaderPath);
        var connectorDir = Path.Combine(agentRoot, "connectors", $"max{maxYear}");
        if (Directory.Exists(connectorDir))
            Directory.Delete(connectorDir, recursive: true);

        var ledger = ledgerStore.Load();
        ledger.Entries.RemoveAll(e => e.ArtifactId == ArtifactId && e.MaxVersion == maxYear);
        ledgerStore.Save(ledger);
        return true;
    }

    private static string BuildLoaderScript(string versionDir) => $$"""
        -- MaxHub Connector loader (auto-generated; managed by MaxHub Agent, do not edit)
        (
            local entryScript = @"{{Path.Combine(versionDir, EntryScriptName)}}"
            if doesFileExist entryScript then (
                try ( fileIn entryScript ) catch (
                    format "MaxHub Connector load failed: %\n" (getCurrentException())
                )
            ) else (
                format "MaxHub Connector entry script missing: %\n" entryScript
            )
        )
        """;
}
