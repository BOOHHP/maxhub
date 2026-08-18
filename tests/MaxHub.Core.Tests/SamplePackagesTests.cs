using System.Text.Json;
using MaxHub.Core.Manifests;
using MaxHub.Core.Packaging;

namespace MaxHub.Core.Tests;

/// <summary>阶段 0 验收：所有样本包必须通过清单校验并可打包、可完整性验证。</summary>
public class SamplePackagesTests
{
    public static TheoryData<string> SampleDirectories()
    {
        var data = new TheoryData<string>();
        foreach (var dir in Directory.EnumerateDirectories(TestPaths.SamplesDir))
            data.Add(Path.GetFileName(dir));
        return data;
    }

    [Fact]
    public void At_least_three_samples_exist() =>
        Assert.True(Directory.EnumerateDirectories(TestPaths.SamplesDir).Count() >= 3);

    [Theory]
    [MemberData(nameof(SampleDirectories))]
    public void Sample_is_valid_and_packable(string sampleName)
    {
        var sampleDir = Path.Combine(TestPaths.SamplesDir, sampleName);
        var manifest = JsonSerializer.Deserialize<ToolManifest>(
            File.ReadAllText(Path.Combine(sampleDir, "manifest.json")), ManifestJson.Options)!;

        var validation = ManifestValidator.Validate(manifest);
        Assert.True(validation.IsValid, $"{sampleName}: {string.Join("; ", validation.Errors)}");

        var zipPath = Path.Combine(Path.GetTempPath(), $"maxhub-sample-{sampleName}.zip");
        try
        {
            var packed = ToolPackage.Pack(sampleDir, zipPath);
            Assert.Equal(packed.Sha256, ToolPackage.ComputeSha256(zipPath));
            Assert.True(ToolPackage.VerifyContents(zipPath, manifest).IsValid);
        }
        finally
        {
            File.Delete(zipPath);
        }
    }
}
