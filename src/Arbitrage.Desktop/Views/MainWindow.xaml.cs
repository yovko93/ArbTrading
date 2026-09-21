using System.Windows;
using Arbitrage.Desktop.ViewModels;

namespace Arbitrage.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow(ShellViewModel viewModel) { InitializeComponent(); DataContext = viewModel; }
}
