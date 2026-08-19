using MaxHub.Server.Data;
using MaxHub.Server.Domain;
using MaxHub.Server.Services;
using MaxHub.Server.Storage;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// 真实飞书凭据放在 git 忽略的本地文件；测试环境不加载，保证用例确定性
if (!builder.Environment.IsEnvironment("Testing"))
    builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true);

var dataDir = builder.Configuration["Storage:DataDir"] ?? Path.Combine(builder.Environment.ContentRootPath, "data");
var enableMockAuth = builder.Configuration.GetValue("Auth:EnableMockProvider", builder.Environment.IsDevelopment());
var publishers = builder.Configuration.GetSection("Roles:Publishers").Get<string[]>() ?? [];
var reviewers = builder.Configuration.GetSection("Roles:Reviewers").Get<string[]>() ?? [];
var admins = builder.Configuration.GetSection("Roles:Admins").Get<string[]>() ?? [];

var feishuOptions = builder.Configuration.GetSection("Feishu").Get<FeishuAuthOptions>() ?? new FeishuAuthOptions();
builder.Services.AddSingleton(feishuOptions);
if (feishuOptions.IsConfigured)
{
    builder.Services.AddSingleton<IFeishuAuthProvider>(new RealFeishuAuthProvider(feishuOptions));
    builder.Services.AddSingleton<IFeishuCodeExchanger>(new FeishuPassportClient(new HttpClient(), feishuOptions));
}
else
{
    builder.Services.AddSingleton<IFeishuAuthProvider, MockFeishuAuthProvider>();
}
builder.Services.AddSingleton<AuthService>();
Directory.CreateDirectory(dataDir);
var dbPath = Path.Combine(dataDir, "maxhub.db");
builder.Services.AddDbContextFactory<MaxHubDb>(o => o.UseSqlite($"Data Source={dbPath}"));
builder.Services.AddSingleton<IRefreshTokenStore, SqliteRefreshTokenStore>();
builder.Services.AddSingleton<IUserDirectory, SqliteUserDirectory>();
builder.Services.AddSingleton(sp => new RoleService(
    sp.GetRequiredService<IUserDirectory>(),
    admins, reviewers, publishers));
builder.Services.AddSingleton(new SigningKeyStore(dataDir));
builder.Services.AddSingleton(sp => new RegistryStore(dataDir, sp.GetRequiredService<IDbContextFactory<MaxHubDb>>(), sp.GetRequiredService<SigningKeyStore>()));

var app = builder.Build();

using (var db = app.Services.GetRequiredService<IDbContextFactory<MaxHubDb>>().CreateDbContext())
{
    db.Database.EnsureCreated();
    // EnsureCreated 不追加列：对旧库做容错加列（列已存在则忽略）
    foreach (var sql in new[]
    {
        "ALTER TABLE Releases ADD COLUMN SignatureBase64 TEXT",
        "ALTER TABLE Connectors ADD COLUMN SignatureBase64 TEXT",
        "ALTER TABLE Users ADD COLUMN Roles TEXT DEFAULT ''",
        """CREATE TABLE IF NOT EXISTS "Users" ("EmployeeId" TEXT NOT NULL CONSTRAINT "PK_Users" PRIMARY KEY, "Username" TEXT NOT NULL)""",
    })
    {
        try { db.Database.ExecuteSqlRaw(sql); }
        catch (Microsoft.Data.Sqlite.SqliteException) { }
    }
}
app.Services.GetRequiredService<RegistryStore>().SignMissingSignatures();

var auth = app.Services.GetRequiredService<AuthService>();
var registry = app.Services.GetRequiredService<RegistryStore>();
var roleService = app.Services.GetRequiredService<RoleService>();

EmployeeIdentity? CurrentUser(HttpContext ctx) => auth.Resolve(ctx.Request.Headers.Authorization);
string[] CurrentRoles(HttpContext ctx) =>
    CurrentUser(ctx) is { } u ? roleService.Resolve(u.EmployeeId) : [];
bool IsAdmin(HttpContext ctx) => roleService.IsIn(CurrentRoles(ctx), Roles.Admin);
bool IsReviewer(HttpContext ctx) => roleService.IsIn(CurrentRoles(ctx), Roles.Reviewer);
bool IsPublisher(HttpContext ctx) => roleService.IsIn(CurrentRoles(ctx), Roles.Publisher);

