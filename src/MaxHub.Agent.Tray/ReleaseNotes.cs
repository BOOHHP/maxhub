using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;

namespace MaxHub.Agent.Tray;

public sealed record ReleaseNoteSection(string Title, string[] Items);
public sealed record ReleaseNote(string Version, string Date, string Summary, ReleaseNoteSection[] Sections);
public sealed record ReleaseNoteItem(ReleaseNote Note, bool IsCurrent, bool IsJustUpdated);

public static class ReleaseNotesCatalog
{
    public static IReadOnlyList<ReleaseNote> Load()
    {
        var uri = new Uri("Assets/release-notes.zh-CN.json", UriKind.Relative);
        var resource = Application.GetResourceStream(uri)
            ?? throw new InvalidOperationException("无法加载内置更新日志。");
        using var stream = resource.Stream;
        var notes = JsonSerializer.Deserialize<ReleaseNote[]>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? [];
        return notes
            .OrderByDescending(note => ParseVersion(note.Version))
            .ToArray();
    }

    private static Version ParseVersion(string value) =>
        Version.TryParse(value.Split('-')[0], out var parsed) ? parsed : new Version();
}

public sealed class ReleaseNotesViewModel : ViewModelBase
{
    private ReleaseNoteItem? _selectedNote;

    public ReleaseNotesViewModel(string currentVersion, string? justUpdatedVersion = null)
    {
        CurrentVersion = currentVersion;
        JustUpdatedVersion = justUpdatedVersion;
        Notes = new ObservableCollection<ReleaseNoteItem>(ReleaseNotesCatalog.Load()
            .Select(note => new ReleaseNoteItem(
                note,
                note.Version == currentVersion,
                note.Version == justUpdatedVersion)));
        SelectedItem = Notes.FirstOrDefault(item => item.IsJustUpdated)
            ?? Notes.FirstOrDefault(item => item.IsCurrent)
            ?? Notes.FirstOrDefault();
    }

    public ObservableCollection<ReleaseNoteItem> Notes { get; }
    public string CurrentVersion { get; }
    public string? JustUpdatedVersion { get; }
    public bool WasJustUpdated => JustUpdatedVersion is not null;
    public string HeaderText => WasJustUpdated
        ? $"✓ 已更新到 v{JustUpdatedVersion}"
        : $"当前版本 v{CurrentVersion}";

    public ReleaseNoteItem? SelectedItem
    {
        get => _selectedNote;
        set => Set(ref _selectedNote, value);
    }
}
