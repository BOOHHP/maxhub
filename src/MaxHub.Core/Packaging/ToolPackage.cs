using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using MaxHub.Core.Manifests;

namespace MaxHub.Core.Packaging;

public sealed record PackResult(string ZipPath, string Sha256);

/// <summary>.dccc-tool.zip 的打包与读取。zip 条目统一使用正斜杠。</summary>
public static class ToolPackage
{
    public const string ManifestEntryName = "manifest.json";

    public static PackResult Pack(string sourceDirectory, string outputZipPath)
    {
        var manifestPath = Path.Combine(sourceDirectory, ManifestEntryName);
        if (!File.Exists(manifestPath))
            throw new InvalidOperationException($"缺少 {ManifestEntryName}: {sourceDirectory}");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputZipPath))!);
        if (File.Exists(outputZipPath))
            File.Delete(outputZipPath);

        using (var zip = ZipFile.Open(outputZipPath, ZipArchiveMode.Create))
        {
            foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                var entryName = Path.GetRelativePath(sourceDirectory, file).Replace('\\', '/');
                zip.CreateEntryFromFile(file, entryName, CompressionLevel.Optimal);
            }
        }

        return new PackResult(outputZipPath, ComputeSha256(outputZipPath));
    }

    public static ToolManifest ReadManifest(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.GetEntry(ManifestEntryName)
            ?? throw new InvalidOperationException($"包内缺少 {ManifestEntryName}: {zipPath}");
        using var stream = entry.Open();
        return JsonSerializer.Deserialize<ToolManifest>(stream, ManifestJson.Options)
            ?? throw new InvalidOperationException("manifest.json 反序列化为空。");
    }

    /// <summary>校验 manifest 声明的 target.source 在包内存在，且包内没有不安全路径。</summary>
    public static ValidationResult VerifyContents(string zipPath, ToolManifest manifest)
    {
        var errors = new List<string>();
        using var zip = ZipFile.OpenRead(zipPath);
        var entryNames = zip.Entries.Select(e => e.FullName).ToHashSet(StringComparer.Ordinal);

        foreach (var name in entryNames)
            if (!ManifestValidator.IsSafeRelativePath(name.TrimEnd('/')))
                errors.Add($"包内路径不安全: \"{name}\"。");

        foreach (var target in manifest.Install.Targets)
        {
            var prefix = target.Source.TrimEnd('/') + "/";
            if (!entryNames.Contains(target.Source) && !entryNames.Any(n => n.StartsWith(prefix, StringComparison.Ordinal)))
                errors.Add($"target.source \"{target.Source}\" 在包内不存在。");
        }

        return errors.Count == 0 ? ValidationResult.Ok() : ValidationResult.Fail(errors);
    }

    public static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
