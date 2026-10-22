using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CrawlerCore.Entities;
using CrawlerCore.Events;
using CrawlerCore.Interfaces;
using CrawlerMaster.WPF.Models;
using MaterialDesignThemes.Wpf;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace CrawlerMaster.WPF.ViewModels
{
    public partial class TaskManagementViewModel : ObservableObject, IDisposable
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IEventAggregator _eventAggregator;
        private readonly Guid _subscriptionId;
        private bool _disposed;

        [ObservableProperty]
        private ObservableCollection<TaskDisplayInfo> _taskList = new();

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(LoadTaskListCommand))]
        private bool _isLoading = false;

        public TaskManagementViewModel(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _eventAggregator = _serviceProvider.GetRequiredService<IEventAggregator>();

            // 订阅任务进度更新事件
            _subscriptionId = _eventAggregator.Subscribe<TaskProgressUpdatedEventArgs>(OnTaskProgressUpdated);

            // 初始化加载任务列表
            _ = LoadTaskListAsync();
        }

        /// <summary>
        /// 事件处理程序：当收到任务进度更新时，同步更新 UI 列表
        /// </summary>
        private void OnTaskProgressUpdated(object? sender, TaskProgressUpdatedEventArgs e)
        {
            // 异步投递到 UI 线程（调度队列按 FIFO 执行，保持事件顺序），不阻塞 gRPC 读循环
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var taskInfo = TaskList.FirstOrDefault(t => t.TaskId == e.TaskId);
                if (taskInfo != null)
                {
                    // 更新现有任务信息
                    taskInfo.Status = e.CurrentStatus;
                    taskInfo.CompletedPages = e.CrawledPages;
                    taskInfo.TotalPages = e.TotalPages;
                    taskInfo.AssignedNode = e.AssignedNodeId;
                    taskInfo.TaskName = e.TaskName;
                }
                else
                {
                    // 如果列表中不存在该任务，则作为新任务添加
                    TaskList.Add(new TaskDisplayInfo
                    {
                        TaskId = e.TaskId,
                        TaskName = e.TaskName,
                        Status = e.CurrentStatus,
                        CompletedPages = e.CrawledPages,
                        TotalPages = e.TotalPages,
                        AssignedNode = e.AssignedNodeId
                    });
                }
            });
        }

        [RelayCommand]
        private async Task ShowCreateTaskDialog()
        {
            var dialogVm = _serviceProvider.GetRequiredService<CreateTaskViewModel>();
            var view = new Views.CreateTaskDialog { DataContext = dialogVm };

            // 监听对话框关闭事件刷新列表
            var result = await DialogHost.Show(view, "TaskDialogHost");
            if (result is true)
            {
                await LoadTaskListAsync();
            }
        }

        [RelayCommand(CanExecute = nameof(CanExecuteCommands))]
        private async Task LoadTaskListAsync()
        {
            if (IsLoading) return;
            IsLoading = true;

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var taskDataService = scope.ServiceProvider.GetRequiredService<ITaskDataService>();
                var tasks = await taskDataService.GetAllTasksAsync();

                // 合并更新（保留已有任务进度，仅更新基础字段或新增任务）
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    foreach (var task in tasks)
                    {
                        var existing = TaskList.FirstOrDefault(t => t.TaskId == task.TaskId);
                        if (existing == null)
                        {
                            TaskList.Add(new TaskDisplayInfo
                            {
                                TaskId = task.TaskId,
                                TaskName = task.TaskName,
                                Status = task.Status,
                                CompletedPages = 0,
                                TotalPages = task.MaxPages,
                                AssignedNode = task.AssignedNode
                            });
                        }
                        else
                        {
                            // 只更新那些事件可能没有覆盖的基础属性。
                            // TotalPages 由进度事件维护（MaxPages=0 表示不限页数），不能用 MaxPages 覆盖，否则进度会跳回 0%
                            existing.TaskName = task.TaskName;
                            existing.AssignedNode = task.AssignedNode;
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                CrawlerCore.Infrastructure.LogHelper.Error(ex, "加载任务列表失败");
            }
            finally
            {
                IsLoading = false;
            }
        }

        private bool CanExecuteCommands() => !IsLoading;

        #region IDisposable 实现（取消事件订阅）
        public void Dispose()
        {
            if (!_disposed)
            {
                _eventAggregator.Unsubscribe<TaskProgressUpdatedEventArgs>(_subscriptionId);
                _disposed = true;
            }
            GC.SuppressFinalize(this);
        }
        #endregion
    }
}