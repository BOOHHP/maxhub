using MaxHub.Server.Domain;
using MaxHub.Server.Services;

var builder = WebApplication.CreateBuilder(args);

var dataDir = builder.Configuration["Storage:DataDir"] ?? Path.Combine(builder.Environment.ContentRootPath, "data");
var enableMockAuth = builder.Configuration.GetValue("Auth:EnableMockProvider", builder.Environment.IsDevelopment());
var publishers = builder.Configuration.GetSection("Roles:Publishers").Get<string[]>() ?? [];
var reviewers = builder.Configuration.GetSection("Roles:Reviewers").Get<string[]>() ?? [];
var admins = builder.Configuration.GetSection("Roles:Admins").Get<string[]>() ?? [];

builder.Services.AddSingleton<IFeishuAuthProvider, MockFeishuAuthProvider>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton(new RegistryStore(dataDir));

var app = builder.Build();

var auth = app.Services.GetRequiredService<AuthService>();
var registry = app.Services.GetRequiredService<RegistryStore>();

EmployeeIdentity? CurrentUser(HttpContext ctx) => auth.Resolve(ctx.Request.Headers.Authorization);
bool IsIn(string[] roleMembers, EmployeeIdentity user) => roleMembers.Contains(user.EmployeeId);

// ---- 认证：飞书扫码会话 ----
app.MapPost("/api/v1/auth/feishu/qr-sessions", () =>
{
    var session = auth.CreateQrSession();
    return Results.Ok(new { sessionId = session.SessionId, authorizeUrl = session.AuthorizeUrl, expiresAtUtc = session.ExpiresAtUtc });
});

if (enableMockAuth)
{
    // 仅测试/开发：模拟员工扫码授权。生产由飞书 OAuth 回调完成同一动作。
    app.MapPost("/api/v1/auth/feishu/qr-sessions/{sessionId}/mock-authorize", (string sessionId, EmployeeIdentity identity) =>
        auth.AuthorizeQr(sessionId, identity) ? Results.Ok() : Results.NotFound());
}

app.MapGet("/api/v1/auth/feishu/qr-sessions/{sessionId}", (string sessionId) =>
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
        },
    });
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

// ---- 工具索引与详情 ----
app.MapGet("/api/v1/tools", (HttpContext ctx, int maxVersion) =>
{
    if (CurrentUser(ctx) is null) return Results.Unauthorized();
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

app.MapGet("/api/v1/tools/{toolId}", (HttpContext ctx, string toolId) =>
{
    if (CurrentUser(ctx) is null) return Results.Unauthorized();
    var releases = registry.GetToolReleases(toolId);
    if (releases.Count == 0) return Results.NotFound();
    return Results.Ok(new
    {
        toolId,
        name = releases[0].Manifest.Name,
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
        targets = manifest.Install.Targets,
        entryPoints = manifest.EntryPoints,
        dependencies = manifest.Dependencies,
    });
});

// ---- 发布与审核 ----
app.MapPost("/api/v1/publish/releases", async (HttpContext ctx) =>
{
    if (CurrentUser(ctx) is not { } user) return Results.Unauthorized();
    if (!IsIn(publishers, user)) return Results.StatusCode(StatusCodes.Status403Forbidden);
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
    if (!IsIn(reviewers, user)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var allowedChannels = new[] { "internal", "beta", "stable" };
    if (request.Approve && !allowedChannels.Contains(request.Channel)) return Results.BadRequest(new { errors = new[] { "非法频道。" } });
    return registry.Review(releaseId, request.Approve, request.Channel ?? "internal", user) ? Results.Ok() : Results.NotFound();
});

// ---- Connector 制品 ----
app.MapPost("/api/v1/admin/connectors", async (HttpContext ctx) =>
{
    if (CurrentUser(ctx) is not { } user) return Results.Unauthorized();
    if (!IsIn(admins, user)) return Results.StatusCode(StatusCodes.Status403Forbidden);
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
    }));
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
internal sealed record ClientEvent(string EventId, string Type, string Subject, string? ClientVersion);

public partial class Program;

