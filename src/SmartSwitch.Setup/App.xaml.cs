using System.Diagnostics;
using System.Windows;
using SmartSwitch.Setup.Services;
using SmartSwitch.Setup.ViewModels;

namespace SmartSwitch.Setup;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var isUninstall = e.Args.Any(
            argument => string.Equals(
                argument,
                "--uninstall",
                StringComparison.OrdinalIgnoreCase));
        var isWorker = e.Args.Any(
            argument => string.Equals(
                argument,
                "--uninstall-worker",
                StringComparison.OrdinalIgnoreCase));

        if (isUninstall && !isWorker && TryLaunchUninstallWorker())
        {
            Shutdown();
            return;
        }

        var installerService = new InstallerService();
        var viewModel = new InstallerViewModel(installerService, isUninstall || isWorker);
        new MainWindow(viewModel).Show();
    }

    private static bool TryLaunchUninstallWorker()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return false;
        }

        var temporaryPath = Path.Combine(
            Path.GetTempPath(),
            $"SmartSwitch-Uninstall-{Guid.NewGuid():N}.exe");
        try
        {
            File.Copy(executablePath, temporaryPath);
            Process.Start(new ProcessStartInfo
            {
                FileName = temporaryPath,
                Arguments = "--uninstall-worker",
                UseShellExecute = true,
            });
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
