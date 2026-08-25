using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MaxHub.Server.Domain;
using MaxHub.Server.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace MaxHub.Server.Tests;

/// <summary>记录投递目标与文本的假飞书发送器，用于验证接收人解析与送达状态。</summary>
public sealed class RecordingFeishuSender : IFeishuMessageSender
{
    public List<(string EmployeeId, string? OpenId, string? UserId, string Text)> Sent { get; } = [];

    public Task SendTextAsync(EmployeeIdentity target, string text, CancellationToken cancellationToken = default)
    {
        lock (Sent) Sent.Add((target.EmployeeId, target.OpenId, target.UserId, text));
        return Task.CompletedTask;
    }
}

public sealed class FeedbackFixture : WebApplicationFactory<Program>
{
    public string DataDir { get; } = Directory.CreateTempSubdirectory("maxhub-feedback-test").FullName;
    public RecordingFeishuSender Sender { get; } = new();

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:DataDir"] = DataDir,
            ["Auth:EnableMockProvider"] = "true",
            ["Roles:Publishers:0"] = "emp-pub",
            ["Roles:Reviewers:0"] = "emp-rev",
            ["Roles:Admins:0"] = "emp-admin",
            ["Feedback:MaxPerHour"] = "3",
        }));
        builder.ConfigureServices(services =>
            services.Replace(ServiceDescriptor.Singleton<IFeishuMessageSender>(Sender)));
        return base.CreateHost(builder);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(DataDir))
            Directory.Delete(DataDir, recursive: true);
    }
}

public class FeedbackTests(FeedbackFixture fixture) : IClassFixture<FeedbackFixture>
{
    private async Task<HttpClient> LoginAsync(string employeeId, string username)
    {
        var client = fixture.CreateClient();
        var created = await client.PostAsync("/api/v1/auth/feishu/qr-sessions", null);
        var sessionId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessionId").GetString()!;
        await client.PostAsJsonAsync($"/api/v1/auth/feishu/qr-sessions/{sessionId}/mock-authorize",
            new { employeeId, username });
        var polled = await client.GetFromJsonAsync<JsonElement>($"/api/v1/auth/feishu/qr-sessions/{sessionId}");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", polled.GetProperty("session").GetProperty("accessToken").GetString()!);
        return client;
    }

    [Fact]
    public async Task Admin_page_keeps_feedback_helpers_inside_script_block()
    {
        // 回归：esc 转义函数曾被误放到 <script> 外，导致后台页渲染出 JS 文本且反馈列表报错空白
        var html = await fixture.CreateClient().GetStringAsync("/admin.html");
        var scriptStart = html.LastIndexOf("<script>", StringComparison.Ordinal);
        var escDef = html.IndexOf("function esc(s)", StringComparison.Ordinal);
        var mainEnd = html.IndexOf("</main>", StringComparison.Ordinal);

        Assert.True(scriptStart > 0);
        Assert.True(escDef > scriptStart);
        Assert.True(escDef > mainEnd);
        Assert.DoesNotContain("</main>\n\n\n// 反馈内容来自用户输入", html);
    }

    [Fact]
    public async Task Anonymous_feedback_is_rejected()
    {
        var client = fixture.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/feedback",
            new { scope = "platform", message = "匿名反馈不应通过" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Validation_rejects_bad_payload()
    {
        var user = await LoginAsync("emp-viewer", "王五");
        var tooShort = await user.PostAsJsonAsync("/api/v1/feedback", new { scope = "platform", message = "短" });
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);

        var badScope = await user.PostAsJsonAsync("/api/v1/feedback", new { scope = "other", message = "非法范围测试内容" });
        Assert.Equal(HttpStatusCode.BadRequest, badScope.StatusCode);

        var toolWithoutId = await user.PostAsJsonAsync("/api/v1/feedback", new { scope = "tool", message = "工具反馈必须带工具编号" });
        Assert.Equal(HttpStatusCode.BadRequest, toolWithoutId.StatusCode);
    }

    private static string RepoRoot { get; } = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MaxHub.sln")))
            dir = dir.Parent!;
        return dir!.FullName;
    }

    private static MultipartFormDataContent PackageContent(string sampleName)
    {
        var sampleDir = Path.Combine(RepoRoot, "samples", "tools", sampleName);
        var zipPath = Path.Combine(Path.GetTempPath(), $"maxhub-fb-{sampleName}-{Guid.NewGuid():N}.zip");
        MaxHub.Core.Packaging.ToolPackage.Pack(sampleDir, zipPath);
        var bytes = File.ReadAllBytes(zipPath);
        File.Delete(zipPath);
        return new MultipartFormDataContent { { new ByteArrayContent(bytes), "package", "package.zip" } };
    }

