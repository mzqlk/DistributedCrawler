using CrawlerMaster.WPF.ViewModels;
using System;
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

        /// <summary>
        /// 窗口关闭时异步调用DisposeAsync，确保资源完整释放
        /// </summary>
        protected override async void OnClosed(EventArgs e)
        {
            if (_viewModel != null)
            {
                await _viewModel.DisposeAsync();
            }
            base.OnClosed(e);
        }
    }
}