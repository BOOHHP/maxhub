using Microsoft.Win32;

namespace MaxHub.Agent.Core.Detection;

/// <summary>生产实现：读 HKLM\SOFTWARE\Autodesk\3dsMax\&lt;ver&gt;\Installdir。</summary>
public sealed class WindowsMaxRegistryReader : IMaxRegistryReader
{
    public IEnumerable<(string VersionKey, string InstallDir)> EnumerateInstallations()
    {
        if (!OperatingSystem.IsWindows())
            yield break;

        using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Autodesk\3dsMax");
        if (root is null)
            yield break;

        foreach (var versionKey in root.GetSubKeyNames())
        {
            using var versionSubKey = root.OpenSubKey(versionKey);
            if (versionSubKey?.GetValue("Installdir") is string installDir)
                yield return (versionKey, installDir);
        }
    }
}
