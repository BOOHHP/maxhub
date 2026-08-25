using MaxHub.Server.Data;
using MaxHub.Server.Domain;

namespace MaxHub.Server.Services;

/// <summary>
/// 脚本提交待审核后，向全部管理员与审核者发飞书通知（排除提交者本人）。
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
        foreach (var employeeId in recipients)
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
