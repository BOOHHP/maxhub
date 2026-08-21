using System.Text.Json;
using MaxHub.Server.Domain;

namespace MaxHub.Server.Services;

/// <summary>飞书应用消息投递：tenant_access_token + im/v1/messages，AppSecret 仅存服务端。</summary>
public interface IFeishuMessageSender
{
    Task SendTextAsync(EmployeeIdentity target, string text, CancellationToken cancellationToken = default);
}

public sealed class FeishuMessagingDisabledException() : Exception("飞书消息未配置");

public sealed class FeishuMessagingException(string message, bool invalidReceiver) : Exception(message)
{
    /// <summary>接收标识无效（可换下一种 ID 类型重试）；权限/网络类错误不重试。</summary>
    public bool InvalidReceiver { get; } = invalidReceiver;
}

public sealed class FeishuMessageClient(HttpClient http, FeishuAuthOptions options) : IFeishuMessageSender
{
    private const string BaseUrl = "https://open.feishu.cn";
    private string? _tenantToken;
    private DateTimeOffset _tokenExpiresAt = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);

    public async Task SendTextAsync(EmployeeIdentity target, string text, CancellationToken cancellationToken = default)
    {
        if (!options.IsConfigured)
            throw new FeishuMessagingDisabledException();

        var token = await GetTenantTokenAsync(cancellationToken);
        FeishuMessagingException? last = null;
        foreach (var (type, receiveId) in CandidateIds(target))
        {
            try
            {
                await PostAsync(type, receiveId, text, token, cancellationToken);
                return;
            }
            catch (FeishuMessagingException ex)
            {
                last = ex;
                if (!ex.InvalidReceiver)
                    throw;
            }
        }
        throw last ?? new FeishuMessagingException("无可用飞书接收标识", invalidReceiver: false);
    }

    /// <summary>依次尝试 open_id/user_id 两种接收标识，兼容仅存员工号的历史用户。</summary>
    private static IEnumerable<(string Type, string Id)> CandidateIds(EmployeeIdentity target)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (type, id) in new[]
        {
            ("open_id", target.OpenId),
            ("user_id", target.UserId),
            ("open_id", (string?)target.EmployeeId),
            ("user_id", (string?)target.EmployeeId),
        })
        {
            if (string.IsNullOrWhiteSpace(id) || !seen.Add(type + ":" + id))
                continue;
            yield return (type, id!);
        }
    }

    private async Task PostAsync(string type, string receiveId, string text, string token, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync(
            $"{BaseUrl}/open-apis/im/v1/messages?receive_id_type={type}",
            new { receive_id = receiveId, msg_type = "text", content = JsonSerializer.Serialize(new { text }) },
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var json = JsonDocument.Parse(body);
        var code = json.RootElement.TryGetProperty("code", out var codeProp) ? codeProp.GetInt32() : -1;
        if (code != 0)
        {
            var msg = json.RootElement.TryGetProperty("msg", out var msgProp) ? msgProp.GetString() ?? body : body;
            throw new FeishuMessagingException($"飞书发送失败（{code}）：{msg}", invalidReceiver: true);
        }
    }

    private async Task<string> GetTenantTokenAsync(CancellationToken cancellationToken)
    {
        if (_tenantToken is not null && DateTimeOffset.UtcNow < _tokenExpiresAt)
            return _tenantToken;
        await _tokenGate.WaitAsync(cancellationToken);
        try
        {
            if (_tenantToken is not null && DateTimeOffset.UtcNow < _tokenExpiresAt)
                return _tenantToken;
            using var response = await http.PostAsJsonAsync(
                $"{BaseUrl}/open-apis/auth/v3/tenant_access_token/internal",
                new { app_id = options.AppId, app_secret = options.AppSecret },
                cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var json = JsonDocument.Parse(body);
            var code = json.RootElement.TryGetProperty("code", out var codeProp) ? codeProp.GetInt32() : -1;
            if (code != 0)
                throw new FeishuMessagingException($"获取 tenant_access_token 失败：{body}", invalidReceiver: false);
            _tenantToken = json.RootElement.GetProperty("tenant_access_token").GetString()!;
            var expire = json.RootElement.TryGetProperty("expire", out var expireProp) ? expireProp.GetInt32() : 7200;
            _tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expire - 300));
            return _tenantToken;
        }
        finally
        {
            _tokenGate.Release();
        }
    }
}
