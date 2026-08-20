namespace MaxHub.Agent.Tests;

/// <summary>Agent 单实例回归：防止 reintroduce 多开或静默退出的重复启动体验。</summary>
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

    private static string AppSource =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "MaxHub.Agent.Tray", "App.xaml.cs"));

    private static string MainWindowSource =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "MaxHub.Agent.Tray", "MainWindow.xaml.cs"));

    [Fact]
    public void App_startup_enforces_single_instance_via_named_mutex()
    {
        var source = AppSource;

        Assert.Contains("new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew)", source);
        Assert.Contains("Local\\MaxHubAgent.SingleInstance", source);
        Assert.Contains("if (!createdNew)", source);
        Assert.Contains("Shutdown();", source);
    }

    [Fact]
    public void Second_instance_brings_existing_window_to_front_before_exit()
    {
        Assert.Contains("BringExistingInstanceToFront();", AppSource);
        Assert.Contains("MaxHubAgent.ShowMain", AppSource);
        Assert.Contains("MaxHubAgent.ShowMain", MainWindowSource);
        Assert.Contains("ShowFromTray", MainWindowSource);
    }

    [Fact]
    public void Mutex_is_released_on_exit()
    {
        var source = AppSource;

        Assert.Contains("protected override void OnExit(ExitEventArgs e)", source);
        Assert.Contains("ReleaseMutex();", source);
    }
}
