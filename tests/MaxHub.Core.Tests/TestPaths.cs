namespace MaxHub.Core.Tests;

public static class TestPaths
{
    public static string RepoRoot { get; } = FindRepoRoot();

    public static string SamplesDir => Path.Combine(RepoRoot, "samples", "tools");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MaxHub.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("未找到仓库根目录（MaxHub.sln）。");
    }
}
