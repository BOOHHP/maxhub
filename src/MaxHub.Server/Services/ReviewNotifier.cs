using MaxHub.Server.Data;
using MaxHub.Server.Domain;

namespace MaxHub.Server.Services;

/// <summary>
/// 脚本提交待审核后，向全部管理员与审核者发飞书通知（排除提交者本人）；
/// 审核通过后通知提交者，并向全部登录过的用户推送新工具上架。
/// 通知失败不阻断提交流程：审核队列始终是权威来源，后台仍能看到待审核项。
/// </summary>
public sealed class ReviewNotifier(RoleService roles, IUserDirectory users, IFeishuMessageSender sender)
{
    public async Task NotifyAsync(EmployeeIdentity submitter, string toolName, string version)
    {
        var recipients = roles.GetAdminEmployeeIds()
            .Concat(roles.GetReviewerEmployeeIds())
            .Where(id => !string.Equals(id, submitter.EmployeeId, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (recipients.Count == 0)
            return;

        var text = $"【MaxHub 待审核】{submitter.Username} 提交了工具「{toolName}」v{version}，请到后台审核。";
        await SendToAll(recipients, text);
    }

    /// <summary>审核通过：通知提交者，并向全部登录过的用户推送新工具上架（均排除提交者，避免重复打扰）。</summary>
    public async Task NotifyApprovedAsync(EmployeeIdentity submitter, string toolName, string version)
    {
        var approvedText = $"【MaxHub 审核通过】你提交的工具「{toolName}」v{version} 已审核通过并上架。";
        try
        {
            await sender.SendTextAsync(users.ResolveIdentity(submitter.EmployeeId), approvedText);
        }
        catch
        {
            // 提交者通知失败不影响上架广播
        }

        var recipients = users.GetAllUsers()
            .Select(u => u.EmployeeId)
            .Where(id => !string.Equals(id, submitter.EmployeeId, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var publishedText = $"【MaxHub 新工具上架】{submitter.Username} 发布的工具「{toolName}」v{version} 已上架，可在工具市场或 Agent 中安装。";
        await SendToAll(recipients, publishedText);
    }

    private async Task SendToAll(IReadOnlyList<string> employeeIds, string text)
    {
        foreach (var employeeId in employeeIds)
        {
            try
            {
                await sender.SendTextAsync(users.ResolveIdentity(employeeId), text);
            }
            catch
            {
                // 单个接收人投递失败不影响其他人；审核队列兜底
            }
        }
    }
}
