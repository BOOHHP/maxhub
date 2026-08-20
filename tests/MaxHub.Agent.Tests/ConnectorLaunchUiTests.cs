using System.Xml.Linq;

namespace MaxHub.Agent.Tests;

public class ConnectorLaunchUiTests
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
    public void Detected_Max_row_exposes_button_bound_to_open_command()
    {
        var xamlPath = Path.Combine(RepoRoot, "src", "MaxHub.Agent.Tray", "MainWindow.xaml");
        var document = XDocument.Load(xamlPath);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        var openButton = document.Descendants(presentation + "Button")
            .SingleOrDefault(button => (string?)button.Attribute("Content") == "打开 Max");

        Assert.NotNull(openButton);
        Assert.Equal("{Binding OpenCommand}", (string?)openButton.Attribute("Command"));
    }

    [Fact]
    public void Open_command_starts_the_detected_executable_without_shell_parsing()
    {
        var sourcePath = Path.Combine(RepoRoot, "src", "MaxHub.Agent.Tray", "ViewModels.cs");
        var source = File.ReadAllText(sourcePath);

        Assert.Contains("OpenCommand = new RelayCommand(OpenMax);", source);
        Assert.Contains("FileName = Installation.ExePath", source);
        Assert.Contains("WorkingDirectory = Installation.InstallDir", source);
        Assert.Contains("UseShellExecute = false", source);
    }
}