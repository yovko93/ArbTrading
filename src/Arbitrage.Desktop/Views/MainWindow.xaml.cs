using System.Windows;
using Arbitrage.Desktop.ViewModels;

namespace Arbitrage.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel) { InitializeComponent(); DataContext = viewModel; }
}
