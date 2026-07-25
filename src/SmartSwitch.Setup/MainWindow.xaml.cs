using System.Windows;
using SmartSwitch.Setup.ViewModels;

namespace SmartSwitch.Setup;

public partial class MainWindow : Window
{
    public MainWindow(InstallerViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
