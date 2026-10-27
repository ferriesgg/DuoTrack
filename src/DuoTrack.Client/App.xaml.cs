using System.Windows;

namespace DuoTrack.Client;

public partial class App : System.Windows.Application
{
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _window = new MainWindow();
        if (!e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase))
        {
            _window.Show();
        }
    }
}
