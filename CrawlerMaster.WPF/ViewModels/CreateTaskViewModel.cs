using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CrawlerCore.Entities;
using CrawlerCore.Interfaces;
using MaterialDesignThemes.Wpf;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;

namespace CrawlerMaster.WPF.ViewModels
{
    public partial class CreateTaskViewModel : ObservableValidator
    {
        [ObservableProperty]
        [Required(ErrorMessage = "任务名称不能为空")]
        [MinLength(2, ErrorMessage = "任务名称至少2个字符")]
        private string _taskName = string.Empty;

        [ObservableProperty]
        [Required(ErrorMessage = "插件名称不能为空")]
        private string _pluginName = "MockTestPlugin";

        [ObservableProperty]
        [Required(ErrorMessage = "起始URL不能为空")]
        private string _startUrlsText = "https://example.com";

        [ObservableProperty]
        [Range(1, 10, ErrorMessage = "最大深度必须在1-10之间")]
        private int _maxDepth = 2;

        [ObservableProperty]
        [Range(1, 10000, ErrorMessage = "最大页数必须在1-10000之间")]
        private int _maxPages = 50;

        [ObservableProperty]
        [Range(1, 5, ErrorMessage = "优先级必须在1-5之间")]
        private int _priority = 3;

        private readonly IServiceProvider _serviceProvider;

        public CreateTaskViewModel(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        [RelayCommand]
        private async Task CreateTask()
        {
            // 触发验证
            ValidateAllProperties();
            if (HasErrors) return;

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var taskSchedulerService = scope.ServiceProvider.GetRequiredService<ITaskSchedulerService>();

                var crawlerTask = new CrawlerTask
                {
                    TaskName = TaskName,
                    PluginName = PluginName,
                    StartUrls = StartUrlsText.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                                            .Select(u => u.Trim())
                                            .ToList(),
                    MaxDepth = MaxDepth,
                    MaxPages = MaxPages,
                    Priority = Priority,
                    Configuration = new Dictionary<string, string>
                    {
                        { "test", "test" } //预留的任务配置参数，可以根据实际插件需求添加更多配置项
                    }
                };

                await taskSchedulerService.ScheduleTaskAsync(crawlerTask);
                CrawlerCore.Infrastructure.LogHelper.Info($"任务创建成功：{crawlerTask.TaskId}");

                // 关闭对话框
                DialogHost.Close("TaskDialogHost", true);
            }
            catch (Exception ex)
            {
                CrawlerCore.Infrastructure.LogHelper.Error(ex, "创建任务失败");
            }
        }
    }
}