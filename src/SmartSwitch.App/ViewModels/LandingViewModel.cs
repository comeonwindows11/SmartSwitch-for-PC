namespace SmartSwitch.App.ViewModels;

public sealed class LandingViewModel
{
    public LandingViewModel(Action showDonor, Action showReceiver)
    {
        ShowDonorCommand = new RelayCommand(showDonor);
        ShowReceiverCommand = new RelayCommand(showReceiver);
    }

    public RelayCommand ShowDonorCommand { get; }

    public RelayCommand ShowReceiverCommand { get; }

    public string ComputerName => Environment.MachineName;

    public string UserName => Environment.UserName;

    public string WindowsVersion => Environment.OSVersion.VersionString;
}
