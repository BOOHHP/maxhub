using MaxHub.Core.Packaging;
using MaxHub.Core.Manifests;
using MaxHub.Server.Data;
using MaxHub.Server.Domain;
using MaxHub.Server.Services;
using MaxHub.Server.Storage;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddResponseCompression();

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
builder.Services.AddSingleton<IFeishuMessageSender>(sp =>
    new FeishuMessageClient(new HttpClient { Timeout = TimeSpan.FromSeconds(10) }, feishuOptions));
builder.Services.AddSingleton(new FeedbackRateLimiter(builder.Configuration.GetValue("Feedback:MaxPerHour", 10)));
builder.Services.AddSingleton(sp => new FeedbackService(
    sp.GetRequiredService<IDbContextFactory<MaxHubDb>>(),
    sp.GetRequiredService<RegistryStore>(),
    sp.GetRequiredService<RoleService>(),
    sp.GetRequiredService<IUserDirectory>(),
    sp.GetRequiredService<IFeishuMessageSender>()));

var app = builder.Build();
app.UseResponseCompression();

using (var db = app.Services.GetRequiredService<IDbContextFactory<MaxHubDb>>().CreateDbContext())
{
    db.Database.EnsureCreated();
    // EnsureCreated 不追加列：对旧库做容错加列（列已存在则忽略）
    foreach (var sql in new[]
    {
        "ALTER TABLE Releases ADD COLUMN SignatureBase64 TEXT",
        "ALTER TABLE Connectors ADD COLUMN SignatureBase64 TEXT",
        "ALTER TABLE Users ADD COLUMN Roles TEXT DEFAULT ''",
        "ALTER TABLE Users ADD COLUMN FeishuOpenId TEXT",
        "ALTER TABLE Users ADD COLUMN FeishuUserId TEXT",
        """CREATE TABLE IF NOT EXISTS "Users" ("EmployeeId" TEXT NOT NULL CONSTRAINT "PK_Users" PRIMARY KEY, "Username" TEXT NOT NULL)""",
        """CREATE TABLE IF NOT EXISTS "AgentReleases" ("Id" INTEGER NOT NULL CONSTRAINT "PK_AgentReleases" PRIMARY KEY AUTOINCREMENT, "Version" TEXT NOT NULL, "DownloadUrl" TEXT NOT NULL, "Sha256" TEXT NOT NULL, "UpdatedAtUtc" TEXT NOT NULL)""",
        """CREATE TABLE IF NOT EXISTS "Feedbacks" ("Id" INTEGER NOT NULL CONSTRAINT "PK_Feedbacks" PRIMARY KEY AUTOINCREMENT, "Scope" TEXT NOT NULL, "ToolId" TEXT, "ToolName" TEXT, "FromEmployeeId" TEXT NOT NULL, "FromUsername" TEXT NOT NULL, "ToEmployeeIds" TEXT NOT NULL, "Message" TEXT NOT NULL, "Client" TEXT NOT NULL, "ClientVersion" TEXT, "MaxYear" INTEGER, "DeliveryStatus" TEXT NOT NULL, "DeliveryError" TEXT, "AtUtc" TEXT NOT NULL)""",
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
var feedback = app.Services.GetRequiredService<FeedbackService>();
var feedbackLimiter = app.Services.GetRequiredService<FeedbackRateLimiter>();
var feedbackPlatformRecipients = builder.Configuration.GetSection("Feedback:PlatformRecipients").Get<string[]>() ?? [];

// GitHub Releases 自动同步 Agent 版本（配置 Agent:GitHubRepo 后启用，如 "BOOHHP/maxhub"）
GitHubReleaseService? githubReleases = null;
var githubRepo = builder.Configuration["Agent:GitHubRepo"];
if (!string.IsNullOrWhiteSpace(githubRepo))
{
    // AllowAutoRedirect=false：api.github.com 不可达时靠 releases/latest 的 302 Location 解析版本
    var githubHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(10),
    };
    githubHttp.DefaultRequestHeaders.UserAgent.ParseAdd("MaxHub-Server");
    githubHttp.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    githubReleases = new GitHubReleaseService(githubHttp, githubRepo);
}

EmployeeIdentity? CurrentUser(HttpContext ctx) => auth.Resolve(ctx.Request.Headers.Authorization);
string[] CurrentRoles(HttpContext ctx) =>
    CurrentUser(ctx) is { } u ? roleService.Resolve(u.EmployeeId) : [];
bool IsAdmin(HttpContext ctx) => roleService.IsIn(CurrentRoles(ctx), Roles.Admin);
bool IsReviewer(HttpContext ctx) => roleService.IsIn(CurrentRoles(ctx), Roles.Reviewer);
bool IsPublisher(HttpContext ctx) => roleService.IsIn(CurrentRoles(ctx), Roles.Publisher);

string AgentFileName(string version) => $"MaxHubAgent-{version}-win-x64.exe";
string AgentMirrorUrl(string version) => $"/downloads/agent/{version}/{AgentFileName(version)}";
string AgentSha256(string version, string declaredSha256)
{
    if (!string.IsNullOrWhiteSpace(declaredSha256))
        return declaredSha256;
    var sidecar = Path.Combine(dataDir, "agent", AgentFileName(version) + ".sha256");
    return File.Exists(sidecar) ? File.ReadAllText(sidecar).Trim() : "";
}
IResult AgentReleaseResult(string version, string fallbackDownloadUrl, string sha256) => Results.Ok(new
{
    version,
    downloadUrl = AgentMirrorUrl(version),
    fallbackDownloadUrl,
    sha256 = AgentSha256(version, sha256),
});

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

// ---- Agent 版本与下载入口（公开，用于网页横幅与 Agent 自更新） ----
app.MapGet("/api/v1/agent/latest", async () =>
{
    // 1. GitHub 最新 Release（发版后自动跟随，无需人工登记）
    if (githubReleases is not null && await githubReleases.GetLatestAsync() is { } gh)
        return AgentReleaseResult(gh.Version, gh.DownloadUrl, gh.Sha256);

    // 2. 数据库手动登记（GitHub 不可达时的兜底/覆盖）
    var dbRelease = registry.GetAgentRelease();
    if (dbRelease is not null)
        return AgentReleaseResult(dbRelease.Version, dbRelease.DownloadUrl, dbRelease.Sha256);

    // 3. 配置文件（初始化兜底）
    var latestVersion = builder.Configuration["Agent:LatestVersion"];
    if (string.IsNullOrWhiteSpace(latestVersion))
        return Results.NotFound();
    return AgentReleaseResult(
        latestVersion,
        builder.Configuration["Agent:DownloadUrl"] ?? "",
        builder.Configuration["Agent:Sha256"] ?? "");
});

// 局域网 Agent 镜像：存在则本机直传；缺失时透明重定向到对应 GitHub Release
app.MapMethods("/downloads/agent/{version}/{fileName}", ["GET", "HEAD"], (string version, string fileName) =>
{
    if (!Version.TryParse(version, out _) || fileName != AgentFileName(version))
        return Results.NotFound();

    var mirrorPath = Path.Combine(dataDir, "agent", fileName);
    if (File.Exists(mirrorPath))
        return Results.File(mirrorPath, "application/octet-stream", fileName, enableRangeProcessing: true);

    string? fallback = null;
    var dbRelease = registry.GetAgentRelease();
    if (dbRelease?.Version == version)
        fallback = dbRelease.DownloadUrl;
    if (string.IsNullOrWhiteSpace(fallback) && builder.Configuration["Agent:LatestVersion"] == version)
        fallback = builder.Configuration["Agent:DownloadUrl"];
    if (string.IsNullOrWhiteSpace(fallback) && !string.IsNullOrWhiteSpace(githubRepo))
        fallback = $"https://github.com/{githubRepo}/releases/download/v{version}/{fileName}";

    return string.IsNullOrWhiteSpace(fallback) ? Results.NotFound() : Results.Redirect(fallback);
});

// 后台更新 Agent 版本元数据（仅 admin，DB 存储后立即生效，无需重启）
app.MapPut("/api/v1/admin/agent-release", (HttpContext ctx, SetAgentReleaseRequest request) =>
{
    if (CurrentUser(ctx) is null) return Results.Unauthorized();
    if (!IsAdmin(ctx)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (string.IsNullOrWhiteSpace(request.Version) || string.IsNullOrWhiteSpace(request.DownloadUrl))
        return Results.BadRequest(new { errors = new[] { "版本号与下载地址必填。" } });
    registry.SetAgentRelease(request.Version, request.DownloadUrl, request.Sha256 ?? "");
    return Results.Ok();
});

// ---- 工具索引与详情（市场公开浏览，无需登录） ----
app.MapGet("/api/v1/tools", (int maxVersion) =>
{
    var items = registry.QueryIndex(maxVersion).Select(r => new
    {
        toolId = r.Manifest.Id,
        publicToolId = ToolId.PublicCode(r.Manifest.Id),
        name = r.Manifest.Name,
        description = r.Manifest.Description,
        category = ToolCategoryClassifier.Classify(r.Manifest.Name, r.Manifest.Description, r.Manifest.Id),
        latestVersion = r.Manifest.Version,
        channel = r.Channel,
        minMaxYear = r.Manifest.Compatibility.MinVersion,
        maxMaxYear = r.Manifest.Compatibility.MaxVersion,
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
        publicToolId = ToolId.PublicCode(r.Manifest.Id),
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

// ---- 脚本直传：自动识别 + 打包提交 ----
app.MapPost("/api/v1/scripts/analyze", (HttpContext ctx, AnalyzeScriptRequest request) =>
{
    if (CurrentUser(ctx) is null) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(request.FileName) || string.IsNullOrWhiteSpace(request.Content))
        return Results.BadRequest(new { errors = new[] { "缺少文件名或脚本内容。" } });
    var d = ScriptDescriptor.Analyze(request.FileName, request.Content);
    return Results.Ok(new { name = d.Name, description = d.Description, suggestedId = d.SuggestedId });
});

app.MapPost("/api/v1/scripts/publish", async (HttpContext ctx, PublishScriptRequest request) =>
{
    if (CurrentUser(ctx) is not { } user) return Results.Unauthorized();
    if (!IsPublisher(ctx)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (string.IsNullOrWhiteSpace(request.FileName) || string.IsNullOrWhiteSpace(request.Content) ||
        string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Version))
        return Results.BadRequest(new { errors = new[] { "缺少文件名、脚本内容、名称或版本。" } });

    var zipPath = Path.Combine(Path.GetTempPath(), $"maxhub-script-{Guid.NewGuid():N}.zip");
    try
    {
        ScriptPackage.Pack(new ScriptPublishRequest(
            request.FileName, request.Content, request.Name,
            request.Description ?? "", request.Version,
            request.MinMaxYear, request.MaxMaxYear), zipPath);
        await using var stream = File.OpenRead(zipPath);
        var outcome = registry.SubmitRelease(user, stream);
        return outcome.Success
            ? Results.Ok(new { releaseId = outcome.ReleaseId, status = "pendingReview" })
            : Results.BadRequest(new { errors = outcome.Errors });
    }
    finally
    {
        File.Delete(zipPath);
    }
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

// ---- Web Portal 静态资源 ----
// HTML 不缓存以确保部署后立即更新；CSS/JS 短时缓存，跨页面导航不重复下载。
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        var extension = Path.GetExtension(context.File.Name);
        if (extension is ".css" or ".js")
            context.Context.Response.Headers.CacheControl = "public,max-age=300";
        else if (extension is ".html")
            context.Context.Response.Headers.CacheControl = "no-cache";
    },
});
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
        publicToolId = ToolId.PublicCode(r.Manifest.Id),
        name = r.Manifest.Name,
        description = r.Manifest.Description,
        version = r.Manifest.Version,
        status = r.Status.ToString(),
        channel = r.Channel,
        submittedBy = names.GetValueOrDefault(r.SubmittedBy, r.SubmittedBy),
        reviewedBy = r.ReviewedBy is null ? null : names.GetValueOrDefault(r.ReviewedBy, r.ReviewedBy),
        submittedAtUtc = r.SubmittedAtUtc,
        signed = r.SignatureBase64 != null,
    }));
});

