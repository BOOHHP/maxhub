using System.IO.Compression;
using MaxHub.Core.Manifests;
using MaxHub.Core.Packaging;

namespace MaxHub.Core.Tests;

public class ScriptDescriptorTests
{
    [Fact]
    public void Extracts_rollout_title_and_header_comment()
    {
        var content = """
            -- 批量重命名场景对象，支持前缀、后缀和序号模板。
            rollout BatchRenamer "批量重命名" (
                button go "开始"
            )
            """;
        var d = ScriptDescriptor.Analyze("batch_renamer.ms", content);
        Assert.Equal("批量重命名", d.Name);
        Assert.Contains("批量重命名场景对象", d.Description);
        Assert.Equal("com.company.batch-renamer", d.SuggestedId);
    }

    [Fact]
    public void Falls_back_to_filename_when_no_rollout()
    {
        var d = ScriptDescriptor.Analyze("quick_exporter.ms", "fn exportSelected() = ()");
        Assert.Equal("Quick Exporter", d.Name);
        Assert.Contains("导出", d.Description);
    }

    [Fact]
    public void Infers_actions_from_keywords()
    {
        var d = ScriptDescriptor.Analyze("cleanup.ms", "fn cleanupScene = ( delete helpers )");
        Assert.Contains("清理", d.Description);
    }

    [Fact]
    public void Handles_empty_content()
    {
        var d = ScriptDescriptor.Analyze("tool.ms", "");
        Assert.Equal("Tool", d.Name);
        Assert.Contains("自动识别", d.Description);
    }
}

public class ScriptPackageTests
{
    [Fact]
    public void Pack_generates_manifest_and_payload()
    {
        var zipPath = Path.Combine(Path.GetTempPath(), $"maxhub-script-{Guid.NewGuid():N}.zip");
        try
        {
            ScriptPackage.Pack(new ScriptPublishRequest(
                FileName: "batch_renamer.ms",
                Content: "-- 批量重命名\nfn go() = ()",
                Name: "批量重命名",
                Description: "批量重命名场景对象",
                Version: "1.0.0",
                MinMaxYear: 2019,
                MaxMaxYear: 2026), zipPath);

            using var zip = ZipFile.OpenRead(zipPath);
            Assert.Contains(zip.Entries, e => e.FullName == "manifest.json");
            Assert.Contains(zip.Entries, e => e.FullName == "payload/3dsmax/scripts/batch_renamer.ms");

            var manifest = ToolPackage.ReadManifest(zipPath);
            Assert.Equal("批量重命名", manifest.Name);
            Assert.Equal("1.0.0", manifest.Version);
            Assert.Equal(2019, manifest.Compatibility.MinVersion);
            Assert.Equal(2026, manifest.Compatibility.MaxVersion);
            Assert.Equal("userScripts", manifest.Install.Targets[0].Destination);
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    [Fact]
    public void Packed_package_passes_validation()
    {
        var zipPath = Path.Combine(Path.GetTempPath(), $"maxhub-script-{Guid.NewGuid():N}.zip");
        try
        {
            ScriptPackage.Pack(new ScriptPublishRequest(
                FileName: "tool.ms", Content: "fn go() = ()", Name: "Tool",
                Description: "测试工具", Version: "1.0.0", MinMaxYear: 2019, MaxMaxYear: 2026), zipPath);
            var manifest = ToolPackage.ReadManifest(zipPath);
            var validation = ManifestValidator.Validate(manifest);
            Assert.True(validation.IsValid, string.Join("; ", validation.Errors));
            var contents = ToolPackage.VerifyContents(zipPath, manifest);
            Assert.True(contents.IsValid, string.Join("; ", contents.Errors));
        }
        finally
        {
            File.Delete(zipPath);
        }
    }
}
