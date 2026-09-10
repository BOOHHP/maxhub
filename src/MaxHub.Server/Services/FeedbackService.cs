using System.Text.Json;
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
            Status = "open",
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

    /// <summary>处理状态白名单与中文名。</summary>
    public static readonly string[] AllowedStatuses = ["open", "in_progress", "resolved", "wontfix"];
    public static string StatusText(string status) => status switch
    {
        "open" => "待处理",
        "in_progress" => "处理中",
        "resolved" => "已解决",
        "wontfix" => "暂不处理",
        _ => status,
    };

    /// <summary>变更处理状态并写备注；返回更新后的行，反馈不存在或状态非法时返回 null。</summary>
    public FeedbackRow? ChangeStatus(int id, string status, string? note)
    {
        if (!AllowedStatuses.Contains(status))
            return null;
        using var db = dbFactory.CreateDbContext();
        var row = db.Feedbacks.Find(id);
        if (row is null)
            return null;
        row.Status = status;
        row.StatusNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        row.StatusChangedAtUtc = DateTimeOffset.UtcNow;
        db.SaveChanges();
        return row;
    }

    /// <summary>反馈人查看自己的反馈（按时间倒序）。</summary>
    public IReadOnlyList<FeedbackRow> ListMine(string employeeId, int take = 100)
    {
        using var db = dbFactory.CreateDbContext();
        return db.Feedbacks.Where(f => f.FromEmployeeId == employeeId).ToList()
            .OrderByDescending(f => f.AtUtc).Take(take).ToList();
    }

    /// <summary>我是接收人的反馈（工具上传者/平台接收人），可变更其处理状态。</summary>
    public IReadOnlyList<FeedbackRow> ListForRecipient(string employeeId, int take = 100)
    {
        using var db = dbFactory.CreateDbContext();
        return db.Feedbacks.ToList()
            .Where(f => f.ToEmployeeIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Contains(employeeId, StringComparer.Ordinal))
            .OrderByDescending(f => f.AtUtc).Take(take).ToList();
    }

    /// <summary>构建反馈生命周期卡片：详情 + 状态链（当前高亮）+ 按钮。portalBase 形如 http://10.2.13.8:5100。</summary>
    /// <param name="forSubmitter">true=回执视角（反馈人查看进展）；false=接收人视角（去处理）。</param>
    public string BuildCard(FeedbackRow row, string portalBase, bool forSubmitter = false)
    {
        var steps = new (string Key, string Label)[]
        {
            ("open", "待处理"), ("in_progress", "处理中"), ("resolved", "已解决"), ("wontfix", "暂不处理"),
        };
        var status = row.Status ?? "open";
        var chain = string.Join("  →  ", steps.Select(s =>
            s.Key == status ? $"**{s.Label}** ✅" : s.Label));
        var subject = row.Scope == "tool" ? $"工具「{row.ToolName ?? "未知"}」" : "MaxHub 平台";
        var url = $"{portalBase.TrimEnd('/')}/publish.html#feedbacks";
        var noteLine = string.IsNullOrWhiteSpace(row.StatusNote) ? "" : $"\n**备注：**{row.StatusNote}";

        var card = new
        {
            config = new { wide_screen_mode = true },
            header = new
            {
                template = status == "resolved" ? "green" : status == "wontfix" ? "grey" : "blue",
                title = new { tag = "plain_text", content = forSubmitter
                    ? $"MaxHub 反馈进展（#{row.Id}）"
                    : row.Scope == "tool" ? "MaxHub 工具反馈" : "MaxHub 平台反馈" },
            },
            elements = new object[]
            {
                new
                {
                    tag = "div",
                    text = new { tag = "lark_md", content =
                        $"**对象：**{subject}\n**反馈人：**{row.FromUsername}\n**内容：**{row.Message}{noteLine}" },
                },
                new { tag = "hr" },
                new
                {
                    tag = "div",
                    text = new { tag = "lark_md", content = $"**处理状态：**{chain}" },
                },
                new
                {
                    tag = "note",
                    elements = new object[] { new { tag = "plain_text", content = forSubmitter
                        ? "点击下方按钮查看该反馈的最新进展"
                        : "点击下方按钮打开 MaxHub 网页，在「反馈跟踪」区查看或更新状态" } },
                },
                new
                {
                    tag = "action",
                    actions = new object[]
                    {
                        new
                        {
                            tag = "button",
                            text = new { tag = "plain_text", content = forSubmitter ? "查看进展" : "去处理" },
                            type = "primary",
                            url = url,
                        },
                    },
                },
            },
        };
        return JsonSerializer.Serialize(card, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    /// <summary>以卡片形式投递给全部接收人；卡片失败时回退纯文本，保证送达。</summary>
    public async Task<(string Status, string? Error)> DeliverCardAsync(FeedbackRow row, string portalBase)
    {
        var card = BuildCard(row, portalBase);
        string? firstError = null;
        var delivered = 0;
        foreach (var employeeId in row.ToEmployeeIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var identity = users.ResolveIdentity(employeeId);
            try
            {
                await sender.SendCardAsync(identity, card);
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
