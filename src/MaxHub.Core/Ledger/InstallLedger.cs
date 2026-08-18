namespace MaxHub.Core.Ledger;

public sealed class InstallLedger
{
    public int SchemaVersion { get; init; } = 1;
    public List<LedgerEntry> Entries { get; init; } = [];
}

public sealed class LedgerEntry
{
    public string ArtifactId { get; init; } = "";
    public string ArtifactType { get; init; } = "tool";
    public string Version { get; init; } = "";
    public int MaxVersion { get; init; }
    public string Scope { get; init; } = "user";
    public List<LedgerFile> Files { get; init; } = [];
    public DateTimeOffset InstalledAtUtc { get; init; }
    public string? BackupId { get; init; }
    public bool Active { get; init; }
}

public sealed class LedgerFile
{
    public string Destination { get; init; } = "";
    public string RelativePath { get; init; } = "";
    public string Sha256 { get; init; } = "";
}
