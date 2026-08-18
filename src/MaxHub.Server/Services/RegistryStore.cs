using System.Collections.Concurrent;
using MaxHub.Core.Manifests;
using MaxHub.Core.Packaging;
using MaxHub.Server.Domain;

namespace MaxHub.Server.Services;

public sealed record SubmitOutcome(bool Success, string? ReleaseId, IReadOnlyList<string> Errors);

public sealed class RegistryStore(string dataDir)
{
    private readonly ConcurrentDictionary<string, List<ToolRelease>> _releasesByTool = new();
    private readonly List<ConnectorRelease> _connectors = [];
    private readonly ConcurrentDictionary<string, ActivityEvent> _activityEvents = new();
    private readonly List<ActivityEvent> _installEvents = [];
    private readonly object _writeLock = new();

    public SubmitOutcome SubmitRelease(EmployeeIdentity submitter, Stream packageStream)
    {
        var tempPath = Path.Combine(dataDir, "incoming", $"{Guid.NewGuid():N}.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
        using (var file = File.Create(tempPath))
            packageStream.CopyTo(file);

        try
        {
            ToolManifest manifest;
            try
            {
                manifest = ToolPackage.ReadManifest(tempPath);
            }
            catch (Exception ex)
            {
                return new SubmitOutcome(false, null, [$"无法读取 manifest：{ex.Message}"]);
            }

            var validation = ManifestValidator.Validate(manifest);
            if (!validation.IsValid)
                return new SubmitOutcome(false, null, validation.Errors);

            var contents = ToolPackage.VerifyContents(tempPath, manifest);
            if (!contents.IsValid)
                return new SubmitOutcome(false, null, contents.Errors);

            var artifactPath = Path.Combine(dataDir, "artifacts", manifest.Id, $"{manifest.Version}.zip");
            Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);

            lock (_writeLock)
            {
                var releases = _releasesByTool.GetOrAdd(manifest.Id, _ => []);
                if (releases.Any(r => r.Manifest.Version == manifest.Version && r.Status != ReleaseStatus.Rejected))
                    return new SubmitOutcome(false, null, [$"版本 {manifest.Version} 已存在。"]);

                File.Copy(tempPath, artifactPath, overwrite: true);
                var release = new ToolRelease
                {
                    ReleaseId = Guid.NewGuid().ToString("N"),
                    Manifest = manifest,
                    ArtifactPath = artifactPath,
                    Sha256 = ToolPackage.ComputeSha256(artifactPath),
                    SizeBytes = new FileInfo(artifactPath).Length,
                    SubmittedBy = submitter.EmployeeId,
                };
                releases.Add(release);
                return new SubmitOutcome(true, release.ReleaseId, []);
            }
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    public bool Review(string releaseId, bool approve, string channel, EmployeeIdentity reviewer)
    {
        lock (_writeLock)
        {
            var release = AllReleases().FirstOrDefault(r => r.ReleaseId == releaseId);
            if (release is null || release.Status != ReleaseStatus.PendingReview)
                return false;
            release.Status = approve ? ReleaseStatus.Published : ReleaseStatus.Rejected;
            release.Channel = approve ? channel : release.Channel;
            release.ReviewedBy = reviewer.EmployeeId;
            return true;
        }
    }

    /// <summary>返回对指定 Max 年份可见的已发布工具索引（每工具取最高已发布版本）。</summary>
    public IReadOnlyList<ToolRelease> QueryIndex(int maxYear) =>
        _releasesByTool.Values
            .Select(releases => releases
                .Where(r => r.Status == ReleaseStatus.Published && Covers(r.Manifest, maxYear))
                .OrderByDescending(r => Version.Parse(TrimPreRelease(r.Manifest.Version)))
                .FirstOrDefault())
            .Where(r => r is not null)
            .Select(r => r!)
            .OrderBy(r => r.Manifest.Id, StringComparer.Ordinal)
            .ToList();

    public IReadOnlyList<ToolRelease> GetToolReleases(string toolId) =>
        _releasesByTool.TryGetValue(toolId, out var releases)
            ? releases.Where(r => r.Status == ReleaseStatus.Published).ToList()
            : [];

    public ToolRelease? GetPublished(string toolId, string version) =>
        GetToolReleases(toolId).FirstOrDefault(r => r.Manifest.Version == version);

    public void RegisterConnector(ConnectorRelease release)
    {
        lock (_writeLock)
            _connectors.Add(release);
    }

    public IReadOnlyList<ConnectorRelease> QueryConnectors(int maxYear) =>
        _connectors.Where(c => maxYear >= c.MinMaxYear && maxYear <= c.MaxMaxYear).ToList();

    public ConnectorRelease? GetConnector(int maxYear, string version) =>
        QueryConnectors(maxYear).FirstOrDefault(c => c.Version == version);

    /// <summary>幂等：重复 eventId 返回 false，不重复计数。</summary>
    public bool AddActivityEvent(ActivityEvent activityEvent) =>
        _activityEvents.TryAdd(activityEvent.EventId, activityEvent);

    public void AddInstallEvent(ActivityEvent installEvent)
    {
        lock (_writeLock)
            _installEvents.Add(installEvent);
    }

    public int CountActivity(string employeeId, string type) =>
        _activityEvents.Values.Count(e => e.EmployeeId == employeeId && e.Type == type);

    private IEnumerable<ToolRelease> AllReleases() => _releasesByTool.Values.SelectMany(r => r);

    private static bool Covers(ToolManifest manifest, int maxYear) =>
        maxYear >= manifest.Compatibility.MinVersion && maxYear <= manifest.Compatibility.MaxVersion;

    private static string TrimPreRelease(string version)
    {
        var dash = version.IndexOf('-');
        return dash < 0 ? version : version[..dash];
    }
}
