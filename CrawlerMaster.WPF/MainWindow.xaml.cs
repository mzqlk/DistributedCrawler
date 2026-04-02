using CrawlerCore.Entities;
using CrawlerMaster.WPF.Data;
using Microsoft.EntityFrameworkCore;
using System.Windows;

namespace CrawlerMaster.WPF
{
    public partial class MainWindow : Window
    {
        private readonly CrawlerDbContext _dbContext;

        public MainWindow(CrawlerDbContext dbContext)
        {
            InitializeComponent();
            _dbContext = dbContext;
            //_ = TestDatabaseAsync();
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