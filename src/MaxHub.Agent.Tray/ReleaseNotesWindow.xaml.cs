using System.Windows;
using System.Windows.Input;

namespace MaxHub.Agent.Tray;

public partial class ReleaseNotesWindow : Window
{
    public ReleaseNotesWindow(string currentVersion, string? justUpdatedVersion = null)
    {
        InitializeComponent();
        DataContext = new ReleaseNotesViewModel(currentVersion, justUpdatedVersion);
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }
}
