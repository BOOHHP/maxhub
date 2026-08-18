using System.Text.Json;
using MaxHub.Core.Ledger;
using MaxHub.Core.Manifests;

namespace MaxHub.Agent.Core.Install;

/// <summary>installed.json 的读写。账本是卸载、修复与回滚的唯一依据。</summary>
public sealed class LedgerStore(string ledgerPath)
{
    public string LedgerPath => ledgerPath;

    public InstallLedger Load()
    {
        if (!File.Exists(ledgerPath))
            return new InstallLedger();
        return JsonSerializer.Deserialize<InstallLedger>(File.ReadAllText(ledgerPath), ManifestJson.Options)
            ?? new InstallLedger();
    }

    public void Save(InstallLedger ledger)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(ledgerPath))!);
        var tempPath = ledgerPath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(ledger, ManifestJson.Options));
        File.Move(tempPath, ledgerPath, overwrite: true);
    }

    public LedgerEntry? Find(string artifactId, int maxYear) =>
        Load().Entries.FirstOrDefault(e => e.ArtifactId == artifactId && e.MaxVersion == maxYear && e.Active);
}
