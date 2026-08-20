using System.IO.Compression;
using MaxHub.Core.Ledger;
using MaxHub.Core.Manifests;
using MaxHub.Core.Packaging;
using MaxHub.Agent.Core.Paths;

namespace MaxHub.Agent.Core.Install;

public sealed record PlannedFile(string Destination, string RelativePath, string AbsolutePath);
public sealed record InstallPlan(ToolManifest Manifest, IReadOnlyList<PlannedFile> Files, string RiskLevel, bool RestartRequired);
public sealed record InstallOutcome(bool Success, string? Error, LedgerEntry? Entry);
public sealed record UninstallOutcome(bool Success, IReadOnlyList<string> Conflicts, string? Error);

/// <summary>
/// 安装事务引擎：哈希校验 → 暂存 → 备份 → 切换 → 账本。
/// 任何失败恢复备份并保持账本不变。所有删除只依据账本记录。
/// </summary>
public sealed class InstallEngine(string agentRoot, IMaxPathResolver pathResolver, LedgerStore ledgerStore)
{
    private string StagingRoot => Path.Combine(agentRoot, "staging");
    private string BackupRoot => Path.Combine(agentRoot, "backups");

    public InstallPlan BuildPlan(string zipPath, int maxYear)
    {
        var manifest = ToolPackage.ReadManifest(zipPath);

        var validation = ManifestValidator.Validate(manifest);
        if (!validation.IsValid)
            throw new InvalidOperationException($"manifest 校验失败：{string.Join("; ", validation.Errors)}");

        var contents = ToolPackage.VerifyContents(zipPath, manifest);
        if (!contents.IsValid)
            throw new InvalidOperationException($"包内容校验失败：{string.Join("; ", contents.Errors)}");

        if (maxYear < manifest.Compatibility.MinVersion || maxYear > manifest.Compatibility.MaxVersion)
            throw new InvalidOperationException(
                $"工具 {manifest.Id} 兼容 {manifest.Compatibility.MinVersion}-{manifest.Compatibility.MaxVersion}，不支持 Max {maxYear}。");

        var files = new List<PlannedFile>();
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            foreach (var target in manifest.Install.Targets)
            {
                var destDir = pathResolver.Resolve(maxYear, target.Destination);
                var prefix = target.Source.TrimEnd('/') + "/";
                foreach (var entry in zip.Entries.Where(e => e.FullName == target.Source || e.FullName.StartsWith(prefix, StringComparison.Ordinal)))
                {
                    if (entry.FullName.EndsWith('/')) continue;
                    var relative = entry.FullName == target.Source
                        ? Path.GetFileName(entry.FullName)
                        : entry.FullName[prefix.Length..];
                    files.Add(new PlannedFile(target.Destination, relative, Path.GetFullPath(Path.Combine(destDir, relative))));
                }
            }
        }

