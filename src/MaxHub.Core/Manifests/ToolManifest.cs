using System.Text.Json;
using System.Text.Json.Serialization;

namespace MaxHub.Core.Manifests;

public static class ManifestJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public sealed class ToolManifest
{
    public int SchemaVersion { get; init; }
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string HostType { get; init; } = "";
    public string? Description { get; init; }
    public Compatibility Compatibility { get; init; } = new();
    public InstallSpec Install { get; init; } = new();
    public IReadOnlyList<EntryPoint>? EntryPoints { get; init; }
    public IReadOnlyList<DependencySpec>? Dependencies { get; init; }
    public IReadOnlyList<string>? Permissions { get; init; }
    public Integrity? Integrity { get; init; }
}

public sealed class Compatibility
{
    public int MinVersion { get; init; }
    public int MaxVersion { get; init; }
    public IReadOnlyList<string> Platforms { get; init; } = [];
}

public sealed class InstallSpec
{
    public string Scope { get; init; } = "";
    public bool RestartRequired { get; init; }
    public IReadOnlyList<InstallTarget> Targets { get; init; } = [];
}

public sealed class InstallTarget
{
    public string Source { get; init; } = "";
    public string Destination { get; init; } = "";
}

public sealed class EntryPoint
{
    public string Kind { get; init; } = "";
    public string Script { get; init; } = "";
    public string? Category { get; init; }
}

public sealed class DependencySpec
{
    public string Id { get; init; } = "";
    public string Range { get; init; } = "";
}

public sealed class Integrity
{
    public string Sha256 { get; init; } = "";
}
