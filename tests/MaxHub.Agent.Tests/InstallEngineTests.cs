using MaxHub.Agent.Core.Install;
using MaxHub.Agent.Core.Paths;
using MaxHub.Core.Packaging;

namespace MaxHub.Agent.Tests;

public class InstallEngineTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("maxhub-engine").FullName;
    private readonly DefaultMaxPathResolver _resolver;
    private readonly LedgerStore _ledger;
    private readonly InstallEngine _engine;

    private static string RepoRoot { get; } = FindRepoRoot();

    public InstallEngineTests()
    {
        _resolver = new DefaultMaxPathResolver(Path.Combine(_root, "maxuser"));
        _ledger = new LedgerStore(Path.Combine(_root, "agent", "installed.json"));
        _engine = new InstallEngine(Path.Combine(_root, "agent"), _resolver, _ledger);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MaxHub.sln")))
            dir = dir.Parent!;
        return dir!.FullName;
    }

    /// <summary>从样本目录构建包，可选修改版本号与脚本内容以模拟新版本。</summary>
    private (string ZipPath, string Sha256) BuildZip(string sampleName, string? newVersion = null, string? scriptSuffix = null)
    {
        var sourceDir = Path.Combine(RepoRoot, "samples", "tools", sampleName);
        var workDir = Path.Combine(_root, "build", $"{sampleName}-{Guid.NewGuid():N}");
        CopyDirectory(sourceDir, workDir);

        if (newVersion is not null)
        {
            var manifestPath = Path.Combine(workDir, "manifest.json");
            File.WriteAllText(manifestPath, File.ReadAllText(manifestPath).Replace("\"version\": \"1.4.0\"", $"\"version\": \"{newVersion}\""));
        }
        if (scriptSuffix is not null)
            foreach (var script in Directory.EnumerateFiles(workDir, "*.ms", SearchOption.AllDirectories))
                File.AppendAllText(script, scriptSuffix);

        var zipPath = Path.Combine(_root, "build", $"{sampleName}-{newVersion ?? "orig"}-{Guid.NewGuid():N}.zip");
        var result = ToolPackage.Pack(workDir, zipPath);
        return (result.ZipPath, result.Sha256);
    }

    private string InstalledPath(int year, string destination, string fileName) =>
        Path.Combine(_resolver.Resolve(year, destination), fileName);

    [Fact]
    public void Install_writes_files_and_ledger()
    {
        var (zip, sha) = BuildZip("scene-batch-renamer");
        var outcome = _engine.Install(zip, sha, 2024);

        Assert.True(outcome.Success, outcome.Error);
        Assert.True(File.Exists(InstalledPath(2024, "userScripts", "SceneBatchRenamer.ms")));
        Assert.True(File.Exists(InstalledPath(2024, "userMacros", "SceneBatchRenamer.mcr")));

        var entry = _ledger.Find("com.company.scene-batch-renamer", 2024);
        Assert.NotNull(entry);
        Assert.Equal("1.4.0", entry.Version);
        Assert.Equal(2, entry.Files.Count);
        Assert.All(entry.Files, f => Assert.Matches("^[a-f0-9]{64}$", f.Sha256));
    }

    [Fact]
    public void Wrong_hash_is_rejected_and_nothing_written()
    {
        var (zip, _) = BuildZip("scene-batch-renamer");
        var outcome = _engine.Install(zip, new string('0', 64), 2024);

        Assert.False(outcome.Success);
        Assert.Contains("哈希", outcome.Error);
        Assert.False(File.Exists(InstalledPath(2024, "userScripts", "SceneBatchRenamer.ms")));
        Assert.Null(_ledger.Find("com.company.scene-batch-renamer", 2024));
    }

    [Fact]
    public void Incompatible_max_year_is_rejected()
    {
        var sourceDir = Path.Combine(RepoRoot, "samples", "tools", "quick-exporter"); // 2021-2026
        var zipPath = Path.Combine(_root, "build", "qe.zip");
        var packed = ToolPackage.Pack(sourceDir, zipPath);

        var outcome = _engine.Install(zipPath, packed.Sha256, 2019);
        Assert.False(outcome.Success);
        Assert.Contains("不支持 Max 2019", outcome.Error);
    }

    [Fact]
    public void Update_backs_up_then_rollback_restores_previous_version()
    {
        var (zipV1, shaV1) = BuildZip("scene-batch-renamer");
        Assert.True(_engine.Install(zipV1, shaV1, 2024).Success);
        var v1Content = File.ReadAllText(InstalledPath(2024, "userScripts", "SceneBatchRenamer.ms"));

        var (zipV2, shaV2) = BuildZip("scene-batch-renamer", newVersion: "1.5.0", scriptSuffix: "\n-- v1.5.0 changes\n");
        Assert.True(_engine.Install(zipV2, shaV2, 2024).Success);
        Assert.Equal("1.5.0", _ledger.Find("com.company.scene-batch-renamer", 2024)!.Version);
        Assert.Contains("v1.5.0 changes", File.ReadAllText(InstalledPath(2024, "userScripts", "SceneBatchRenamer.ms")));

        var rollback = _engine.Rollback("com.company.scene-batch-renamer", 2024);
        Assert.True(rollback.Success, rollback.Error);
        Assert.Equal("1.4.0", _ledger.Find("com.company.scene-batch-renamer", 2024)!.Version);
        Assert.Equal(v1Content, File.ReadAllText(InstalledPath(2024, "userScripts", "SceneBatchRenamer.ms")));
    }

    [Fact]
    public void Mid_install_failure_restores_previous_state()
    {
        var (zipV1, shaV1) = BuildZip("scene-batch-renamer");
        Assert.True(_engine.Install(zipV1, shaV1, 2024).Success);
        var v1Script = File.ReadAllText(InstalledPath(2024, "userScripts", "SceneBatchRenamer.ms"));

        var (zipV2, shaV2) = BuildZip("scene-batch-renamer", newVersion: "1.5.0", scriptSuffix: "\n-- v2\n");

        // 独占锁定 macros 目标文件，迫使第二个 target 的复制失败
        var macroPath = InstalledPath(2024, "userMacros", "SceneBatchRenamer.mcr");
        using (File.Open(macroPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var outcome = _engine.Install(zipV2, shaV2, 2024);
            Assert.False(outcome.Success);
            Assert.Contains("已回滚", outcome.Error);
        }

        // 前一版本完整可用，账本未被污染
        Assert.Equal(v1Script, File.ReadAllText(InstalledPath(2024, "userScripts", "SceneBatchRenamer.ms")));
        Assert.Equal("1.4.0", _ledger.Find("com.company.scene-batch-renamer", 2024)!.Version);
    }

    [Fact]
    public void Uninstall_preserves_user_modified_and_foreign_files()
    {
        var (zip, sha) = BuildZip("scene-batch-renamer");
        Assert.True(_engine.Install(zip, sha, 2024).Success);

        var scriptPath = InstalledPath(2024, "userScripts", "SceneBatchRenamer.ms");
        File.AppendAllText(scriptPath, "\n-- user edit\n"); // 用户改动
        var foreignPath = InstalledPath(2024, "userScripts", "user_custom.ms");
        File.WriteAllText(foreignPath, "-- not managed by MaxHub\n"); // 未受管文件

        var outcome = _engine.Uninstall("com.company.scene-batch-renamer", 2024);
        Assert.True(outcome.Success);

        Assert.True(File.Exists(scriptPath), "用户改动过的文件必须保留");
        Assert.Contains(outcome.Conflicts, c => c.EndsWith("SceneBatchRenamer.ms"));
        Assert.False(File.Exists(InstalledPath(2024, "userMacros", "SceneBatchRenamer.mcr")), "未改动的受管文件应删除");
        Assert.True(File.Exists(foreignPath), "未受管文件必须保留");
        Assert.Null(_ledger.Find("com.company.scene-batch-renamer", 2024));
    }

    [Fact]
    public void Same_tool_on_different_max_years_is_isolated()
    {
        var (zip, sha) = BuildZip("scene-batch-renamer");
        Assert.True(_engine.Install(zip, sha, 2019).Success);
        Assert.True(_engine.Install(zip, sha, 2026).Success);

        Assert.True(_engine.Uninstall("com.company.scene-batch-renamer", 2019).Success);
        Assert.False(File.Exists(InstalledPath(2019, "userScripts", "SceneBatchRenamer.ms")));
        Assert.True(File.Exists(InstalledPath(2026, "userScripts", "SceneBatchRenamer.ms")), "卸载 2019 不得影响 2026");
        Assert.NotNull(_ledger.Find("com.company.scene-batch-renamer", 2026));
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
