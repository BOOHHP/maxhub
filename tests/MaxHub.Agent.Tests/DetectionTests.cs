using MaxHub.Agent.Core.Detection;
using MaxHub.Agent.Core.Paths;

namespace MaxHub.Agent.Tests;

public class DetectionTests
{
    private sealed class FakeRegistry(params (string Key, string Dir)[] entries) : IMaxRegistryReader
    {
        public IEnumerable<(string VersionKey, string InstallDir)> EnumerateInstallations() => entries;
    }

    [Fact]
    public void Detects_supported_years_and_filters_stale_entries()
    {
        var root = Directory.CreateTempSubdirectory("maxhub-detect").FullName;
        try
        {
            var max2019 = Path.Combine(root, "Max2019");
            var max2026 = Path.Combine(root, "Max2026");
            var stale2022 = Path.Combine(root, "Max2022-gone");
            Directory.CreateDirectory(max2019);
            Directory.CreateDirectory(max2026);
            File.WriteAllText(Path.Combine(max2019, "3dsmax.exe"), "");
            File.WriteAllText(Path.Combine(max2026, "3dsmax.exe"), "");

            var detector = new MaxInstallationDetector(new FakeRegistry(
                ("21.0", max2019),      // 2019，有效
                ("28.0", max2026),      // 2026，有效
                ("24.0", stale2022),    // 注册表残留，exe 不存在
                ("20.0", max2019),      // 2018，超出支持范围
                ("garbage", max2019))); // 非法键

            var installations = detector.Detect();
            Assert.Equal([2019, 2026], installations.Select(i => i.Year));
            Assert.All(installations, i => Assert.EndsWith("3dsmax.exe", i.ExePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

public class PathResolverTests
{
    [Fact]
    public void Resolves_mvp_destinations_per_protocol()
    {
        var resolver = new DefaultMaxPathResolver(@"C:\Users\u\AppData\Local\Autodesk\3dsMax");
        Assert.Equal(@"C:\Users\u\AppData\Local\Autodesk\3dsMax\2024 - 64bit\ENU\scripts", resolver.Resolve(2024, "userScripts"));
        Assert.Equal(@"C:\Users\u\AppData\Local\Autodesk\3dsMax\2024 - 64bit\ENU\usermacros", resolver.Resolve(2024, "userMacros"));
        Assert.Equal(@"C:\Users\u\AppData\Local\Autodesk\3dsMax\2019 - 64bit\ENU\scripts\startup", resolver.Resolve(2019, "userStartup"));
    }

    [Theory]
    [InlineData("userPlugins")]
    [InlineData("sharedScripts")]
    [InlineData("anything")]
    public void Unknown_destination_throws(string destination) =>
        Assert.Throws<ArgumentException>(() => new DefaultMaxPathResolver(@"C:\x").Resolve(2024, destination));
}