// ---- 认证：飞书扫码会话 ----
app.MapPost("/api/v1/auth/feishu/qr-sessions", (HttpContext ctx) =>
{
    // client=web 时授权回调指向管理后台页面（需在飞书后台登记 WebRedirectUri）
    var isWeb = ctx.Request.Query["client"] == "web" && feishuOptions.WebRedirectUri.Length > 0;
    var session = auth.CreateQrSession(isWeb ? feishuOptions.WebRedirectUri : null);
    return Results.Ok(new { sessionId = session.SessionId, authorizeUrl = session.AuthorizeUrl, expiresAtUtc = session.ExpiresAtUtc });
});

if (enableMockAuth)
{
    // 仅测试/开发：模拟员工扫码授权。生产由飞书 OAuth 回调完成同一动作。
    app.MapPost("/api/v1/auth/feishu/qr-sessions/{sessionId}/mock-authorize", (string sessionId, EmployeeIdentity identity) =>
        auth.AuthorizeQr(sessionId, identity) ? Results.Ok() : Results.NotFound());
}

app.MapGet("/api/v1/auth/feishu/qr-sessions/{sessionId}", (HttpContext ctx, string sessionId) =>
{
    var (status, session) = auth.PollQr(sessionId);
    return Results.Ok(new
    {
        status = status.ToString().ToLowerInvariant(),
        session = session is null ? null : new
        {
            accessToken = session.AccessToken,
            refreshToken = session.RefreshToken,
            expiresAtUtc = session.ExpiresAtUtc,
            user = new { employeeId = session.User.EmployeeId, username = session.User.Username },
            roles = roleService.Resolve(session.User.EmployeeId),
        },
    });
});

// 本机回调模式：Agent 收到飞书重定向的 code 后回传，由服务端完成换码与身份映射
app.MapPost("/api/v1/auth/feishu/qr-sessions/{sessionId}/complete", async (HttpContext ctx, string sessionId, CompleteQrRequest request) =>
{
    if (ctx.RequestServices.GetService<IFeishuCodeExchanger>() is not { } exchanger)
        return Results.NotFound();
    if (!string.Equals(request.State, sessionId, StringComparison.Ordinal))
        return Results.BadRequest(new { errors = new[] { "state 与登录会话不匹配，已终止流程。" } });

    EmployeeIdentity identity;
    try
    {
        var redirectUri = request.Client == "web" && feishuOptions.WebRedirectUri.Length > 0 ? feishuOptions.WebRedirectUri : null;
        identity = await exchanger.ExchangeAsync(request.Code, redirectUri, ctx.RequestAborted);
    }
    catch (FeishuAuthException ex)
    {
        return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "飞书授权码交换失败", detail: ex.Message);
    }
    return auth.AuthorizeQr(sessionId, identity) ? Results.Ok() : Results.NotFound();
});

app.MapPost("/api/v1/auth/sessions/refresh", (RefreshRequest request) =>
{
    var session = auth.Refresh(request.RefreshToken);
    return session is null
        ? Results.Unauthorized()
        : Results.Ok(new { accessToken = session.AccessToken, refreshToken = session.RefreshToken, expiresAtUtc = session.ExpiresAtUtc });
});

app.MapDelete("/api/v1/auth/sessions/current", (HttpContext ctx) =>
{
    var header = ctx.Request.Headers.Authorization.ToString();
    return header.StartsWith("Bearer ") && auth.Revoke(header["Bearer ".Length..]) ? Results.NoContent() : Results.Unauthorized();
});

// ---- 工具索引与详情（市场公开浏览，无需登录） ----
app.MapGet("/api/v1/tools", (int maxVersion) =>
{
    var items = registry.QueryIndex(maxVersion).Select(r => new
    {
        toolId = r.Manifest.Id,
        name = r.Manifest.Name,
        description = r.Manifest.Description,
        latestVersion = r.Manifest.Version,
        channel = r.Channel,
        compatibility = r.Manifest.Compatibility,
    });
    return Results.Ok(items);
});

