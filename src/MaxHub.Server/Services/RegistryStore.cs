using System.Text.Json;
using MaxHub.Core.Manifests;
using MaxHub.Core.Packaging;
using MaxHub.Server.Data;
using MaxHub.Server.Domain;
using MaxHub.Server.Storage;
using Microsoft.EntityFrameworkCore;

namespace MaxHub.Server.Services;

public sealed record SubmitOutcome(bool Success, string? ReleaseId, IReadOnlyList<string> Errors);

/// <summary>SQLite 持久化的注册表；制品文件在 dataDir，元数据在 maxhub.db。已发布制品均携带 ECDSA 签名。</summary>
public sealed class RegistryStore(string dataDir, IDbContextFactory<MaxHubDb> dbFactory, SigningKeyStore signer)
{
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
                using var db = dbFactory.CreateDbContext();
                var duplicate = db.Releases.Any(r =>
                    r.ToolId == manifest.Id && r.Version == manifest.Version && r.Status != ReleaseStatus.Rejected);
                if (duplicate)
                    return new SubmitOutcome(false, null, [$"版本 {manifest.Version} 已存在。"]);

                File.Copy(tempPath, artifactPath, overwrite: true);
                var row = new ReleaseRow
                {
                    ReleaseId = Guid.NewGuid().ToString("N"),
                    ToolId = manifest.Id,
                    Version = manifest.Version,
                    ManifestJson = JsonSerializer.Serialize(manifest, ManifestJson.Options),
                    ArtifactPath = artifactPath,
                    Sha256 = ToolPackage.ComputeSha256(artifactPath),
                    SizeBytes = new FileInfo(artifactPath).Length,
                    SubmittedBy = submitter.EmployeeId,
                    Status = ReleaseStatus.PendingReview,
                    Channel = "internal",
                    SubmittedAtUtc = DateTimeOffset.UtcNow,
                };
                db.Releases.Add(row);
                db.SaveChanges();
                return new SubmitOutcome(true, row.ReleaseId, []);
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
            using var db = dbFactory.CreateDbContext();
            var row = db.Releases.Find(releaseId);
            if (row is null || row.Status != ReleaseStatus.PendingReview)
                return false;
            row.Status = approve ? ReleaseStatus.Published : ReleaseStatus.Rejected;
            if (approve)
            {
                row.Channel = channel;
                row.SignatureBase64 = signer.Sign(row.Sha256); // 发布即签名，Agent 验签后才允许安装
            }
            row.ReviewedBy = reviewer.EmployeeId;
            db.SaveChanges();
            return true;
        }
    }

    /// <summary>返回对指定 Max 年份可见的已发布工具索引（每工具取最高已发布版本）。</summary>
    public IReadOnlyList<ToolRelease> QueryIndex(int maxYear)
    {
        using var db = dbFactory.CreateDbContext();
        var published = db.Releases.Where(r => r.Status == ReleaseStatus.Published).AsNoTracking().ToList();
        return published
            .Select(ToDomain)
            .Where(r => Covers(r.Manifest, maxYear))
            .GroupBy(r => r.Manifest.Id)
            .Select(g => g.OrderByDescending(r => Version.Parse(TrimPreRelease(r.Manifest.Version))).First())
            .OrderBy(r => r.Manifest.Id, StringComparer.Ordinal)
            .ToList();
    }

    public IReadOnlyList<ToolRelease> GetToolReleases(string toolId)
    {
        using var db = dbFactory.CreateDbContext();
        return db.Releases
            .Where(r => r.ToolId == toolId && r.Status == ReleaseStatus.Published)
            .AsNoTracking().ToList()
            .Select(ToDomain)
            .ToList();
    }

    public ToolRelease? GetPublished(string toolId, string version) =>
        GetToolReleases(toolId).FirstOrDefault(r => r.Manifest.Version == version);

    /// <summary>管理后台：全部版本（含待审核/已拒绝），按提交时间倒序。</summary>
    public IReadOnlyList<ToolRelease> GetAllReleases()
    {
        using var db = dbFactory.CreateDbContext();
        return db.Releases.AsNoTracking()
            .ToList() // SQLite 不支持 DateTimeOffset ORDER BY，取回后内存排序
            .OrderByDescending(r => r.SubmittedAtUtc)
            .Select(ToDomain)
            .ToList();
    }

    public void RegisterConnector(ConnectorRelease release)
    {
        using var db = dbFactory.CreateDbContext();
        db.Connectors.Add(new ConnectorRow
        {
            Version = release.Version,
            MinMaxYear = release.MinMaxYear,
            MaxMaxYear = release.MaxMaxYear,
            ArtifactPath = release.ArtifactPath,
            Sha256 = release.Sha256,
            SizeBytes = release.SizeBytes,
            SignatureBase64 = signer.Sign(release.Sha256),
        });
        db.SaveChanges();
    }

    public IReadOnlyList<ConnectorRelease> QueryConnectors(int maxYear)
    {
        using var db = dbFactory.CreateDbContext();
        return db.Connectors
            .Where(c => maxYear >= c.MinMaxYear && maxYear <= c.MaxMaxYear)
            .AsNoTracking().ToList()
            .Select(c => new ConnectorRelease
            {
                Version = c.Version,
                MinMaxYear = c.MinMaxYear,
                MaxMaxYear = c.MaxMaxYear,
                ArtifactPath = c.ArtifactPath,
                Sha256 = c.Sha256,
                SizeBytes = c.SizeBytes,
                SignatureBase64 = c.SignatureBase64,
            })
            .ToList();
    }

    public ConnectorRelease? GetConnector(int maxYear, string version) =>
        QueryConnectors(maxYear).FirstOrDefault(c => c.Version == version);

    /// <summary>幂等：重复 eventId 返回 false，不重复计数。</summary>
    public bool AddActivityEvent(ActivityEvent activityEvent)
    {
        lock (_writeLock)
        {
            using var db = dbFactory.CreateDbContext();
            if (db.ActivityEvents.Find(activityEvent.EventId) is not null)
                return false;
            db.ActivityEvents.Add(new ActivityEventRow
            {
                EventId = activityEvent.EventId,
                EmployeeId = activityEvent.EmployeeId,
                Type = activityEvent.Type,
                Subject = activityEvent.Subject,
                ClientVersion = activityEvent.ClientVersion,
                AtUtc = activityEvent.AtUtc,
            });
            db.SaveChanges();
            return true;
        }
    }

    public void AddInstallEvent(ActivityEvent installEvent)
    {
        using var db = dbFactory.CreateDbContext();
        db.InstallEvents.Add(new InstallEventRow
        {
            EventId = installEvent.EventId,
            EmployeeId = installEvent.EmployeeId,
            Type = installEvent.Type,
            Subject = installEvent.Subject,
            ClientVersion = installEvent.ClientVersion,
            AtUtc = installEvent.AtUtc,
        });
        db.SaveChanges();
    }

    public int CountActivity(string employeeId, string type)
    {
        using var db = dbFactory.CreateDbContext();
        return db.ActivityEvents.Count(e => e.EmployeeId == employeeId && e.Type == type);
    }

    /// <summary>紧急撤回：已发布→已撤回，索引与下载立即不可见。</summary>
    public bool Withdraw(string releaseId, EmployeeIdentity @operator)
    {
        lock (_writeLock)
        {
            using var db = dbFactory.CreateDbContext();
            var row = db.Releases.Find(releaseId);
            if (row is null || row.Status != ReleaseStatus.Published)
                return false;
            row.Status = ReleaseStatus.Withdrawn;
            row.ReviewedBy = @operator.EmployeeId;
            db.SaveChanges();
            return true;
        }
    }

    public IReadOnlyList<ConnectorRelease> GetAllConnectors()
    {
        using var db = dbFactory.CreateDbContext();
        return db.Connectors.AsNoTracking().ToList()
            .OrderByDescending(c => Version.Parse(c.Version))
            .Select(c => new ConnectorRelease
            {
                Version = c.Version,
                MinMaxYear = c.MinMaxYear,
                MaxMaxYear = c.MaxMaxYear,
                ArtifactPath = c.ArtifactPath,
                Sha256 = c.Sha256,
                SizeBytes = c.SizeBytes,
                SignatureBase64 = c.SignatureBase64,
            })
            .ToList();
    }

    public sealed record SubjectStats(string Subject, int Downloads, int Installs);

    // ---- Agent 版本元数据（DB 存储，后台可更新，无需重启） ----
    public AgentReleaseRow? GetAgentRelease()
    {
        using var db = dbFactory.CreateDbContext();
        return db.AgentReleases.AsNoTracking().OrderByDescending(a => a.Id).FirstOrDefault();
    }

    public void SetAgentRelease(string version, string downloadUrl, string sha256)
    {
        lock (_writeLock)
        {
            using var db = dbFactory.CreateDbContext();
            db.AgentReleases.Add(new AgentReleaseRow
            {
                Version = version,
                DownloadUrl = downloadUrl,
                Sha256 = sha256,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            });
            db.SaveChanges();
        }
    }

    /// <summary>使用统计：按主题（toolId@version）聚合下载/安装次数，及活跃用户数。</summary>
    public (IReadOnlyList<SubjectStats> Subjects, int ActiveUsers) GetStats()
    {
        using var db = dbFactory.CreateDbContext();
        var downloads = db.ActivityEvents.Where(e => e.Type == "download")
            .GroupBy(e => e.Subject).Select(g => new { g.Key, Count = g.Count() }).ToDictionary(x => x.Key, x => x.Count);
        var installs = db.InstallEvents.Where(e => e.Type == "install")
            .GroupBy(e => e.Subject).Select(g => new { g.Key, Count = g.Count() }).ToDictionary(x => x.Key, x => x.Count);
        var subjects = downloads.Keys.Union(installs.Keys)
            .Select(s => new SubjectStats(s, downloads.GetValueOrDefault(s), installs.GetValueOrDefault(s)))
            .OrderByDescending(s => s.Downloads + s.Installs)
            .ToList();
        var activeUsers = db.ActivityEvents.Select(e => e.EmployeeId)
            .Union(db.InstallEvents.Select(e => e.EmployeeId)).Distinct().Count();
        return (subjects, activeUsers);
    }

    /// <summary>启动时对存量无签名的已发布制品补签（签名功能上线前的历史数据）。</summary>
    public void SignMissingSignatures()
    {
        lock (_writeLock)
        {
            using var db = dbFactory.CreateDbContext();
            foreach (var row in db.Releases.Where(r => r.Status == ReleaseStatus.Published && r.SignatureBase64 == null))
                row.SignatureBase64 = signer.Sign(row.Sha256);
            foreach (var row in db.Connectors.Where(c => c.SignatureBase64 == null))
                row.SignatureBase64 = signer.Sign(row.Sha256);
            db.SaveChanges();
        }
    }

    private static ToolRelease ToDomain(ReleaseRow row) => new()
    {
        ReleaseId = row.ReleaseId,
        Manifest = JsonSerializer.Deserialize<ToolManifest>(row.ManifestJson, ManifestJson.Options)!,
        ArtifactPath = row.ArtifactPath,
        Sha256 = row.Sha256,
        SizeBytes = row.SizeBytes,
        SubmittedBy = row.SubmittedBy,
        Status = row.Status,
        Channel = row.Channel,
        ReviewedBy = row.ReviewedBy,
        SignatureBase64 = row.SignatureBase64,
        SubmittedAtUtc = row.SubmittedAtUtc,
    };

    private static bool Covers(ToolManifest manifest, int maxYear) =>
        maxYear >= manifest.Compatibility.MinVersion && maxYear <= manifest.Compatibility.MaxVersion;

    private static string TrimPreRelease(string version)
    {
        var dash = version.IndexOf('-');
        return dash < 0 ? version : version[..dash];
    }
}
