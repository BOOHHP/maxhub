using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MaxHub.Agent.Tests;

public class ReleaseNotesResourceTests
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
    public void Current_agent_version_has_unique_Chinese_release_notes()
    {
        var projectPath = Path.Combine(RepoRoot, "src", "MaxHub.Agent.Tray", "MaxHub.Agent.Tray.csproj");
        var currentVersion = XDocument.Load(projectPath).Descendants("Version").Single().Value;
        var notesPath = Path.Combine(RepoRoot, "src", "MaxHub.Agent.Tray", "Assets", "release-notes.zh-CN.json");
        using var document = JsonDocument.Parse(File.ReadAllText(notesPath));
        var notes = document.RootElement.EnumerateArray().ToArray();
        var versions = notes.Select(note => note.GetProperty("version").GetString()!).ToArray();

        Assert.Equal(versions.Length, versions.Distinct(StringComparer.Ordinal).Count());
        Assert.All(versions, version => Assert.True(Version.TryParse(version, out _), $"版本号无效：{version}"));
        Assert.Contains(currentVersion, versions);

        var current = notes.Single(note => note.GetProperty("version").GetString() == currentVersion);
        var serialized = current.GetRawText();
        Assert.Matches(new Regex("[\\u4e00-\\u9fff]"), serialized);
        Assert.NotEmpty(current.GetProperty("sections").EnumerateArray());
    }
}
