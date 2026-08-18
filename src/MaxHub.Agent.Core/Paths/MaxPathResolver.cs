namespace MaxHub.Agent.Core.Paths;

public interface IMaxPathResolver
{
    /// <summary>把逻辑安装目标解析为指定 Max 年份的绝对目录。未知目标抛出异常。</summary>
    string Resolve(int maxYear, string destination);
}

/// <summary>与 protocol/README.md 的目录映射保持一致。userRoot 可注入用于测试。</summary>
public sealed class DefaultMaxPathResolver(string? userRoot = null) : IMaxPathResolver
{
    private readonly string _userRoot = userRoot
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Autodesk", "3dsMax");

    public string Resolve(int maxYear, string destination)
    {
        var enuDir = Path.Combine(_userRoot, $"{maxYear} - 64bit", "ENU");
        return destination switch
        {
            "userScripts" => Path.Combine(enuDir, "scripts"),
            "userMacros" => Path.Combine(enuDir, "usermacros"),
            "userStartup" => Path.Combine(enuDir, "scripts", "startup"),
            _ => throw new ArgumentException($"MVP 不支持的安装目标 \"{destination}\"。"),
        };
    }
}
