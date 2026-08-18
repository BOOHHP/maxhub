using System.Security.Cryptography;
using System.Text;
using MaxHub.Server.Domain;
using Microsoft.EntityFrameworkCore;

namespace MaxHub.Server.Data;

public sealed class MaxHubDb(DbContextOptions<MaxHubDb> options) : DbContext(options)
{
    public DbSet<ReleaseRow> Releases => Set<ReleaseRow>();
    public DbSet<ConnectorRow> Connectors => Set<ConnectorRow>();
    public DbSet<ActivityEventRow> ActivityEvents => Set<ActivityEventRow>();
    public DbSet<InstallEventRow> InstallEvents => Set<InstallEventRow>();
    public DbSet<RefreshTokenRow> RefreshTokens => Set<RefreshTokenRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ReleaseRow>().HasKey(r => r.ReleaseId);
        modelBuilder.Entity<ReleaseRow>().HasIndex(r => new { r.ToolId, r.Version });
        modelBuilder.Entity<ActivityEventRow>().HasKey(e => e.EventId);
        modelBuilder.Entity<RefreshTokenRow>().HasKey(t => t.TokenHash);
    }
}

public sealed class ReleaseRow
{
    public required string ReleaseId { get; set; }
    public required string ToolId { get; set; }
    public required string Version { get; set; }
    public required string ManifestJson { get; set; }
    public required string ArtifactPath { get; set; }
    public required string Sha256 { get; set; }
    public long SizeBytes { get; set; }
    public required string SubmittedBy { get; set; }
    public ReleaseStatus Status { get; set; }
    public required string Channel { get; set; }
    public string? ReviewedBy { get; set; }
    public string? SignatureBase64 { get; set; }
    public DateTimeOffset SubmittedAtUtc { get; set; }
}

public sealed class ConnectorRow
{
    public int Id { get; set; }
    public required string Version { get; set; }
    public int MinMaxYear { get; set; }
    public int MaxMaxYear { get; set; }
    public required string ArtifactPath { get; set; }
    public required string Sha256 { get; set; }
    public long SizeBytes { get; set; }
    public string? SignatureBase64 { get; set; }
}

public sealed class ActivityEventRow
{
    public required string EventId { get; set; }
    public required string EmployeeId { get; set; }
    public required string Type { get; set; }
    public required string Subject { get; set; }
    public string? ClientVersion { get; set; }
    public DateTimeOffset AtUtc { get; set; }
}

public sealed class InstallEventRow
{
    public int Id { get; set; }
    public required string EventId { get; set; }
    public required string EmployeeId { get; set; }
    public required string Type { get; set; }
    public required string Subject { get; set; }
    public string? ClientVersion { get; set; }
    public DateTimeOffset AtUtc { get; set; }
}

public sealed class RefreshTokenRow
{
    public required string TokenHash { get; set; }
    public required string EmployeeId { get; set; }
    public required string Username { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
}

/// <summary>刷新令牌落库（哈希存储），使 MaxHub 会话跨服务端重启存活。</summary>
public interface IRefreshTokenStore
{
    void Save(string refreshToken, EmployeeIdentity user, DateTimeOffset expiresAtUtc);
    /// <summary>一次性消费：命中即删除并返回身份，过期或未知返回 null。</summary>
    EmployeeIdentity? Consume(string refreshToken);
}

public sealed class SqliteRefreshTokenStore(IDbContextFactory<MaxHubDb> dbFactory) : IRefreshTokenStore
{
    public void Save(string refreshToken, EmployeeIdentity user, DateTimeOffset expiresAtUtc)
    {
        using var db = dbFactory.CreateDbContext();
        db.RefreshTokens.Add(new RefreshTokenRow
        {
            TokenHash = Hash(refreshToken),
            EmployeeId = user.EmployeeId,
            Username = user.Username,
            ExpiresAtUtc = expiresAtUtc,
        });
        db.SaveChanges();
    }

    public EmployeeIdentity? Consume(string refreshToken)
    {
        using var db = dbFactory.CreateDbContext();
        var row = db.RefreshTokens.Find(Hash(refreshToken));
        if (row is null)
            return null;
        db.RefreshTokens.Remove(row);
        db.SaveChanges();
        return row.ExpiresAtUtc < DateTimeOffset.UtcNow ? null : new EmployeeIdentity(row.EmployeeId, row.Username);
    }

    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
