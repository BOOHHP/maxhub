namespace MaxHub.Agent.Tests;

/// <summary>反馈入口回归：防止 reintroduce 缺失客户端/Connector 反馈链路。</summary>
public class FeedbackEntryTests
{
    private static string RepoRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MaxHub.sln")))
                directory = directory.Parent;
            return directory?.FullName ?? throw new DirectoryNotFoundException("找不到仓库根目录。");
        }
    }

    private static string Read(params string[] segments) =>
        File.ReadAllText(Path.Combine(new[] { RepoRoot }.Concat(segments).ToArray()));

    [Fact]
    public void Agent_exposes_platform_feedback_page_and_submit()
    {
        var viewModels = Read("src", "MaxHub.Agent.Tray", "ViewModels.cs");
        var window = Read("src", "MaxHub.Agent.Tray", "MainWindow.xaml");

        Assert.Contains("public sealed class FeedbackViewModel", viewModels);
        Assert.Contains("SubmitFeedbackAsync(\"platform\"", viewModels);
        Assert.Contains("x:Name=\"FeedbackPage\"", window);
        Assert.Contains("NavFeedback_Checked", window);
    }

    [Fact]
    public void Agent_local_service_forwards_connector_feedback_with_session()
    {
        var localServer = Read("src", "MaxHub.Agent.Service", "AgentLocalServer.cs");
        var hubClient = Read("src", "MaxHub.Agent.Core", "Remote", "HubClient.cs");

        Assert.Contains("app.MapMethods(\"/max/feedback\", [\"GET\", \"POST\"]", localServer);
        Assert.Contains("SubmitFeedbackAsync(\"tool\"", localServer);
        Assert.Contains("SubmitFeedbackAsync(", hubClient);
    }

    [Fact]
    public void Connector_has_feedback_button_dialog_and_json_post()
    {
        var connector = Read("connector", "maxhub_connector.ms");

        Assert.Contains("button btnFeedback", connector);
        Assert.Contains("rollout maxHubFeedbackRollout", connector);
        Assert.Contains("fn encodeBase64", connector);
        Assert.Contains("messageBase64=", connector);
        Assert.Contains("EscapeDataString", connector);
        Assert.DoesNotContain("fn jsonEscape", connector);
        Assert.Contains("local toolNames = #()", connector);
        Assert.Contains("local installedNames = #()", connector);
        Assert.Contains("local updateNames = #()", connector);
        Assert.DoesNotContain("findString line \" | \"", connector);
        Assert.Contains("local openMaxHubFeedback", connector);
        Assert.DoesNotContain("isValid maxHubFeedbackRollout", connector);
        Assert.Contains("try ( destroyDialog maxHubFeedbackRollout ) catch ()", connector);
        Assert.Contains("/max/feedback?toolId=", connector);
    }
}