        var risk = manifest.Install.Targets.Any(t => t.Destination == "userStartup") ? "medium" : "low";
        return new InstallPlan(manifest, files, risk, manifest.Install.RestartRequired);
    }

    public InstallOutcome Install(string zipPath, string expectedSha256, int maxYear)
    {
        if (!string.Equals(ToolPackage.ComputeSha256(zipPath), expectedSha256, StringComparison.OrdinalIgnoreCase))
            return new InstallOutcome(false, "包哈希与服务端声明不一致，拒绝安装。", null);

        InstallPlan plan;
        try
        {
            plan = BuildPlan(zipPath, maxYear);
        }
        catch (InvalidOperationException ex)
        {
            return new InstallOutcome(false, ex.Message, null);
        }

        var stagingDir = Path.Combine(StagingRoot, $"{plan.Manifest.Id}-{Guid.NewGuid():N}");
        var backupId = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..24];
        var backupDir = Path.Combine(BackupRoot, plan.Manifest.Id, maxYear.ToString(), backupId);
        var writtenFiles = new List<string>();

        try
        {
            ZipFile.ExtractToDirectory(zipPath, stagingDir);

            // 备份将被覆盖的文件与上一版本账本
            var previousEntry = ledgerStore.Find(plan.Manifest.Id, maxYear);
            Directory.CreateDirectory(backupDir);
            BackupExisting(plan, previousEntry, backupDir, maxYear);

            // 从暂存区切换到正式目录
            var ledgerFiles = new List<LedgerFile>();
            foreach (var target in plan.Manifest.Install.Targets)
            {
                var sourceInStaging = Path.Combine(stagingDir, target.Source.Replace('/', Path.DirectorySeparatorChar));
                foreach (var planned in plan.Files.Where(f => f.Destination == target.Destination))
                {
                    var stagedFile = File.Exists(sourceInStaging)
                        ? sourceInStaging
                        : Path.Combine(sourceInStaging, planned.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(stagedFile))
                        continue; // 属于其他 target 的文件
                    Directory.CreateDirectory(Path.GetDirectoryName(planned.AbsolutePath)!);
                    File.Copy(stagedFile, planned.AbsolutePath, overwrite: true);
                    writtenFiles.Add(planned.AbsolutePath);
                    ledgerFiles.Add(new LedgerFile
                    {
                        Destination = planned.Destination,
                        RelativePath = planned.RelativePath,
                        Sha256 = ToolPackage.ComputeSha256(planned.AbsolutePath),
                    });
                }
            }

            var entry = new LedgerEntry
            {
                ArtifactId = plan.Manifest.Id,
                ArtifactType = "tool",
                Version = plan.Manifest.Version,
                DisplayName = plan.Manifest.Name,
                MaxVersion = maxYear,
                Files = ledgerFiles,
                InstalledAtUtc = DateTimeOffset.UtcNow,
                BackupId = backupId,
                Active = true,
            };
            SavePreviousEntrySnapshot(backupDir, previousEntry);
            ReplaceLedgerEntry(entry);
            return new InstallOutcome(true, null, entry);
        }
        catch (Exception ex)
        {
            RestoreBackup(backupDir, writtenFiles, maxYear);
            return new InstallOutcome(false, $"安装失败已回滚：{ex.Message}", null);
        }
        finally
        {
            if (Directory.Exists(stagingDir))
                Directory.Delete(stagingDir, recursive: true);
        }
    }

    /// <summary>卸载：只删除账本记录且哈希未被用户改动的文件。</summary>
    public UninstallOutcome Uninstall(string artifactId, int maxYear)
    {
        var entry = ledgerStore.Find(artifactId, maxYear);
        if (entry is null)
            return new UninstallOutcome(false, [], "账本中没有该工具的激活安装记录。");

        var conflicts = new List<string>();
        foreach (var file in entry.Files)
        {
            var absolutePath = Path.Combine(pathResolver.Resolve(maxYear, file.Destination), file.RelativePath);
            if (!File.Exists(absolutePath))
                continue;
            if (ToolPackage.ComputeSha256(absolutePath) != file.Sha256)
            {
                conflicts.Add(absolutePath); // 用户改过的文件保留，人工处理
                continue;
            }
            File.Delete(absolutePath);
        }

        var ledger = ledgerStore.Load();
        ledger.Entries.RemoveAll(e => e.ArtifactId == artifactId && e.MaxVersion == maxYear);
        ledgerStore.Save(ledger);
        return new UninstallOutcome(true, conflicts, null);
    }

    /// <summary>回滚到备份中记录的上一版本；无上一版本则等价于卸载当前版本。</summary>
    public InstallOutcome Rollback(string artifactId, int maxYear)
    {
        var entry = ledgerStore.Find(artifactId, maxYear);
        if (entry?.BackupId is null)
            return new InstallOutcome(false, "没有可回滚的安装记录。", null);

        var backupDir = Path.Combine(BackupRoot, artifactId, maxYear.ToString(), entry.BackupId);
        if (!Directory.Exists(backupDir))
            return new InstallOutcome(false, $"备份 {entry.BackupId} 不存在。", null);

        var uninstall = Uninstall(artifactId, maxYear);
        if (!uninstall.Success)
            return new InstallOutcome(false, uninstall.Error, null);

        var previousEntry = LoadPreviousEntrySnapshot(backupDir);
        if (previousEntry is null)
            return new InstallOutcome(true, null, null); // 首次安装的回滚 = 卸载

        foreach (var file in previousEntry.Files)
        {
            var backupFile = Path.Combine(backupDir, "files", file.Destination, file.RelativePath);
            var absolutePath = Path.Combine(pathResolver.Resolve(maxYear, file.Destination), file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
            File.Copy(backupFile, absolutePath, overwrite: true);
        }
        ReplaceLedgerEntry(previousEntry);
        return new InstallOutcome(true, null, previousEntry);
    }

    private void BackupExisting(InstallPlan plan, LedgerEntry? previousEntry, string backupDir, int maxYear)
    {
        var toBackup = new Dictionary<string, (string Destination, string RelativePath)>(StringComparer.OrdinalIgnoreCase);
        foreach (var planned in plan.Files)
            toBackup[planned.AbsolutePath] = (planned.Destination, planned.RelativePath);
        foreach (var file in previousEntry?.Files ?? [])
        {
            var absolutePath = Path.Combine(pathResolver.Resolve(maxYear, file.Destination), file.RelativePath);
            toBackup[Path.GetFullPath(absolutePath)] = (file.Destination, file.RelativePath);
        }

        foreach (var (absolutePath, (destination, relativePath)) in toBackup)
        {
            if (!File.Exists(absolutePath))
                continue;
            var backupFile = Path.Combine(backupDir, "files", destination, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
            File.Copy(absolutePath, backupFile, overwrite: true);
        }
    }

    private void RestoreBackup(string backupDir, IReadOnlyList<string> writtenFiles, int maxYear)
    {
        foreach (var written in writtenFiles)
            if (File.Exists(written))
                File.Delete(written);

        var filesRoot = Path.Combine(backupDir, "files");
        if (!Directory.Exists(filesRoot))
            return;
        foreach (var backupFile in Directory.EnumerateFiles(filesRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(filesRoot, backupFile);
            var separatorIndex = relative.IndexOf(Path.DirectorySeparatorChar);
            var destination = relative[..separatorIndex];
            var relativePath = relative[(separatorIndex + 1)..];
            var absolutePath = Path.Combine(pathResolver.Resolve(maxYear, destination), relativePath);
            // 内容与备份一致的文件（如被占用但未被改动）无需恢复
            if (File.Exists(absolutePath) && ToolPackage.ComputeSha256(absolutePath) == ToolPackage.ComputeSha256(backupFile))
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
            File.Copy(backupFile, absolutePath, overwrite: true);
        }
    }

    private void ReplaceLedgerEntry(LedgerEntry entry)
    {
        var ledger = ledgerStore.Load();
        ledger.Entries.RemoveAll(e => e.ArtifactId == entry.ArtifactId && e.MaxVersion == entry.MaxVersion);
        ledger.Entries.Add(entry);
        ledgerStore.Save(ledger);
    }

    private static void SavePreviousEntrySnapshot(string backupDir, LedgerEntry? previousEntry)
    {
        if (previousEntry is null)
            return;
        File.WriteAllText(Path.Combine(backupDir, "entry.json"),
            System.Text.Json.JsonSerializer.Serialize(previousEntry, ManifestJson.Options));
    }

    private static LedgerEntry? LoadPreviousEntrySnapshot(string backupDir)
    {
        var path = Path.Combine(backupDir, "entry.json");
        return File.Exists(path)
            ? System.Text.Json.JsonSerializer.Deserialize<LedgerEntry>(File.ReadAllText(path), ManifestJson.Options)
            : null;
    }
}
