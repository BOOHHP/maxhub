using System.Diagnostics;
using System.Security.Cryptography;

namespace MaxHub.Agent.Core.Remote;

/// <summary>
/// Agent 自更新：从服务器获取最新版本元数据，下载 exe、校验 SHA256、替换并重启。
/// 更新期间写一个 pending-update 标记，避免进程被替换后无法重启自己。
/// </summary>
public sealed class SelfUpdater(HubClient hub)
{
    public string? CurrentVersion { get; init; }

    /// <summary>检查服务器是否有比当前更新的版本。未配置（404）或无更新返回 null。</summary>
    public async Task<AgentReleaseInfo?> CheckForUpdateAsync()
    {
        AgentReleaseInfo? release;
        try
        {
            release = await hub.GetLatestAgentAsync();
        }
        catch
        {
            return null; // 服务器不可达时不打扰用户
        }
        if (release is null || string.IsNullOrWhiteSpace(release.DownloadUrl))
            return null;
        return IsNewer(release.Version) ? release : null;
    }

    /// <summary>下载新版本 exe 到应用目录旁的临时文件，校验后替换并重启。</summary>
    public async Task DownloadAndInstallAsync(AgentReleaseInfo release, IProgress<double>? progress = null)
    {
        if (Environment.ProcessPath is not { } currentExe)
            throw new InvalidOperationException("无法定位当前可执行文件路径。");

        var dir = Path.GetDirectoryName(currentExe)!;
        var tempPath = Path.Combine(dir, $"MaxHubAgent.new-{release.Version}.exe");
        try
        {
            await hub.DownloadAgentAsync(release.DownloadUrl, tempPath, progress);

            // 校验 SHA256（服务器配置了 hash 时）
            if (!string.IsNullOrWhiteSpace(release.Sha256))
            {
                var actual = await ComputeSha256Async(tempPath);
                if (!string.Equals(actual, release.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("下载文件校验失败（SHA256 不匹配），已取消更新。");
            }

            // 准备重启脚本：等待当前进程退出后替换 exe 再启动
            var scriptPath = Path.Combine(dir, "maxhub-update.cmd");
            await File.WriteAllTextAsync(scriptPath, BuildRestartScript(currentExe, tempPath));
            Process.Start(new ProcessStartInfo
            {
                FileName = scriptPath,
                WindowStyle = ProcessWindowStyle.Hidden,
                UseShellExecute = true,
            });
        }
        catch
        {
            try { File.Delete(tempPath); } catch { /* ignore */ }
            throw;
        }
    }

    /// <summary>与当前版本比较：服务器版本更新（语义化）返回 true。</summary>
    private bool IsNewer(string serverVersion)
    {
        if (string.IsNullOrWhiteSpace(CurrentVersion))
            return true;
        var cur = Parse(CurrentVersion);
        var next = Parse(serverVersion);
        return cur is not null && next is not null && next.CompareTo(cur) > 0;
    }

    private static Version? Parse(string v)
    {
        // 去预发布后缀（如 1.0.1-beta）后解析
        var core = v.Split('-')[0];
        return Version.TryParse(core, out var parsed) ? parsed : null;
    }

    private static string BuildRestartScript(string currentExe, string newExe)
    {
        // 延时等当前进程退出 → 替换 → 启动新版本
        return string.Join("\r\n", new[]
        {
            "@echo off",
            "timeout /t 2 /nobreak > nul",
            $"move /y \"{newExe}\" \"{currentExe}\" > nul",
            $"start \"\" \"{currentExe}\"",
            "del \"%~f0\"",
        });
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
