namespace MaxHub.Agent.Tests;

/// <summary>启动容错回归：服务器不可达时 Agent 不得崩溃。</summary>
public class StartupResilienceTests
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

    [Fact]
    public void Session_restore_survives_network_errors_without_crashing()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot, "src", "MaxHub.Agent.Tray", "ViewModels.cs"));

        // 1.0.21 曾只捕获 InvalidOperationException，服务器不可达时 HttpRequestException 导致启动闪退
        Assert.Contains("public bool TryRestoreSession()", source);
        Assert.Contains("catch (InvalidOperationException)", source);
        Assert.Contains("catch (Exception)", source);
        Assert.Contains("服务器不可达等网络异常：保留凭据、以未登录态进入，绝不崩溃", source);
    }
}