app.MapGet("/api/v1/tools/{toolId}", (string toolId) =>
{
    var releases = registry.GetToolReleases(toolId);
    if (releases.Count == 0) return Results.NotFound();
    return Results.Ok(new
    {
        toolId,
        name = releases[0].Manifest.Name,
        description = releases[0].Manifest.Description,
        releases = releases.Select(r => new
        {
            version = r.Manifest.Version,
            channel = r.Channel,
            sha256 = r.Sha256,
            sizeBytes = r.SizeBytes,
            compatibility = r.Manifest.Compatibility,
            restartRequired = r.Manifest.Install.RestartRequired,
        }),
    });
});

// ---- 当前用户与角色（供前端导航角色感知） ----
app.MapGet("/api/v1/auth/me", (HttpContext ctx) =>
{
    if (CurrentUser(ctx) is not { } user) return Results.Unauthorized();
    return Results.Ok(new
    {
        employeeId = user.EmployeeId,
        username = user.Username,
        roles = roleService.Resolve(user.EmployeeId),
    });
});

// ---- 我的提交（publish 页跟踪审核状态） ----
app.MapGet("/api/v1/my-tools", (HttpContext ctx) =>
{
    if (CurrentUser(ctx) is not { } user) return Results.Unauthorized();
    var releases = registry.GetAllReleases().Where(r => r.SubmittedBy == user.EmployeeId);
    var users = app.Services.GetRequiredService<IUserDirectory>();
    var names = users.GetNames(releases.Select(r => r.ReviewedBy ?? ""));
    return Results.Ok(releases.Select(r => new
    {
        releaseId = r.ReleaseId,
        toolId = r.Manifest.Id,
        name = r.Manifest.Name,
        version = r.Manifest.Version,
        status = r.Status.ToString(),
        channel = r.Channel,
        reviewedBy = r.ReviewedBy is null ? null : names.GetValueOrDefault(r.ReviewedBy, r.ReviewedBy),
        submittedAtUtc = r.SubmittedAtUtc,
    }));
});

app.MapGet("/api/v1/tools/{toolId}/releases/{version}/install-plan", (HttpContext ctx, string toolId, string version) =>
{
    if (CurrentUser(ctx) is null) return Results.Unauthorized();
    var release = registry.GetPublished(toolId, version);
    if (release is null) return Results.NotFound();
    var manifest = release.Manifest;
    return Results.Ok(new
    {
        toolId,
        version,
        sha256 = release.Sha256,
        sizeBytes = release.SizeBytes,
        restartRequired = manifest.Install.RestartRequired,
        riskLevel = manifest.Install.Targets.Any(t => t.Destination == "userStartup") ? "medium" : "low",
        signature = release.SignatureBase64,
        targets = manifest.Install.Targets,
        entryPoints = manifest.EntryPoints,
        dependencies = manifest.Dependencies,
    });
});

