using System.Text.Json;

namespace MaxHub.Agent.Core.Remote;

/// <summary>独立于登录会话保存已自动展示的更新日志版本。</summary>
public sealed class ReleaseNotesStateStore(string statePath)
{
    public bool ShouldAutoShow(string currentVersion, bool launchedAfterUpdate, bool existingInstallation)
    {
        var previousVersion = ReadLastShownVersion();
        var shouldShow = launchedAfterUpdate ||
            (previousVersion is null ? existingInstallation : previousVersion != currentVersion);
        Save(currentVersion);
        return shouldShow;
    }

    public string? ReadLastShownVersion()
    {
        if (!File.Exists(statePath))
            return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(statePath));
            return document.RootElement.TryGetProperty("lastShownVersion", out var version)
                ? version.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private void Save(string version)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(statePath))!);
        var tempPath = statePath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(new { lastShownVersion = version }));
        File.Move(tempPath, statePath, overwrite: true);
    }
}