// 规范化编辑：修改展示元数据（名称/描述/频道），不动包文件与签名
app.MapPatch("/api/v1/admin/releases/{releaseId}/metadata", (HttpContext ctx, string releaseId, UpdateReleaseMetadataRequest request) =>
{
    if (CurrentUser(ctx) is null) return Results.Unauthorized();
    if (!IsReviewer(ctx) && !IsAdmin(ctx)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (request.Channel is { Length: > 0 } ch && ch is not ("internal" or "beta" or "stable"))
        return Results.BadRequest(new { errors = new[] { "非法频道。" } });
    return registry.UpdateReleaseMetadata(releaseId, request.Name, request.Description, request.Channel)
        ? Results.Ok()
        : Results.NotFound();
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
    var releasesBySubject = registry.GetAllReleases()
        .GroupBy(r => $"{r.Manifest.Id}@{r.Manifest.Version}")
        .ToDictionary(g => g.Key, g => g.First());
    return Results.Ok(new
    {
        activeUsers,
        subjects = subjects.Select(s =>
        {
            var separator = s.Subject.LastIndexOf('@');
            var toolId = separator > 0 ? s.Subject[..separator] : s.Subject;
            var version = separator > 0 ? s.Subject[(separator + 1)..] : "";
            releasesBySubject.TryGetValue(s.Subject, out var release);
            return new
            {
                subject = s.Subject,
                toolId,
                publicToolId = ToolId.PublicCode(toolId),
                version,
                name = release?.Manifest.Name ?? "未知工具",
                downloads = s.Downloads,
                installs = s.Installs,
            };
        }),
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

// ---- 用户反馈（登录即可提交；身份来自会话，客户端不可冒充） ----
app.MapPost("/api/v1/feedback", async (HttpContext ctx, SubmitFeedbackRequest request) =>
{
    if (CurrentUser(ctx) is not { } user) return Results.Unauthorized();
    var scope = request.Scope == "tool" ? "tool" : request.Scope == "platform" ? "platform" : null;
    if (scope is null)
        return Results.BadRequest(new { errors = new[] { "scope 必须为 tool 或 platform。" } });
    var message = request.Message?.Trim() ?? "";
    if (message.Length is < 5 or > 2000)
        return Results.BadRequest(new { errors = new[] { "反馈内容需 5-2000 字。" } });
    string? toolId = null;
    if (scope == "tool")
    {
        toolId = request.ToolId?.Trim();
        if (string.IsNullOrWhiteSpace(toolId))
            return Results.BadRequest(new { errors = new[] { "工具反馈必须指定 toolId。" } });
    }
    if (!feedbackLimiter.TryRegister($"{user.EmployeeId}:{scope}", DateTimeOffset.UtcNow))
        return Results.StatusCode(429);
    var (recipients, toolName) = feedback.ResolveRecipients(scope, toolId, feedbackPlatformRecipients);
    if (recipients.Length == 0)
        return Results.BadRequest(new { errors = new[] { "暂无可用接收人，请联系管理员。" } });
    var row = feedback.Save(scope, toolId, toolName, user, recipients, message,
        request.Client ?? "unknown", request.ClientVersion, request.MaxYear);
    var (status, error) = await feedback.DeliverAsync(row);
    return Results.Ok(new { feedbackId = row.Id, deliveryStatus = status, deliveryError = error });
});

// ---- 后台：用户反馈列表与补发 ----
app.MapGet("/api/v1/admin/feedbacks", (HttpContext ctx) =>
{
    if (CurrentUser(ctx) is null) return Results.Unauthorized();
    if (!IsReviewer(ctx) && !IsAdmin(ctx)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var users = app.Services.GetRequiredService<IUserDirectory>();
    return Results.Ok(feedback.List().Select(f =>
    {
        var toIds = f.ToEmployeeIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var names = users.GetNames(toIds);
        return new
        {
            id = f.Id,
            scope = f.Scope,
            toolId = f.ToolId,
            publicToolId = f.ToolId is null ? null : ToolId.PublicCode(f.ToolId),
            toolName = f.ToolName,
            fromEmployeeId = f.FromEmployeeId,
            fromUsername = f.FromUsername,
            toUsernames = string.Join("、", toIds.Select(id => names.GetValueOrDefault(id) ?? id)),
            message = f.Message,
            client = f.Client,
            clientVersion = f.ClientVersion,
            maxYear = f.MaxYear,
            deliveryStatus = f.DeliveryStatus,
            deliveryError = f.DeliveryError,
            atUtc = f.AtUtc,
        };
    }));
});

app.MapPost("/api/v1/admin/feedbacks/{id:int}/redeliver", async (HttpContext ctx, int id) =>
{
    if (CurrentUser(ctx) is null) return Results.Unauthorized();
    if (!IsAdmin(ctx)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var row = feedback.Get(id);
    if (row is null) return Results.NotFound();
    var (status, error) = await feedback.DeliverAsync(row);
    return Results.Ok(new { deliveryStatus = status, deliveryError = error });
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
internal sealed record AnalyzeScriptRequest(string FileName, string Content);
internal sealed record PublishScriptRequest(string FileName, string Content, string Name, string? Description, string Version, int MinMaxYear, int MaxMaxYear);
internal sealed record SetAgentReleaseRequest(string Version, string DownloadUrl, string? Sha256);
internal sealed record UpdateReleaseMetadataRequest(string? Name, string? Description, string? Channel);
internal sealed record SubmitFeedbackRequest(string Scope, string? ToolId, string Message, string? Client, string? ClientVersion, int? MaxYear);

public partial class Program;

