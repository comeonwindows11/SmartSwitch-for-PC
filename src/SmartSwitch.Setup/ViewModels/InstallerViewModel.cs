using System.Windows;
using Microsoft.Win32;
using SmartSwitch.Setup.Services;

namespace SmartSwitch.Setup.ViewModels;

public sealed class InstallerViewModel : ObservableObject
{
    private readonly InstallerService _installerService;
    private readonly bool _isUninstall;
    private WizardPage _page = WizardPage.Welcome;
    private string _installationPath;
    private bool _createDesktopShortcut = true;
    private double _progress;
    private string _statusMessage = "Prêt.";
    private string _errorMessage = string.Empty;
    private bool _operationSucceeded = true;

    public InstallerViewModel(InstallerService installerService, bool isUninstall)
    {
        _installerService = installerService;
        _isUninstall = isUninstall;
        _installationPath = installerService.GetDefaultInstallationPath();
        PrimaryCommand = new AsyncRelayCommand(AdvanceAsync);
        BackCommand = new RelayCommand(GoBack);
        CancelCommand = new RelayCommand(Cancel);
        BrowseCommand = new RelayCommand(Browse);
    }

    public string WindowTitle => _isUninstall
        ? "Désinstallation de SmartSwitch"
        : "Installation de SmartSwitch";

    public string PageTitle => Page switch
    {
        WizardPage.Welcome when _isUninstall => "Désinstaller SmartSwitch",
        WizardPage.Welcome => "Bienvenue dans SmartSwitch",
        WizardPage.Options => "Options d'installation",
        WizardPage.Installing when _isUninstall => "Désinstallation en cours…",
        WizardPage.Installing => "Installation en cours…",
        WizardPage.Complete when !_operationSucceeded => "L'opération a échoué",
        WizardPage.Complete when _isUninstall => "SmartSwitch a été désinstallé",
        WizardPage.Complete => "SmartSwitch est prêt",
        _ => string.Empty,
    };

    public string PageDescription => Page switch
    {
        WizardPage.Welcome when _isUninstall =>
            "Cet assistant supprimera l'application, ses raccourcis et son inscription Windows.",
        WizardPage.Welcome =>
            "Cet assistant installe SmartSwitch Migration Tool pour votre compte Windows.",
        WizardPage.Complete when !_operationSucceeded =>
            "Aucun changement supplémentaire ne sera effectué. Consultez le détail ci-dessous.",
        WizardPage.Complete when _isUninstall =>
            "Les composants installés ont été retirés de ce PC.",
        WizardPage.Complete =>
            "Vous pouvez maintenant lancer l'application depuis le menu Démarrer.",
        _ => string.Empty,
    };

    public string WelcomeNote => _isUninstall
        ? "Vos migrations déjà reçues et vos journaux personnels ne sont pas supprimés."
        : "Le transfert réseau reste local entre les deux PC associés. Aucun compte en ligne n'est requis.";

    public string InstallationPath
    {
        get => _installationPath;
        set => SetProperty(ref _installationPath, value);
    }

    public bool CreateDesktopShortcut
    {
        get => _createDesktopShortcut;
        set => SetProperty(ref _createDesktopShortcut, value);
    }

    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public bool IsWelcome => Page == WizardPage.Welcome;

    public bool IsOptions => Page == WizardPage.Options;

    public bool IsInstalling => Page == WizardPage.Installing;

    public bool IsComplete => Page == WizardPage.Complete;

    public bool CanGoBack => Page == WizardPage.Options;

    public bool CanUsePrimaryButton => Page != WizardPage.Installing;

    public string PrimaryButtonText => Page switch
    {
        WizardPage.Welcome when _isUninstall => "Désinstaller",
        WizardPage.Welcome => "Suivant",
        WizardPage.Options => "Installer",
        WizardPage.Installing => "Veuillez patienter…",
        WizardPage.Complete => "Fermer",
        _ => "Suivant",
    };

    public string CancelButtonText => Page == WizardPage.Complete ? "Fermer" : "Annuler";

    public AsyncRelayCommand PrimaryCommand { get; }

    public RelayCommand BackCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand BrowseCommand { get; }

    private WizardPage Page
    {
        get => _page;
        set
        {
            if (!SetProperty(ref _page, value))
            {
                return;
            }

            OnPropertyChanged(nameof(PageTitle));
            OnPropertyChanged(nameof(PageDescription));
            OnPropertyChanged(nameof(IsWelcome));
            OnPropertyChanged(nameof(IsOptions));
            OnPropertyChanged(nameof(IsInstalling));
            OnPropertyChanged(nameof(IsComplete));
            OnPropertyChanged(nameof(CanGoBack));
            OnPropertyChanged(nameof(CanUsePrimaryButton));
            OnPropertyChanged(nameof(PrimaryButtonText));
            OnPropertyChanged(nameof(CancelButtonText));
        }
    }

    private async Task AdvanceAsync()
    {
        if (Page == WizardPage.Complete)
        {
            Application.Current.Shutdown();
            return;
        }

        if (Page == WizardPage.Welcome && !_isUninstall)
        {
            Page = WizardPage.Options;
            return;
        }

        Page = WizardPage.Installing;
        Progress = 0;
        ErrorMessage = string.Empty;
        _operationSucceeded = true;
        var progress = new Progress<InstallerProgress>(value =>
        {
            Progress = value.Percentage;
            StatusMessage = value.Message;
        });

        try
        {
            if (_isUninstall)
            {
                await _installerService.UninstallAsync(progress);
            }
            else
            {
                await _installerService.InstallAsync(
                    InstallationPath,
                    CreateDesktopShortcut,
                    progress);
            }

            Progress = 100;
        }
        catch (Exception exception)
        {
            _operationSucceeded = false;
            ErrorMessage = exception.Message;
        }

        Page = WizardPage.Complete;
    }

    private void GoBack()
    {
        if (Page == WizardPage.Options)
        {
            Page = WizardPage.Welcome;
        }
    }

    private void Cancel()
    {
        if (Page != WizardPage.Installing)
        {
            Application.Current.Shutdown();
        }
    }

    private void Browse()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choisir le dossier d'installation",
            InitialDirectory = Directory.Exists(InstallationPath)
                ? InstallationPath
                : Path.GetDirectoryName(InstallationPath),
        };
        if (dialog.ShowDialog() == true)
        {
            InstallationPath = dialog.FolderName;
        }
    }
}

internal enum WizardPage
{
    Welcome,
    Options,
    Installing,
    Complete,
}
