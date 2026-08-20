using MaxHub.Agent.Core.Remote;

namespace MaxHub.Agent.Tests;

public class ReleaseNotesStateStoreTests
{
    [Fact]
    public void Fresh_install_does_not_auto_show_but_records_version()
    {
        var path = TempPath();
        try
        {
            var store = new ReleaseNotesStateStore(path);
            Assert.False(store.ShouldAutoShow("1.0.14", launchedAfterUpdate: false, existingInstallation: false));
            Assert.Equal("1.0.14", store.ReadLastShownVersion());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Existing_install_without_state_auto_shows_once()
    {
        var path = TempPath();
        try
        {
            var store = new ReleaseNotesStateStore(path);
            Assert.True(store.ShouldAutoShow("1.0.14", launchedAfterUpdate: false, existingInstallation: true));
            Assert.False(store.ShouldAutoShow("1.0.14", launchedAfterUpdate: false, existingInstallation: true));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void New_version_or_explicit_update_auto_shows_once()
    {
        var path = TempPath();
        try
        {
            var store = new ReleaseNotesStateStore(path);
            store.ShouldAutoShow("1.0.14", launchedAfterUpdate: false, existingInstallation: false);
            Assert.True(store.ShouldAutoShow("1.0.15", launchedAfterUpdate: false, existingInstallation: true));
            Assert.False(store.ShouldAutoShow("1.0.15", launchedAfterUpdate: false, existingInstallation: true));
            Assert.True(store.ShouldAutoShow("1.0.15", launchedAfterUpdate: true, existingInstallation: true));
        }
        finally { File.Delete(path); }
    }

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"maxhub-release-notes-{Guid.NewGuid():N}.json");
}
