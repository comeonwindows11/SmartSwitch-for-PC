using System.Reflection;
using SmartSwitch.App.Services;
using SmartSwitch.Core.Abstractions;

namespace SmartSwitch.App.ViewModels;

public sealed class ShellViewModel : ObservableObject
{
    private object _currentPage;
    private string _breadcrumb = "SmartSwitch / Démarrage";

    public ShellViewModel(
        IMigrationEngine migrationEngine,
        INetworkTransferService transferService,
        INetworkInformationService networkInformation,
        IMigrationLogger logger,
        IFolderPickerService folderPicker)
    {
        Donor = new DonorViewModel(migrationEngine, transferService, logger, folderPicker);
        Receiver = new ReceiverViewModel(
            transferService,
            networkInformation,
            logger,
            folderPicker);
        Landing = new LandingViewModel(ShowDonor, ShowReceiver);
        _currentPage = Landing;

        NavigateHomeCommand = new RelayCommand(ShowLanding);
        NavigateDonorCommand = new RelayCommand(ShowDonor);
        NavigateReceiverCommand = new RelayCommand(ShowReceiver);
    }

    public LandingViewModel Landing { get; }

    public DonorViewModel Donor { get; }

    public ReceiverViewModel Receiver { get; }

    public object CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    public string Breadcrumb
    {
        get => _breadcrumb;
        private set => SetProperty(ref _breadcrumb, value);
    }

    public string ComputerName => Environment.MachineName;

    public string AppVersion =>
        $"Version {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0"}";

    public RelayCommand NavigateHomeCommand { get; }

    public RelayCommand NavigateDonorCommand { get; }

    public RelayCommand NavigateReceiverCommand { get; }

    private void ShowLanding()
    {
        CurrentPage = Landing;
        Breadcrumb = "SmartSwitch / Démarrage";
    }

    private void ShowDonor()
    {
        CurrentPage = Donor;
        Breadcrumb = "SmartSwitch / PC donneur";
    }

    private void ShowReceiver()
    {
        CurrentPage = Receiver;
        Breadcrumb = "SmartSwitch / PC receveur";
    }
}
