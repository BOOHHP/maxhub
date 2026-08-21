namespace MaxHub.Agent.Tests;

/// <summary>Connector 脚本安全回归：防止 reintroduce 运行时不支持的控件属性赋值。</summary>
public class ConnectorScriptSafetyTests
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
    public void Case_statements_keep_open_paren_on_the_of_line()
    {
        // MaxScript 要求 case ... of 的左括号与 of 同行；换行会导致脚本加载即编译失败（1.5.7 踩过）
        var lines = File.ReadAllLines(Path.Combine(RepoRoot, "connector", "maxhub_connector.ms"));
        var offenders = lines
            .Select((line, index) => (line, index))
            .Where(x => System.Text.RegularExpressions.Regex.IsMatch(x.line.TrimEnd(), @"case\s+.+\bof\s*$"))
            .ToList();
        Assert.True(offenders.Count == 0,
            "存在 of 行尾缺少左括号的 case 语句：" + string.Join("; ", offenders.Select(x => $"第 {x.index + 1} 行")));
    }

    [Fact]
    public void ProgressBar_runtime_width_is_guarded_for_unsupported_max_versions()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot, "connector", "maxhub_connector.ms"));

        // 部分 Max 版本的 ProgressBar 运行时不支持 width 属性，必须受保护，
        // 否则“关闭后再打开”会在 layoutControls 中抛出未处理异常。
        Assert.Contains("try ( pbInstall.width = contentW ) catch ()", source);
        Assert.DoesNotContain("pbInstall.pos = [margin, progressY]; pbInstall.width = contentW", source);
    }
}
