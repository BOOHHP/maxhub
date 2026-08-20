using System.Text.RegularExpressions;

namespace MaxHub.Core.Manifests;

public sealed record ValidationResult(bool IsValid, IReadOnlyList<string> Errors)
{
    public static ValidationResult Ok() => new(true, []);
    public static ValidationResult Fail(IReadOnlyList<string> errors) => new(false, errors);
}

/// <summary>与 protocol/manifest.schema.json 保持一致的唯一执法点。</summary>
public static partial class ManifestValidator
{
    public const int MinMaxYear = 2019;
    public const int MaxMaxYear = 2026;

    public static readonly IReadOnlySet<string> MvpDestinations =
        new HashSet<string>(StringComparer.Ordinal) { "userScripts", "userMacros", "userStartup" };

    public static readonly IReadOnlySet<string> Phase2Destinations =
        new HashSet<string>(StringComparer.Ordinal) { "userPlugins", "projectScripts", "sharedScripts" };

    private static readonly HashSet<string> KnownPermissions = ["file.read", "file.write"];
    private static readonly HashSet<string> KnownEntryPointKinds = ["macroScript", "script"];

    [GeneratedRegex(@"^[a-z0-9]+(\.[a-z0-9-]+)+$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$")]
    private static partial Regex SemverPattern();

    [GeneratedRegex(@"^[\^~]?\d+\.\d+\.\d+$")]
    private static partial Regex RangePattern();

    [GeneratedRegex(@"^[a-f0-9]{64}$")]
    private static partial Regex Sha256Pattern();

    public static ValidationResult Validate(ToolManifest manifest)
    {
        var errors = new List<string>();

        if (manifest.SchemaVersion != 1)
            errors.Add($"schemaVersion 必须为 1，当前为 {manifest.SchemaVersion}。");
        if (string.IsNullOrEmpty(manifest.Id) || manifest.Id.Length > 128 ||
            (!ToolId.IsCanonical(manifest.Id) && !IdPattern().IsMatch(manifest.Id)))
            errors.Add("id 必须是 MaxTool 加 8 位数字，或兼容旧版小写反向域名格式。");
        if (string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Length > 100)
            errors.Add("name 必须为 1-100 个字符。");
        if (!SemverPattern().IsMatch(manifest.Version))
            errors.Add($"version 必须是语义化版本，当前为 \"{manifest.Version}\"。");
        if (manifest.HostType != "3dsmax")
            errors.Add($"hostType 必须为 3dsmax，当前为 \"{manifest.HostType}\"。");

        ValidateCompatibility(manifest.Compatibility, errors);
        ValidateInstall(manifest.Install, errors);
        ValidateEntryPoints(manifest.EntryPoints, errors);
        ValidateDependencies(manifest.Dependencies, errors);

        foreach (var permission in manifest.Permissions ?? [])
            if (!KnownPermissions.Contains(permission))
                errors.Add($"未知权限 \"{permission}\"。");

        if (manifest.Integrity is { } integrity && !Sha256Pattern().IsMatch(integrity.Sha256))
            errors.Add("integrity.sha256 必须是 64 位小写十六进制。");

        return errors.Count == 0 ? ValidationResult.Ok() : ValidationResult.Fail(errors);
    }

    private static void ValidateCompatibility(Compatibility compatibility, List<string> errors)
    {
        if (compatibility.MinVersion is < MinMaxYear or > MaxMaxYear ||
            compatibility.MaxVersion is < MinMaxYear or > MaxMaxYear)
            errors.Add($"compatibility 版本必须在 {MinMaxYear}-{MaxMaxYear} 范围内。");
        else if (compatibility.MinVersion > compatibility.MaxVersion)
            errors.Add("compatibility.minVersion 不能大于 maxVersion。");

        if (compatibility.Platforms.Count == 0 || compatibility.Platforms.Any(p => p != "win-x64"))
            errors.Add("platforms 只支持 win-x64。");
    }

    private static void ValidateInstall(InstallSpec install, List<string> errors)
    {
        if (install.Scope != "user")
            errors.Add($"install.scope 目前只支持 user，当前为 \"{install.Scope}\"。");
        if (install.Targets.Count is 0 or > 32)
        {
            errors.Add("install.targets 必须为 1-32 项。");
            return;
        }

        foreach (var target in install.Targets)
        {
            if (!IsSafeRelativePath(target.Source) || !target.Source.StartsWith("payload/3dsmax/", StringComparison.Ordinal))
                errors.Add($"target.source \"{target.Source}\" 必须是 payload/3dsmax/ 下的安全相对路径。");

            if (MvpDestinations.Contains(target.Destination))
                continue;
            errors.Add(Phase2Destinations.Contains(target.Destination)
                ? $"destination \"{target.Destination}\" 属于第二阶段，MVP 不接受。"
                : $"未知 destination \"{target.Destination}\"。");
        }
    }

    private static void ValidateEntryPoints(IReadOnlyList<EntryPoint>? entryPoints, List<string> errors)
    {
        foreach (var entryPoint in entryPoints ?? [])
        {
            if (!KnownEntryPointKinds.Contains(entryPoint.Kind))
                errors.Add($"未知 entryPoint.kind \"{entryPoint.Kind}\"。");
            if (!IsSafeRelativePath(entryPoint.Script))
                errors.Add($"entryPoint.script \"{entryPoint.Script}\" 不是安全相对路径。");
        }
    }

    private static void ValidateDependencies(IReadOnlyList<DependencySpec>? dependencies, List<string> errors)
    {
        foreach (var dependency in dependencies ?? [])
        {
            if (!ToolId.IsCanonical(dependency.Id) && !IdPattern().IsMatch(dependency.Id))
                errors.Add($"依赖 id \"{dependency.Id}\" 格式不合法。");
            if (!RangePattern().IsMatch(dependency.Range))
                errors.Add($"依赖 range \"{dependency.Range}\" 格式不合法。");
        }
    }

    /// <summary>正斜杠相对路径；拒绝盘符、反斜杠、空段和 ./..。</summary>
    public static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains('\\') || path.Contains(':') || path.StartsWith('/'))
            return false;
        var segments = path.Split('/');
        return segments.All(s => s.Length > 0 && s != "." && s != "..");
    }
}
