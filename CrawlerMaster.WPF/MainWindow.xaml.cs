using System.Windows;
using CrawlerMaster.WPF.ViewModels;

namespace CrawlerMaster.WPF;

public partial class MainWindow : Window
{
    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}