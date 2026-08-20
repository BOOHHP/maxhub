namespace MaxHub.Agent.Tests;

/// <summary>Agent 单实例回归：防止 reintroduce 多开。</summary>
public class SingleInstanceTests
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
    public void App_startup_enforces_single_instance_via_named_mutex()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot, "src", "MaxHub.Agent.Tray", "App.xaml.cs"));

        Assert.Contains("Mutex", source);
        Assert.Contains("MaxHubAgent.SingleInstance", source);
        Assert.Contains("createdNew", source);
    }

    [Fact]
    public void Mutex_is_released_on_exit()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot, "src", "MaxHub.Agent.Tray", "App.xaml.cs"));

        Assert.Contains("OnExit", source);
        Assert.Contains("ReleaseMutex", source);
    }
}