    [Fact]
    public async Task Tool_feedback_goes_to_publisher_and_admins()
    {
        // 在同一服务器实例内完成发布与审核，保证反馈解析到该工具的上传者
        var publisher = await LoginAsync("emp-pub", "张三");
        var upload = await publisher.PostAsync("/api/v1/publish/releases", PackageContent("scene-batch-renamer"));
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var releaseId = (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("releaseId").GetString()!;
        var reviewer = await LoginAsync("emp-rev", "李四");
        await reviewer.PostAsJsonAsync($"/api/v1/releases/{releaseId}/review", new { approve = true, channel = "stable" });

        int before;
        lock (fixture.Sender.Sent) before = fixture.Sender.Sent.Count;

        var viewer = await LoginAsync("emp-viewer", "王五");
        var response = await viewer.PostAsJsonAsync("/api/v1/feedback", new
        {
            scope = "tool",
            toolId = "com.company.scene-batch-renamer",
            message = "批量重命名很好用，希望支持正则。",
            client = "connector",
            clientVersion = "1.5.7",
            maxYear = 2025,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("delivered", body.GetProperty("deliveryStatus").GetString());

        lock (fixture.Sender.Sent)
        {
            var delta = fixture.Sender.Sent.Skip(before).ToList();
            var recipients = delta.Select(s => s.EmployeeId).OrderBy(x => x).ToArray();
            Assert.Contains("emp-pub", recipients); // 上传者
            Assert.Contains("emp-admin", recipients); // 管理员抄送
            Assert.DoesNotContain("emp-viewer", recipients); // 反馈人不收自己
            var text = delta.First(s => s.EmployeeId == "emp-pub").Text;
            Assert.Contains("王五", text);
            Assert.Contains("批量重命名很好用", text);
            Assert.Contains("Max 2025", text);
        }
    }

    [Fact]
    public async Task Platform_feedback_goes_to_admins_only()
    {
        int before;
        lock (fixture.Sender.Sent) before = fixture.Sender.Sent.Count;

        var user = await LoginAsync("emp-viewer", "王五");
        var response = await user.PostAsJsonAsync("/api/v1/feedback", new
        {
            scope = "platform",
            message = "希望增加深色主题切换。",
            client = "agent",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        lock (fixture.Sender.Sent)
        {
            var recipients = fixture.Sender.Sent.Skip(before).Select(s => s.EmployeeId).Distinct().ToArray();
            Assert.Equal(["emp-admin"], recipients);
        }
    }

    [Fact]
    public async Task Rate_limit_blocks_excessive_feedback()
    {
        var user = await LoginAsync("emp-flood", "刷屏");
        for (var i = 0; i < 3; i++)
        {
            var ok = await user.PostAsJsonAsync("/api/v1/feedback", new { scope = "platform", message = $"限流测试第 {i} 条内容" });
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }
        var blocked = await user.PostAsJsonAsync("/api/v1/feedback", new { scope = "platform", message = "限流测试第四条内容" });
        Assert.Equal((HttpStatusCode)429, blocked.StatusCode);
    }

    [Fact]
    public async Task Admin_can_list_and_redeliver_feedback()
    {
        var user = await LoginAsync("emp-list", "列表");
        await user.PostAsJsonAsync("/api/v1/feedback", new { scope = "platform", message = "后台列表与补发验证内容。" });

        var viewer = await LoginAsync("emp-viewer", "王五");
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v1/admin/feedbacks")).StatusCode);

        var admin = await LoginAsync("emp-admin", "管理员");
        var list = await admin.GetFromJsonAsync<JsonElement[]>("/api/v1/admin/feedbacks");
        Assert.NotEmpty(list);
        var first = list.First();
        var id = first.GetProperty("id").GetInt32();
        var redeliver = await admin.PostAsync($"/api/v1/admin/feedbacks/{id}/redeliver", null);
        Assert.Equal(HttpStatusCode.OK, redeliver.StatusCode);
        var body = await redeliver.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("delivered", body.GetProperty("deliveryStatus").GetString());
    }

    [Fact]
    public async Task Script_submission_notifies_admins_and_reviewers_via_feishu()
    {
        var publisher = await LoginAsync("emp-pub", "发布者");
        var res = await publisher.PostAsJsonAsync("/api/v1/scripts/publish", new
        {
            fileName = "notify-tool.ms",
            content = "macroScript NotifyTool category:\"MaxHub\" buttonText:\"N\"\n(\n)\n",
            name = "审核通知测试工具",
            description = "用于验证提交后飞书通知",
            version = "1.0.0",
            minMaxYear = 2019,
            maxMaxYear = 2026,
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        // 通知为后台异步发送，轮询等待
        var sw = System.Diagnostics.Stopwatch.StartNew();
        List<(string EmployeeId, string? OpenId, string? UserId, string Text)> sent;
        do
        {
            await Task.Delay(200);
            lock (fixture.Sender.Sent) sent = [.. fixture.Sender.Sent];
        } while (sw.Elapsed.TotalSeconds < 5 && !sent.Any(s => s.Text.Contains("待审核")));

        lock (fixture.Sender.Sent)
        {
            var notify = fixture.Sender.Sent.Where(s => s.Text.Contains("待审核")).ToList();
            var recipients = notify.Select(s => s.EmployeeId).Distinct().OrderBy(x => x).ToArray();
            Assert.Equal(["emp-admin", "emp-rev"], recipients); // 管理员+审核者，排除提交者
            Assert.Contains("审核通知测试工具", notify.First().Text);
        }
    }
}
