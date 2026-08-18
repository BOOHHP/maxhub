namespace MaxHub.Agent.Core.Detection;

public sealed record MaxInstallation(int Year, string InstallDir, string ExePath);

/// <summary>注册表读取抽象；生产实现读 HKLM\SOFTWARE\Autodesk\3dsMax，测试注入假数据。</summary>
public interface IMaxRegistryReader
{
    /// <summary>返回 (内部版本号如 "21.0", 安装目录)。</summary>
    IEnumerable<(string VersionKey, string InstallDir)> EnumerateInstallations();
}

public sealed class MaxInstallationDetector(IMaxRegistryReader registryReader, Func<string, bool>? fileExists = null)
{
    private readonly Func<string, bool> _fileExists = fileExists ?? File.Exists;

    /// <summary>内部版本号 → 年份。21.0=2019 … 28.0=2026。</summary>
    private static readonly IReadOnlyDictionary<string, int> VersionKeyToYear = new Dictionary<string, int>
    {
        ["21.0"] = 2019, ["22.0"] = 2020, ["23.0"] = 2021, ["24.0"] = 2022,
        ["25.0"] = 2023, ["26.0"] = 2024, ["27.0"] = 2025, ["28.0"] = 2026,
    };

    /// <summary>注册表与可执行文件交叉校验，仅返回受支持年份的可信安装实例。</summary>
    public IReadOnlyList<MaxInstallation> Detect()
    {
        var results = new List<MaxInstallation>();
        foreach (var (versionKey, installDir) in registryReader.EnumerateInstallations())
        {
            if (!VersionKeyToYear.TryGetValue(versionKey, out var year))
                continue; // 不在 2019-2026 支持范围
            if (string.IsNullOrWhiteSpace(installDir))
                continue;
            var exePath = Path.Combine(installDir, "3dsmax.exe");
            if (!_fileExists(exePath))
                continue; // 残留注册表项，无真实安装
            results.Add(new MaxInstallation(year, installDir, exePath));
        }
        return results.OrderBy(r => r.Year).ToList();
    }
}
