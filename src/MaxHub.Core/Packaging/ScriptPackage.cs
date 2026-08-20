using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MaxHub.Core.Manifests;

namespace MaxHub.Core.Packaging;

/// <summary>上传脚本的元数据（前端填写，后端据此生成 manifest）。</summary>
public sealed record ScriptPublishRequest(
    string FileName,
    string Content,
    string Name,
    string Description,
    string Version,
    int MinMaxYear,
    int MaxMaxYear);

/// <summary>
/// 把单个脚本文件打包成标准 .zip（自动生成 manifest.json + payload 目录）。
/// 供"上传本地脚本自动识别"流程使用：用户选脚本 → 后端解析预填 → 确认后打包提交。
/// </summary>
public static class ScriptPackage
{
    public static string Pack(ScriptPublishRequest req, string outputZipPath)
    {
        var ext = Path.GetExtension(req.FileName).ToLowerInvariant();
        var dest = ext switch
        {
            ".ms" or ".mcr" => "userScripts",
            ".py" => "userScripts",
            _ => "userScripts",
        };
        var scriptEntry = $"payload/3dsmax/scripts/{SanitizeFileName(req.FileName)}";

        var manifest = new ToolManifest
        {
            SchemaVersion = 1,
            Id = ToolId.Generate(req.Name),
            Name = req.Name,
            Version = req.Version,
            HostType = "3dsmax",
            Description = req.Description,
            Compatibility = new Compatibility
            {
                MinVersion = req.MinMaxYear,
                MaxVersion = req.MaxMaxYear,
                Platforms = ["win-x64"],
            },
            Install = new InstallSpec
            {
                Scope = "user",
                RestartRequired = false,
                Targets = [new InstallTarget { Source = scriptEntry, Destination = dest }],
            },
            EntryPoints = ext is ".ms" or ".mcr"
                ? [new EntryPoint { Kind = "script", Script = scriptEntry, Category = "MaxHub" }]
                : null,
        };

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputZipPath))!);
        if (File.Exists(outputZipPath)) File.Delete(outputZipPath);

        using (var zip = ZipFile.Open(outputZipPath, ZipArchiveMode.Create))
        {
            var manifestEntry = zip.CreateEntry(ManifestJsonEntryName);
            using (var w = new StreamWriter(manifestEntry.Open(), new UTF8Encoding(false)))
                w.Write(JsonSerializer.Serialize(manifest, ManifestJson.Options));

            var scriptEntryZip = zip.CreateEntry(scriptEntry);
            using (var w = new StreamWriter(scriptEntryZip.Open(), new UTF8Encoding(false)))
                w.Write(req.Content);
        }

        return outputZipPath;
    }

    private const string ManifestJsonEntryName = "manifest.json";

    private static string SanitizeFileName(string name)
    {
        var safe = Path.GetFileName(name);
        return string.IsNullOrWhiteSpace(safe) ? "script.ms" : safe;
    }

}
