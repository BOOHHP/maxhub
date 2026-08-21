using MaxHub.Server.Data;

namespace MaxHub.Server.Services;

/// <summary>平台角色常量。</summary>
public static class Roles
{
    public const string Admin = "admin";
    public const string Reviewer = "reviewer";
    public const string Publisher = "publisher";
}

/// <summary>
/// 角色解析：引导配置（appsettings Roles:*）优先，其次 DB 中管理员授予的角色，最后默认 publisher。
/// 引导配置用于首次部署时把创建者设为 admin，日常授权走 DB（后台成员角色面板）。
/// </summary>
public sealed class RoleService(IUserDirectory users, string[] bootstrapAdmins, string[] bootstrapReviewers, string[] bootstrapPublishers)
{
    public string[] Resolve(string employeeId)
    {
        var roles = new HashSet<string>(StringComparer.Ordinal);
        if (bootstrapAdmins.Contains(employeeId)) roles.Add(Roles.Admin);
        if (bootstrapReviewers.Contains(employeeId)) roles.Add(Roles.Reviewer);
        if (bootstrapPublishers.Contains(employeeId)) roles.Add(Roles.Publisher);
        foreach (var role in users.GetRoles(employeeId))
            roles.Add(role);
        if (roles.Count == 0)
            roles.Add(Roles.Publisher);
        return roles.OrderBy(r => r).ToArray();
    }

    public bool IsIn(string[] roles, string role) => roles.Contains(role);

    /// <summary>全部管理员：引导配置 + 数据库中授予 admin 的用户，用于反馈抄送。</summary>
    public string[] GetAdminEmployeeIds()
    {
        var ids = new HashSet<string>(bootstrapAdmins, StringComparer.Ordinal);
        foreach (var user in users.GetAllUsers())
            if (IsIn(SplitRoles(user.Roles), Roles.Admin))
                ids.Add(user.EmployeeId);
        return ids.OrderBy(id => id, StringComparer.Ordinal).ToArray();
    }

    private static string[] SplitRoles(string? roles) =>
        string.IsNullOrWhiteSpace(roles) ? [] : roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