// ---- 发布与审核 ----
app.MapPost("/api/v1/publish/releases", async (HttpContext ctx) =>
{
    if (CurrentUser(ctx) is not { } user) return Results.Unauthorized();
    if (!IsPublisher(ctx)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var form = await ctx.Request.ReadFormAsync();
    if (form.Files["package"] is not { } file) return Results.BadRequest(new { errors = new[] { "缺少 package 文件。" } });

    await using var stream = file.OpenReadStream();
    var outcome = registry.SubmitRelease(user, stream);
    return outcome.Success
        ? Results.Ok(new { releaseId = outcome.ReleaseId, status = "pendingReview" })
        : Results.BadRequest(new { errors = outcome.Errors });
});

app.MapPost("/api/v1/releases/{releaseId}/review", (HttpContext ctx, string releaseId, ReviewRequest request) =>
{
    if (CurrentUser(ctx) is not { } user) return Results.Unauthorized();
    if (!IsReviewer(ctx)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var allowedChannels = new[] { "internal", "beta", "stable" };
    if (request.Approve && !allowedChannels.Contains(request.Channel)) return Results.BadRequest(new { errors = new[] { "非法频道。" } });
    return registry.Review(releaseId, request.Approve, request.Channel ?? "internal", user) ? Results.Ok() : Results.NotFound();
});

// ---- Connector 制品 ----
app.MapPost("/api/v1/admin/connectors", async (HttpContext ctx) =>
{
    if (CurrentUser(ctx) is not { } user) return Results.Unauthorized();
    if (!IsAdmin(ctx)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var form = await ctx.Request.ReadFormAsync();
    if (form.Files["package"] is not { } file ||
        !int.TryParse(form["minMaxYear"], out var minYear) ||
        !int.TryParse(form["maxMaxYear"], out var maxYear) ||
        string.IsNullOrWhiteSpace(form["version"]))
        return Results.BadRequest(new { errors = new[] { "需要 package、version、minMaxYear、maxMaxYear。" } });

    var artifactPath = Path.Combine(dataDir, "connectors", $"{form["version"]}_{minYear}-{maxYear}.zip");
    Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
    await using (var target = File.Create(artifactPath))
        await file.CopyToAsync(target);

    registry.RegisterConnector(new ConnectorRelease
    {
        Version = form["version"]!,
        MinMaxYear = minYear,
        MaxMaxYear = maxYear,
        ArtifactPath = artifactPath,
        Sha256 = MaxHub.Core.Packaging.ToolPackage.ComputeSha256(artifactPath),
        SizeBytes = new FileInfo(artifactPath).Length,
    });
    return Results.Ok();
});

app.MapGet("/api/v1/connectors", (HttpContext ctx, int maxVersion) =>
{
    if (CurrentUser(ctx) is null) return Results.Unauthorized();
    return Results.Ok(registry.QueryConnectors(maxVersion).Select(c => new
    {
        version = c.Version,
        minMaxYear = c.MinMaxYear,
        maxMaxYear = c.MaxMaxYear,
        sha256 = c.Sha256,
        sizeBytes = c.SizeBytes,
        signature = c.SignatureBase64,
    }));
});

// 签名公钥（SPKI Base64）：Agent 首次获取后固定（TOFU）
app.MapGet("/api/v1/signing/public-key", () =>
{
    var signer = app.Services.GetRequiredService<SigningKeyStore>();
    return Results.Ok(new { publicKey = signer.PublicKeyBase64, algorithm = "ECDSA_P256_SHA256" });
});

// ---- 管理后台 ----
app.UseStaticFiles();
app.MapGet("/admin", () => Results.Redirect("/admin.html"));

app.MapGet("/api/v1/admin/releases", (HttpContext ctx) =>
{
    if (CurrentUser(ctx) is not { } user) return Results.Unauthorized();
    if (!IsReviewer(ctx) && !IsAdmin(ctx)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var releases = registry.GetAllReleases();
    var users = app.Services.GetRequiredService<IUserDirectory>();
    var names = users.GetNames(releases.SelectMany(r => new[] { r.SubmittedBy, r.ReviewedBy ?? "" }));
    return Results.Ok(releases.Select(r => new
    {
        releaseId = r.ReleaseId,
        toolId = r.Manifest.Id,
        name = r.Manifest.Name,
        version = r.Manifest.Version,
        status = r.Status.ToString(),
        channel = r.Channel,
        submittedBy = names.GetValueOrDefault(r.SubmittedBy, r.SubmittedBy),
        reviewedBy = r.ReviewedBy is null ? null : names.GetValueOrDefault(r.ReviewedBy, r.ReviewedBy),
        submittedAtUtc = r.SubmittedAtUtc,
        signed = r.SignatureBase64 != null,
    }));
});

// 紧急撤回：下架已发布版本
app.MapPost("/api/v1/releases/{releaseId}/withdraw", (HttpContext ctx, string releaseId) =>
{
    if (CurrentUser(ctx) is not { } user) return Results.Unauthorized();
    if (!IsReviewer(ctx) && !IsAdmin(ctx)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    return registry.Withdraw(releaseId, user) ? Results.Ok() : Results.NotFound();
});

app.MapGet("/api/v1/admin/connectors", (HttpContext ctx) =>
{
    if (CurrentUser(ctx) is not { } user) return Results.Unauthorized();
    if (!IsAdmin(ctx)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    return Results.Ok(registry.GetAllConnectors().Select(c => new
    {
        version = c.Version,
        minMaxYear = c.MinMaxYear,
        maxMaxYear = c.MaxMaxYear,
        sizeBytes = c.SizeBytes,
        signed = c.SignatureBase64 != null,
    }));
});

app.MapGet("/api/v1/admin/stats", (HttpContext ctx) =>
{
    if (CurrentUser(ctx) is not { } user) return Results.Unauthorized();
    if (!IsReviewer(ctx) && !IsAdmin(ctx)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var (subjects, activeUsers) = registry.GetStats();
    return Results.Ok(new
    {
        activeUsers,
        subjects = subjects.Select(s => new { subject = s.Subject, downloads = s.Downloads, installs = s.Installs }),
    });
});

// ---- 成员角色管理（仅 admin） ----
app.MapGet("/api/v1/admin/users", (HttpContext ctx) =>
{
    if (CurrentUser(ctx) is null) return Results.Unauthorized();
    if (!IsAdmin(ctx)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var users = app.Services.GetRequiredService<IUserDirectory>();
    return Results.Ok(users.GetAllUsers().Select(u => new
    {
        employeeId = u.EmployeeId,
        username = u.Username,
        roles = string.IsNullOrWhiteSpace(u.Roles) ? new[] { Roles.Publisher } : u.Roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
    }));
});

app.MapPut("/api/v1/admin/users/{employeeId}/roles", (HttpContext ctx, string employeeId, SetRolesRequest request) =>
{
    if (CurrentUser(ctx) is null) return Results.Unauthorized();
    if (!IsAdmin(ctx)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var allowed = new[] { Roles.Admin, Roles.Reviewer, Roles.Publisher };
    if (request.Roles.Any(r => !allowed.Contains(r)))
        return Results.BadRequest(new { errors = new[] { "非法角色。" } });
    var users = app.Services.GetRequiredService<IUserDirectory>();
    users.SetRoles(employeeId, request.Roles);
    return Results.Ok();
});

// ---- 下载（服务端按认证主体记账） ----
app.MapGet("/downloads/{toolId}/{version}/package.zip", (HttpContext ctx, string toolId, string version) =>
{
    if (CurrentUser(ctx) is not { } user) return Results.Unauthorized();
    var release = registry.GetPublished(toolId, version);
    if (release is null) return Results.NotFound();
    registry.AddActivityEvent(new ActivityEvent(Guid.NewGuid().ToString("N"), user.EmployeeId, "download", $"{toolId}@{version}", null, DateTimeOffset.UtcNow));
    return Results.File(release.ArtifactPath, "application/zip");
});

app.MapGet("/downloads/connectors/{maxVersion:int}/{connectorVersion}/package.zip", (HttpContext ctx, int maxVersion, string connectorVersion) =>
{
    if (CurrentUser(ctx) is not { } user) return Results.Unauthorized();
    var connector = registry.GetConnector(maxVersion, connectorVersion);
    if (connector is null) return Results.NotFound();
    registry.AddActivityEvent(new ActivityEvent(Guid.NewGuid().ToString("N"), user.EmployeeId, "download-connector", $"connector@{connectorVersion}/max{maxVersion}", null, DateTimeOffset.UtcNow));
    return Results.File(connector.ArtifactPath, "application/zip");
});

// ---- 统计与安装事件（用户身份一律取自会话，客户端提交的用户字段不被接受） ----
app.MapPost("/api/v1/activity/events", (HttpContext ctx, ClientEvent clientEvent) =>
{
    if (CurrentUser(ctx) is not { } user) return Results.Unauthorized();
    var added = registry.AddActivityEvent(new ActivityEvent(clientEvent.EventId, user.EmployeeId, clientEvent.Type, clientEvent.Subject, clientEvent.ClientVersion, DateTimeOffset.UtcNow));
    return Results.Ok(new { accepted = added, duplicate = !added });
});

app.MapPost("/api/v1/installations/events", (HttpContext ctx, ClientEvent clientEvent) =>
{
    if (CurrentUser(ctx) is not { } user) return Results.Unauthorized();
    registry.AddInstallEvent(new ActivityEvent(clientEvent.EventId, user.EmployeeId, clientEvent.Type, clientEvent.Subject, clientEvent.ClientVersion, DateTimeOffset.UtcNow));
    return Results.Ok();
});

app.Run();

internal sealed record RefreshRequest(string RefreshToken);
internal sealed record ReviewRequest(bool Approve, string? Channel);
internal sealed record CompleteQrRequest(string Code, string State, string? Client = null);
internal sealed record ClientEvent(string EventId, string Type, string Subject, string? ClientVersion);
internal sealed record SetRolesRequest(string[] Roles);

public partial class Program;

