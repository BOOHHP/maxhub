using System.Collections.Concurrent;
using System.Security.Cryptography;
using MaxHub.Server.Domain;

namespace MaxHub.Server.Services;

public sealed record QrSessionView(string SessionId, string AuthorizeUrl, DateTimeOffset ExpiresAtUtc);
public sealed record IssuedSession(string AccessToken, string RefreshToken, EmployeeIdentity User, DateTimeOffset ExpiresAtUtc);

/// <summary>
/// 飞书扫码登录的服务端抽象。生产环境实现企业自建应用的授权码交换；
/// 本仓库内置 mock 提供方用于开发与自动化测试（Auth:EnableMockProvider）。
/// Agent 永远只拿 MaxHub 会话令牌，不接触飞书凭据。
/// </summary>
public interface IFeishuAuthProvider
{
    string BuildAuthorizeUrl(string sessionId);
}

public sealed class MockFeishuAuthProvider : IFeishuAuthProvider
{
    public string BuildAuthorizeUrl(string sessionId) => $"maxhub-mock://qr/{sessionId}";
}

public sealed class AuthService(IFeishuAuthProvider provider)
{
    private static readonly TimeSpan QrTtl = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan AccessTtl = TimeSpan.FromMinutes(30);

    private sealed class QrSession
    {
        public required string Id { get; init; }
        public QrStatus Status { get; set; } = QrStatus.Pending;
        public required DateTimeOffset ExpiresAtUtc { get; init; }
        public EmployeeIdentity? Identity { get; set; }
    }

    private sealed record TokenState(EmployeeIdentity User, DateTimeOffset ExpiresAtUtc);

    private readonly ConcurrentDictionary<string, QrSession> _qrSessions = new();
    private readonly ConcurrentDictionary<string, TokenState> _accessTokens = new();
    private readonly ConcurrentDictionary<string, EmployeeIdentity> _refreshTokens = new();

    public QrSessionView CreateQrSession()
    {
        var session = new QrSession { Id = NewToken(), ExpiresAtUtc = DateTimeOffset.UtcNow + QrTtl };
        _qrSessions[session.Id] = session;
        return new QrSessionView(session.Id, provider.BuildAuthorizeUrl(session.Id), session.ExpiresAtUtc);
    }

    /// <summary>由授权回调（生产为飞书重定向，测试为 mock 端点）确认扫码人身份。</summary>
    public bool AuthorizeQr(string sessionId, EmployeeIdentity identity)
    {
        if (!_qrSessions.TryGetValue(sessionId, out var session) || IsExpired(session) || session.Status != QrStatus.Pending)
            return false;
        session.Identity = identity;
        session.Status = QrStatus.Authorized;
        return true;
    }

    /// <summary>Agent 轮询扫码状态；授权完成后一次性签发 MaxHub 会话。</summary>
    public (QrStatus Status, IssuedSession? Session) PollQr(string sessionId)
    {
        if (!_qrSessions.TryGetValue(sessionId, out var session) || IsExpired(session))
            return (QrStatus.Expired, null);
        if (session.Status != QrStatus.Authorized)
            return (session.Status, null);

        session.Status = QrStatus.Consumed;
        return (QrStatus.Authorized, Issue(session.Identity!));
    }

    public IssuedSession? Refresh(string refreshToken)
    {
        if (!_refreshTokens.TryRemove(refreshToken, out var user))
            return null;
        return Issue(user);
    }

    public bool Revoke(string accessToken) => _accessTokens.TryRemove(accessToken, out _);

    public EmployeeIdentity? Resolve(string? authorizationHeader)
    {
        const string prefix = "Bearer ";
        if (authorizationHeader is null || !authorizationHeader.StartsWith(prefix, StringComparison.Ordinal))
            return null;
        var token = authorizationHeader[prefix.Length..];
        if (!_accessTokens.TryGetValue(token, out var state) || state.ExpiresAtUtc < DateTimeOffset.UtcNow)
            return null;
        return state.User;
    }

    private IssuedSession Issue(EmployeeIdentity user)
    {
        var session = new IssuedSession(NewToken(), NewToken(), user, DateTimeOffset.UtcNow + AccessTtl);
        _accessTokens[session.AccessToken] = new TokenState(user, session.ExpiresAtUtc);
        _refreshTokens[session.RefreshToken] = user;
        return session;
    }

    private static bool IsExpired(QrSession session)
    {
        if (session.ExpiresAtUtc >= DateTimeOffset.UtcNow)
            return false;
        session.Status = QrStatus.Expired;
        return true;
    }

    private static string NewToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}
