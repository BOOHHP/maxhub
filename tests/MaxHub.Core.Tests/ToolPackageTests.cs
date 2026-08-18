using MaxHub.Core.Manifests;
using MaxHub.Core.Packaging;

namespace MaxHub.Core.Tests;

public class ToolPackageTests : IDisposable
{
    private readonly string _workDir = Directory.CreateTempSubdirectory("maxhub-pkg-test").FullName;

    [Fact]
    public void Pack_read_verify_roundtrip()
    {
        var sampleDir = Path.Combine(TestPaths.SamplesDir, "scene-batch-renamer");
        var zipPath = Path.Combine(_workDir, "scene-batch-renamer-1.4.0.dccc-tool.zip");

        var result = ToolPackage.Pack(sampleDir, zipPath);
        Assert.True(File.Exists(result.ZipPath));
        Assert.Matches("^[a-f0-9]{64}$", result.Sha256);

        var manifest = ToolPackage.ReadManifest(zipPath);
        Assert.Equal("com.company.scene-batch-renamer", manifest.Id);
        Assert.True(ManifestValidator.Validate(manifest).IsValid);
        Assert.True(ToolPackage.VerifyContents(zipPath, manifest).IsValid);
    }

    [Fact]
    public void Hash_changes_when_content_tampered()
    {
        var sampleDir = Path.Combine(TestPaths.SamplesDir, "quick-exporter");
        var zip1 = Path.Combine(_workDir, "a.zip");
        var original = ToolPackage.Pack(sampleDir, zip1).Sha256;

        var tamperedBytes = File.ReadAllBytes(zip1);
        tamperedBytes[^1] ^= 0xFF;
        var zip2 = Path.Combine(_workDir, "b.zip");
        File.WriteAllBytes(zip2, tamperedBytes);

        Assert.NotEqual(original, ToolPackage.ComputeSha256(zip2));
    }

    [Fact]
    public void Missing_declared_source_is_reported()
    {
        var dir = Path.Combine(_workDir, "broken-tool");
        Directory.CreateDirectory(dir);
        File.Copy(Path.Combine(TestPaths.SamplesDir, "quick-exporter", "manifest.json"), Path.Combine(dir, "manifest.json"));
        // 故意不拷贝 payload

        var zipPath = Path.Combine(_workDir, "broken.zip");
        ToolPackage.Pack(dir, zipPath);
        var manifest = ToolPackage.ReadManifest(zipPath);

        var verify = ToolPackage.VerifyContents(zipPath, manifest);
        Assert.False(verify.IsValid);
        Assert.Contains(verify.Errors, e => e.Contains("quick_exporter.py"));
    }

    [Fact]
    public void Pack_without_manifest_throws()
    {
        var dir = Path.Combine(_workDir, "no-manifest");
        Directory.CreateDirectory(dir);
        Assert.Throws<InvalidOperationException>(() => ToolPackage.Pack(dir, Path.Combine(_workDir, "x.zip")));
    }

    public void Dispose() => Directory.Delete(_workDir, recursive: true);
}
