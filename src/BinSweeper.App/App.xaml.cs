using System.Windows;

namespace BinSweeper.App;

public partial class App : System.Windows.Application
{
    private TrayIconHost? _tray;
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _window = new MainWindow();
        _tray = new TrayIconHost(
            onOpen: () => _window.ShowFromTray(),
            onScan: () => _window.TriggerScan(),
            onExit: () =>
            {
                _window.AllowClose();
                Shutdown();
            });
        _tray.Show();
        _window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }
}
