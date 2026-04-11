using CrawlerCore.Entities;
using CrawlerMaster.WPF.Data;
using CrawlerMaster.WPF.ViewModels;
using Microsoft.EntityFrameworkCore;
using System.Windows;

namespace CrawlerMaster.WPF
{
    public partial class MainWindow : Window
    {
        private readonly CrawlerDbContext _dbContext;
        private readonly MainWindowViewModel _viewModel;

        public MainWindow(MainWindowViewModel viewModel)
        {
            InitializeComponent();
            //_dbContext = dbContext;
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            DataContext = viewModel;

            // 绑定刷新按钮事件
            RefreshButton.Click += async (s, e) => await _viewModel.LoadNodesAsync();
            //_ = TestDatabaseAsync();
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

        private async Task TestDatabaseAsync()
        {
            try
            {
                // 测试数据
                var testTask = new CrawlerTask
                {
                    TaskName = "阶段一工作日5-验证任务",
                    PluginName = "TestPlugin",
                    MaxDepth = 2,
                    CreatedAt = DateTime.Now,
                    StartUrls = ["https://example.com"],
                    Configuration = new Dictionary<string, string> { ["Timeout"] = "5000" }
                };

                _dbContext.CrawlerTasks.Add(testTask);
                await _dbContext.SaveChangesAsync();

                var result = await _dbContext.CrawlerTasks.FirstOrDefaultAsync(t => t.TaskId == testTask.TaskId);
                MessageBox.Show($"验证成功！\n任务ID：{result?.TaskId}", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"测试失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}