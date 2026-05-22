using System.Windows;
using PortChecker.Services;

namespace PortChecker;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private void ApplicationStartup(object sender, StartupEventArgs e)
    {
        PortableMode.Initialize();

        if (ProcessControlService.TryHandleElevatedCommand(e.Args)
            || ReservedPortRangeService.TryHandleElevatedCommand(e.Args))
        {
            Shutdown(Environment.ExitCode);
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
