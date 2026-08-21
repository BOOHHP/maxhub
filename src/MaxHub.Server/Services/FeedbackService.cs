using MaxHub.Server.Data;
using MaxHub.Server.Domain;
using Microsoft.EntityFrameworkCore;

namespace MaxHub.Server.Services;

/// <summary>滑动窗口限流：每用户每 scope 每小时最多 maxPerHour 条，防止骚扰接收人。</summary>
public sealed class FeedbackRateLimiter(int maxPerHour)
{
    private readonly Dictionary<string, Queue<DateTimeOffset>> _hits = new(StringComparer.Ordinal);

    public bool TryRegister(string key, DateTimeOffset now)
    {
        lock (_hits)
        {
            if (!_hits.TryGetValue(key, out var queue))
                _hits[key] = queue = new Queue<DateTimeOffset>();
            while (queue.Count > 0 && queue.Peek() < now.AddHours(-1))
                queue.Dequeue();
            if (queue.Count >= maxPerHour)
                return false;
            queue.Enqueue(now);
            return true;
        }
    }
}

/// <summary>
/// 反馈管道：先落库再投飞书。投递失败不丢内容，后台可补发。
/// 接收人规则：tool=最新已发布版本的上传者+全部管理员；platform=配置的平台接收人。
/// </summary>
public sealed class FeedbackService(
    IDbContextFactory<MaxHubDb> dbFactory,
    RegistryStore registry,
    RoleService roles,
    IUserDirectory users,
    IFeishuMessageSender sender)
{
    public FeedbackRow Save(
        string scope, string? toolId, string? toolName, EmployeeIdentity from,
        string[] toEmployeeIds, string message, string client, string? clientVersion, int? maxYear)
    {
        using var db = dbFactory.CreateDbContext();
        var row = new FeedbackRow
        {
            Scope = scope,
            ToolId = toolId,
            ToolName = toolName,
            FromEmployeeId = from.EmployeeId,
            FromUsername = from.Username,
            ToEmployeeIds = string.Join(",", toEmployeeIds),
            Message = message,
            Client = client,
            ClientVersion = clientVersion,
            MaxYear = maxYear,
            DeliveryStatus = "pending",
            AtUtc = DateTimeOffset.UtcNow,
        };
        db.Feedbacks.Add(row);
        db.SaveChanges();
        return row;
    }

    public IReadOnlyList<FeedbackRow> List(int take = 200)
    {
        // SQLite 不支持 DateTimeOffset ORDER BY：先物化再内存排序
        using var db = dbFactory.CreateDbContext();
        return db.Feedbacks.ToList().OrderByDescending(f => f.AtUtc).Take(take).ToList();
    }

    public FeedbackRow? Get(int id)
    {
        using var db = dbFactory.CreateDbContext();
        return db.Feedbacks.Find(id);
    }

    /// <summary>解析接收人：tool 发给最新已发布版本上传者并抄送管理员；platform 发给配置接收人。</summary>
    public (string[] Recipients, string? ToolName) ResolveRecipients(string scope, string? toolId, string[] platformRecipients)
    {
        if (scope == "platform")
        {
            var fallback = platformRecipients.Length > 0 ? platformRecipients : roles.GetAdminEmployeeIds();
            return (fallback.Distinct(StringComparer.Ordinal).ToArray(), null);
        }

        var release = registry.GetAllReleases()
            .Where(r => r.Manifest.Id == toolId && r.Status == ReleaseStatus.Published)
            .OrderByDescending(r => r.SubmittedAtUtc)
            .FirstOrDefault();
        var owner = release?.SubmittedBy;
        var toolName = release?.Manifest.Name;
        var recipients = new List<string>();
        if (!string.IsNullOrWhiteSpace(owner))
            recipients.Add(owner);
        recipients.AddRange(roles.GetAdminEmployeeIds());
        return (recipients.Distinct(StringComparer.Ordinal).ToArray(), toolName);
    }

    public string BuildText(FeedbackRow row)
    {
        var lines = new List<string>
        {
            row.Scope == "tool" ? "【MaxHub 工具反馈】" : "【MaxHub 平台反馈】",
        };
        if (row.Scope == "tool")
            lines.Add($"工具：{row.ToolName ?? "未知工具"}（{ToolIdPublic(row.ToolId)}）");
        lines.Add($"反馈人：{row.FromUsername}");
        var source = row.Client == "connector" ? "Max 工具中心" : row.Client == "agent" ? "MaxHub Agent" : row.Client;
        if (!string.IsNullOrWhiteSpace(row.ClientVersion))
            source += $" {row.ClientVersion}";
        if (row.MaxYear is { } year)
            source += $" / Max {year}";
        lines.Add($"来源：{source}");
        lines.Add($"内容：{row.Message}");
        return string.Join("\n", lines);
    }

    private static string ToolIdPublic(string? toolId) =>
        string.IsNullOrWhiteSpace(toolId) ? "" : MaxHub.Core.Manifests.ToolId.PublicCode(toolId);

    /// <summary>逐人投递；全部成功=delivered，未配置=skipped，其余=failed（保留首条错误）。</summary>
    public async Task<(string Status, string? Error)> DeliverAsync(FeedbackRow row)
    {
        var text = BuildText(row);
        string? firstError = null;
        var delivered = 0;
        foreach (var employeeId in row.ToEmployeeIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var identity = users.ResolveIdentity(employeeId);
            try
            {
                await sender.SendTextAsync(identity, text);
                delivered++;
            }
            catch (FeishuMessagingDisabledException)
            {
                return UpdateStatus(row, "skipped", null);
            }
            catch (Exception ex)
            {
                firstError ??= $"{employeeId}: {ex.Message}";
            }
        }

        return firstError is null
            ? UpdateStatus(row, "delivered", null)
            : UpdateStatus(row, delivered > 0 ? "partial" : "failed", firstError);
    }

    private (string Status, string? Error) UpdateStatus(FeedbackRow row, string status, string? error)
    {
        using var db = dbFactory.CreateDbContext();
        var stored = db.Feedbacks.Find(row.Id);
        if (stored is not null)
        {
            stored.DeliveryStatus = status;
            stored.DeliveryError = error;
            db.SaveChanges();
        }
        row.DeliveryStatus = status;
        row.DeliveryError = error;
        return (status, error);
    }
}
