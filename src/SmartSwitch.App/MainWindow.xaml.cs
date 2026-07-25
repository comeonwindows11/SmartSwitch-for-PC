using System.Windows;
using SmartSwitch.App.ViewModels;

namespace SmartSwitch.App;

public partial class MainWindow : Window
{
    public MainWindow(ShellViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
