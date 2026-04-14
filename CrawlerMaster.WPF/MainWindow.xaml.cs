using CrawlerCore.Entities;
using CrawlerMaster.WPF.Data;
using CrawlerMaster.WPF.ViewModels;
using Microsoft.EntityFrameworkCore;
using System.Windows;

namespace CrawlerMaster.WPF
{
    public partial class MainWindow : Window
    {
        private readonly MainWindowViewModel _viewModel;

        public MainWindow(MainWindowViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            DataContext = viewModel;

            // 绑定刷新按钮事件
            RefreshButton.Click += async (s, e) => await _viewModel.LoadNodesAsync();
        }

        private async void TestDispatchButton_Click(object sender, RoutedEventArgs e)
        {
            await _viewModel.TestCreateAndDispatchTaskAsync();
        }

        protected override void OnClosed(EventArgs e)
        {
            // 清理 ViewModel 资源
            _viewModel.Dispose();
            base.OnClosed(e);
        }
    }
}