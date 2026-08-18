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
        var localeDir = ResolveActiveLocaleDir(maxYear);
        return destination switch
        {
            "userScripts" => Path.Combine(localeDir, "scripts"),
            "userMacros" => Path.Combine(localeDir, "usermacros"),
            "userStartup" => Path.Combine(localeDir, "scripts", "startup"),
            _ => throw new ArgumentException($"MVP 不支持的安装目标 \"{destination}\"。"),
        };
    }

    /// <summary>
    /// 本地化 Max（CHS/FRA/…）的用户目录不是 ENU。
    /// 活动语言目录 = 最近被 Max 写过 3dsMax.ini 的语言文件夹；探测不到时回退 ENU。
    /// </summary>
    private string ResolveActiveLocaleDir(int maxYear)
    {
        var yearDir = Path.Combine(_userRoot, $"{maxYear} - 64bit");
        if (Directory.Exists(yearDir))
        {
            var active = Directory.EnumerateDirectories(yearDir)
                .Where(dir => File.Exists(Path.Combine(dir, "3dsMax.ini")))
                .OrderByDescending(dir => File.GetLastWriteTimeUtc(Path.Combine(dir, "3dsMax.ini")))
                .FirstOrDefault();
            if (active is not null)
                return active;
        }
        return Path.Combine(yearDir, "ENU");
    }
}
