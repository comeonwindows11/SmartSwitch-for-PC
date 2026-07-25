using System.Globalization;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using SmartSwitch.App.Services;
using SmartSwitch.App.ViewModels;
using SmartSwitch.Infrastructure.DependencyInjection;

namespace SmartSwitch.App;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                args.Exception.ToString(),
                "SmartSwitch — Erreur inattendue",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        try
        {
            var culture = CultureInfo.GetCultureInfo("fr-CA");
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;

            var services = new ServiceCollection();
            services.AddSmartSwitch();
            services.AddSingleton<IFolderPickerService, FolderPickerService>();
            services.AddSingleton<ShellViewModel>();
            services.AddSingleton<MainWindow>();
            _serviceProvider = services.BuildServiceProvider(validateScopes: true);
            _serviceProvider.GetRequiredService<MainWindow>().Show();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                exception.Message,
                "SmartSwitch — Échec du démarrage",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }

}
