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
    public DbSet<UserRow> Users => Set<UserRow>();
    public DbSet<AgentReleaseRow> AgentReleases => Set<AgentReleaseRow>();
    public DbSet<FeedbackRow> Feedbacks => Set<FeedbackRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ReleaseRow>().HasKey(r => r.ReleaseId);
        modelBuilder.Entity<ReleaseRow>().HasIndex(r => new { r.ToolId, r.Version });
        modelBuilder.Entity<ActivityEventRow>().HasKey(e => e.EventId);
        modelBuilder.Entity<RefreshTokenRow>().HasKey(t => t.TokenHash);
        modelBuilder.Entity<UserRow>().HasKey(u => u.EmployeeId);
        modelBuilder.Entity<AgentReleaseRow>().HasKey(a => a.Id);
        modelBuilder.Entity<FeedbackRow>().HasKey(f => f.Id);
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

public sealed class UserRow
{
    public required string EmployeeId { get; set; }
    public required string Username { get; set; }
    public string? FeishuOpenId { get; set; }
    public string? FeishuUserId { get; set; }
    /// <summary>逗号分隔的角色（admin/reviewer/publisher）。旧库加列后存量行为 NULL，读取时按空串处理。</summary>
    public string? Roles { get; set; }
}

/// <summary>用户反馈：先落库再投飞书，投递失败不丢内容，后台可补发。</summary>
public sealed class FeedbackRow
{
    public int Id { get; set; }
    /// <summary>tool=针对具体工具；platform=针对平台。</summary>
    public required string Scope { get; set; }
    public string? ToolId { get; set; }
    public string? ToolName { get; set; }
    public required string FromEmployeeId { get; set; }
    public required string FromUsername { get; set; }
    /// <summary>接收人员工号列表（逗号分隔）：上传者+管理员，或平台固定接收人+管理员。</summary>
    public required string ToEmployeeIds { get; set; }
    public required string Message { get; set; }
    public required string Client { get; set; }
    public string? ClientVersion { get; set; }
    public int? MaxYear { get; set; }
    /// <summary>pending/delivered/failed/skipped。</summary>
    public required string DeliveryStatus { get; set; }
    public string? DeliveryError { get; set; }
    public DateTimeOffset AtUtc { get; set; }
}

/// <summary>Agent 版本元数据（数据库存储，后台网页可直接更新，无需重启服务器）。</summary>
public sealed class AgentReleaseRow
{
    public int Id { get; set; }
    public required string Version { get; set; }
    public required string DownloadUrl { get; set; }
    public required string Sha256 { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>员工目录：登录时记录 employeeId→姓名，供后台展示用；并承载角色读写。</summary>
public interface IUserDirectory
{
    void Upsert(EmployeeIdentity user);
    IReadOnlyDictionary<string, string> GetNames(IEnumerable<string> employeeIds);
    EmployeeIdentity ResolveIdentity(string employeeId);
    string[] GetRoles(string employeeId);
    void SetRoles(string employeeId, string[] roles);
    IReadOnlyList<UserRow> GetAllUsers();
}

public sealed class SqliteUserDirectory(IDbContextFactory<MaxHubDb> dbFactory) : IUserDirectory
{
    public void Upsert(EmployeeIdentity user)
    {
        using var db = dbFactory.CreateDbContext();
        var row = db.Users.Find(user.EmployeeId);
        if (row is null)
            db.Users.Add(new UserRow
            {
                EmployeeId = user.EmployeeId,
                Username = user.Username,
                FeishuOpenId = user.OpenId,
                FeishuUserId = user.UserId,
            });
        else
        {
            row.Username = user.Username;
            row.FeishuOpenId = user.OpenId ?? row.FeishuOpenId;
            row.FeishuUserId = user.UserId ?? row.FeishuUserId;
        }
        db.SaveChanges();
    }

    public IReadOnlyDictionary<string, string> GetNames(IEnumerable<string> employeeIds)
    {
        var ids = employeeIds.Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList();
        using var db = dbFactory.CreateDbContext();
        return db.Users.Where(u => ids.Contains(u.EmployeeId))
            .ToDictionary(u => u.EmployeeId, u => u.Username);
    }

    public EmployeeIdentity ResolveIdentity(string employeeId)
    {
        using var db = dbFactory.CreateDbContext();
        var row = db.Users.Find(employeeId);
        return row is null
            ? new EmployeeIdentity(employeeId, employeeId)
            : new EmployeeIdentity(row.EmployeeId, row.Username, row.FeishuOpenId, row.FeishuUserId);
    }

    public string[] GetRoles(string employeeId)
    {
        using var db = dbFactory.CreateDbContext();
        var row = db.Users.Find(employeeId);
        return row is null || string.IsNullOrWhiteSpace(row.Roles) ? [] : SplitRoles(row.Roles);
    }
    public void SetRoles(string employeeId, string[] roles)
    {
        using var db = dbFactory.CreateDbContext();
        var row = db.Users.Find(employeeId);
        if (row is null)
            return;
        row.Roles = string.Join(",", roles.Distinct().OrderBy(r => r));
        db.SaveChanges();
    }

    public IReadOnlyList<UserRow> GetAllUsers()
    {
        using var db = dbFactory.CreateDbContext();
        return db.Users.OrderBy(u => u.Username).ToList();
    }

    private static string[] SplitRoles(string roles) =>
        roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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
