using CrawlerCore.Entities;
using CrawlerCore.Events;
using CrawlerCore.Infrastructure;
using CrawlerCore.Interfaces;
using CrawlerMaster.WPF.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CrawlerMaster.WPF.ViewModels
{
    public class MainWindowViewModel : IDisposable, INotifyPropertyChanged
    {
        #region 原有字段（完全保留，无修改）
        private readonly IEventAggregator _eventAggregator;
        private readonly IServiceProvider _serviceProvider;
        private readonly ITaskSchedulerService _taskSchedulerService;
        private readonly Dispatcher _uiDispatcher;
        private Guid _nodeStatusSubscriptionId = Guid.Empty;
        private bool _disposed = false;
        #endregion

        #region 新增SignalR相关字段
        private readonly SignalRSettings _signalRSettings;
        private HubConnection? _hubConnection;
        private string? _hubUrl;
        private string _connectionState = "未连接";
        private bool _isSignalRConnected;
        #endregion

        #region 绑定到UI的集合
        public ObservableCollection<NodeDisplayInfo> Nodes { get; } = new ObservableCollection<NodeDisplayInfo>();
        public ObservableCollection<TaskDisplayInfo> Tasks { get; } = new ObservableCollection<TaskDisplayInfo>();
        #endregion

        #region 连接状态绑定属性
        public string ConnectionState
        {
            get => _connectionState;
            private set => SetProperty(ref _connectionState, value);
        }

        public bool IsSignalRConnected
        {
            get => _isSignalRConnected;
            private set => SetProperty(ref _isSignalRConnected, value);
        }
        #endregion

        #region INotifyPropertyChanged 实现
        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(field, value))
                return false;

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
        #endregion

        #region 构造函数
        public MainWindowViewModel(
            IEventAggregator eventAggregator,
            IServiceProvider serviceProvider,
            ITaskSchedulerService taskSchedulerService,
            IOptions<SignalRSettings> signalROptions)
        {
            _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _taskSchedulerService = taskSchedulerService ?? throw new ArgumentNullException(nameof(taskSchedulerService));
            _uiDispatcher = Application.Current.Dispatcher ?? throw new InvalidOperationException("Application.Current.Dispatcher 不可用");

            _nodeStatusSubscriptionId = _eventAggregator.Subscribe<NodeStatusChangedEventArgs>(OnNodeStatusChanged);
            _ = LoadNodesAsync();

            _signalRSettings = signalROptions.Value ?? throw new ArgumentNullException(nameof(signalROptions));
            _ = Task.Run(InitializeSignalRAsync);
        }
        #endregion

        #region SignalR客户端核心逻辑
        private async Task InitializeSignalRAsync()
        {
            try
            {
                LogHelper.Info("开始初始化SignalR客户端连接");

                _hubUrl = $"http://localhost:{_signalRSettings.Port}{_signalRSettings.HubPath}";
                _hubConnection = new HubConnectionBuilder()
                    .WithUrl(_hubUrl)
                    .WithAutomaticReconnect(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30) })
                    .Build();

                _hubConnection.Reconnecting += OnHubReconnecting;
                _hubConnection.Reconnected += OnHubReconnected;
                _hubConnection.Closed += OnHubClosed;

                RegisterMessageSubscriptions();
                await StartConnectionWithRetryAsync();
            }
            catch (Exception ex)
            {
                LogHelper.Error(ex, "SignalR客户端初始化失败");
                if (_uiDispatcher != null && !_uiDispatcher.HasShutdownStarted)
                {
                    _uiDispatcher.Invoke(() =>
                    {
                        ConnectionState = "初始化失败";
                        IsSignalRConnected = false;
                    });
                }
            }
        }

        private void RegisterMessageSubscriptions()
        {
            if (_hubConnection == null) return;

            _hubConnection.On<NodeUpdateMessage>("ReceiveNodeUpdate", async (message) =>
            {
                try
                {
                    if (_uiDispatcher == null || _uiDispatcher.HasShutdownStarted) return;
                    await _uiDispatcher.InvokeAsync(() =>
                    {
                        var existingNode = Nodes.FirstOrDefault(n => n.NodeId == message.NodeId);
                        if (existingNode != null)
                        {
                            existingNode.MachineName = message.MachineName;
                            existingNode.IpAddress = message.IpAddress;
                            existingNode.Status = message.Status;
                            existingNode.CpuUsage = message.CpuUsage;
                            existingNode.MemoryUsageMb = message.MemoryUsageMb;
                            existingNode.LastHeartbeat = message.LastHeartbeat;
                        }
                        else
                        {
                            Nodes.Add(new NodeDisplayInfo
                            {
                                NodeId = message.NodeId,
                                MachineName = message.MachineName,
                                IpAddress = message.IpAddress,
                                Status = message.Status,
                                CpuUsage = message.CpuUsage,
                                MemoryUsageMb = message.MemoryUsageMb,
                                LastHeartbeat = message.LastHeartbeat
                            });
                        }
                    });
                }
                catch (Exception ex)
                {
                    LogHelper.Error(ex, "处理节点状态推送消息失败");
                }
            });

            _hubConnection.On<TaskProgressMessage>("ReceiveTaskProgress", async (message) =>
            {
                try
                {
                    if (_uiDispatcher == null || _uiDispatcher.HasShutdownStarted) return;
                    await _uiDispatcher.InvokeAsync(() =>
                    {
                        var existingTask = Tasks.FirstOrDefault(t => t.TaskId == message.TaskId);
                        if (existingTask != null)
                        {
                            existingTask.TaskName = message.TaskName;
                            existingTask.Status = message.Status;
                            existingTask.CompletedPages = message.CompletedPages;
                            existingTask.TotalPages = message.TotalPages;
                            existingTask.AssignedNode = message.AssignedNode;
                        }
                        else
                        {
                            Tasks.Add(new TaskDisplayInfo
                            {
                                TaskId = message.TaskId,
                                TaskName = message.TaskName,
                                Status = message.Status,
                                CompletedPages = message.CompletedPages,
                                TotalPages = message.TotalPages,
                                AssignedNode = message.AssignedNode
                            });
                        }
                    });
                }
                catch (Exception ex)
                {
                    LogHelper.Error(ex, "处理任务进度推送消息失败");
                }
            });

            LogHelper.Info("SignalR消息订阅注册完成");
        }

        private async Task StartConnectionWithRetryAsync()
        {
            if (_hubConnection == null) return;

            var retryCount = 0;
            const int maxRetry = 5;

            while (retryCount < maxRetry)
            {
                try
                {
                    await _hubConnection.StartAsync();
                    LogHelper.Info($"SignalR客户端连接成功，服务端地址：{_hubUrl}");

                    if (_uiDispatcher != null && !_uiDispatcher.HasShutdownStarted)
                    {
                        _uiDispatcher.Invoke(() =>
                        {
                            ConnectionState = "实时已连接";
                            IsSignalRConnected = true;
                        });
                    }

                    return;
                }
                catch (Exception ex)
                {
                    retryCount++;
                    LogHelper.Warn($"SignalR连接失败，第{retryCount}次重试，错误：{ex.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(2));
                }
            }

            LogHelper.Error($"SignalR连接失败，已达到最大重试次数{maxRetry}");
            if (_uiDispatcher != null && !_uiDispatcher.HasShutdownStarted)
            {
                _uiDispatcher.Invoke(() =>
                {
                    ConnectionState = "连接失败";
                    IsSignalRConnected = false;
                });
            }
        }

        private Task OnHubReconnecting(Exception? ex)
        {
            try
            {
                if (_uiDispatcher != null && !_uiDispatcher.HasShutdownStarted)
                {
                    _uiDispatcher.Invoke(() =>
                    {
                        ConnectionState = "正在重连...";
                        IsSignalRConnected = false;
                        LogHelper.Warn("SignalR连接状态：重连中");
                    });
                }
            }
            catch (Exception e)
            {
                LogHelper.Error(e, "OnHubReconnecting 处理失败");
            }
            return Task.CompletedTask;
        }

        private Task OnHubReconnected(string? connectionId)
        {
            try
            {
                if (_uiDispatcher != null && !_uiDispatcher.HasShutdownStarted)
                {
                    _uiDispatcher.Invoke(() =>
                    {
                        ConnectionState = "实时已连接";
                        IsSignalRConnected = true;
                        LogHelper.Info("SignalR连接状态：已连接");
                    });
                }
            }
            catch (Exception e)
            {
                LogHelper.Error(e, "OnHubReconnected 处理失败");
            }
            return Task.CompletedTask;
        }

        private Task OnHubClosed(Exception? ex)
        {
            try
            {
                if (_uiDispatcher != null && !_uiDispatcher.HasShutdownStarted)
                {
                    _uiDispatcher.Invoke(() =>
                    {
                        ConnectionState = "实时已断开";
                        IsSignalRConnected = false;
                        LogHelper.Warn("SignalR连接状态：已断开");
                    });
                }
            }
            catch (Exception e)
            {
                LogHelper.Error(e, "OnHubClosed 处理失败");
            }
            return Task.CompletedTask;
        }
        #endregion

        #region 原有方法（完全保留，无修改）
        private void OnNodeStatusChanged(object? sender, NodeStatusChangedEventArgs args)
        {
            try
            {
                if (_uiDispatcher == null || _uiDispatcher.HasShutdownStarted) return;
                _uiDispatcher.Invoke(() =>
                {
                    try
                    {
                        var existingNode = Nodes.FirstOrDefault(n => n.NodeId == args.NodeId);
                        if (existingNode != null)
                        {
                            UpdateNodeDisplayInfo(existingNode, args.NodeInfo);
                        }
                        else
                        {
                            Nodes.Add(CreateNodeDisplayInfo(args.NodeInfo));
                        }
                    }
                    catch (Exception ex)
                    {
                        LogHelper.Error(ex, "UI线程更新节点信息失败");
                    }
                });
            }
            catch (Exception ex)
            {
                LogHelper.Error(ex, "调度UI线程更新节点信息失败");
            }
        }

        private NodeDisplayInfo CreateNodeDisplayInfo(NodeInfo nodeInfo)
        {
            return new NodeDisplayInfo
            {
                NodeId = nodeInfo.NodeId,
                MachineName = nodeInfo.MachineName,
                IpAddress = nodeInfo.IpAddress,
                CpuCores = nodeInfo.CpuCores,
                TotalMemoryGb = nodeInfo.TotalMemoryGb,
                Status = nodeInfo.Status,
                CpuUsage = nodeInfo.CpuUsage,
                MemoryUsageMb = nodeInfo.MemoryUsageMb,
                LastHeartbeat = nodeInfo.LastHeartbeat
            };
        }

        private void UpdateNodeDisplayInfo(NodeDisplayInfo displayInfo, NodeInfo nodeInfo)
        {
            displayInfo.MachineName = nodeInfo.MachineName;
            displayInfo.IpAddress = nodeInfo.IpAddress;
            displayInfo.CpuCores = nodeInfo.CpuCores;
            displayInfo.TotalMemoryGb = nodeInfo.TotalMemoryGb;
            displayInfo.Status = nodeInfo.Status;
            displayInfo.CpuUsage = nodeInfo.CpuUsage;
            displayInfo.MemoryUsageMb = nodeInfo.MemoryUsageMb;
            displayInfo.LastHeartbeat = nodeInfo.LastHeartbeat;
        }

        public async Task LoadNodesAsync()
        {
            try
            {
                LogHelper.Info("开始加载节点列表");

                using var scope = _serviceProvider.CreateScope();
                var nodeService = scope.ServiceProvider.GetRequiredService<INodeManagementService>();
                var activeNodes = await nodeService.GetActiveNodesAsync();

                if (_uiDispatcher != null && !_uiDispatcher.HasShutdownStarted)
                {
                    _uiDispatcher.Invoke(() =>
                    {
                        Nodes.Clear();
                        foreach (var node in activeNodes)
                        {
                            Nodes.Add(CreateNodeDisplayInfo(node));
                        }
                    });
                }

                LogHelper.Info("节点列表加载完成，共 {Count} 个活跃节点", activeNodes.Count);
            }
            catch (Exception ex)
            {
                LogHelper.Error(ex, "加载节点列表失败");
            }
        }

        public async Task TestCreateAndDispatchTaskAsync()
        {
            try
            {
                LogHelper.Info("=== 手动触发测试任务下发 ===");

                var testTask = new CrawlerTask
                {
                    TaskName = $"手动测试-{DateTime.Now:HHmmss}",
                    PluginName = "ThesisPlugin",
                    MaxDepth = 2,
                    MaxPages = 5,
                    Priority = 3,
                    StartUrls = new System.Collections.Generic.List<string> { "https://example.com" },
                    Configuration = new System.Collections.Generic.Dictionary<string, string> { ["UserAgent"] = "TestBot" }
                };

                bool scheduled = await _taskSchedulerService.ScheduleTaskAsync(testTask);
                if (scheduled)
                {
                    LogHelper.Info($"测试任务已入队，TaskId = {testTask.TaskId}，等待调度器分配...");
                    MessageBox.Show($"任务已创建并入队！\nTaskId: {testTask.TaskId}\n请观察 Master 与 Worker 日志。",
                                    "测试下发", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    LogHelper.Error("测试任务入队失败");
                    MessageBox.Show("任务入队失败，请查看日志。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                LogHelper.Error(ex, "手动测试任务下发异常");
                MessageBox.Show($"异常：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        #endregion

        #region IDisposable 实现（优化版）
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;

            if (disposing)
            {
                if (_nodeStatusSubscriptionId != Guid.Empty)
                {
                    try
                    {
                        _eventAggregator.Unsubscribe<NodeStatusChangedEventArgs>(_nodeStatusSubscriptionId);
                        _nodeStatusSubscriptionId = Guid.Empty;
                        LogHelper.Info("MainWindowViewModel 已取消节点状态变更事件订阅");
                    }
                    catch (Exception ex)
                    {
                        LogHelper.Error(ex, "取消节点状态变更事件订阅失败");
                    }
                }

                try
                {
                    if (_hubConnection != null)
                    {
                        _hubConnection.Reconnecting -= OnHubReconnecting;
                        _hubConnection.Reconnected -= OnHubReconnected;
                        _hubConnection.Closed -= OnHubClosed;

                        // 优化：后台线程异步释放，ConfigureAwait(false)避免死锁
                        Task.Run(async () =>
                        {
                            await _hubConnection.DisposeAsync().ConfigureAwait(false);
                        }).Wait(TimeSpan.FromSeconds(3));

                        _hubConnection = null;
                        LogHelper.Info("SignalR客户端资源已释放");
                    }
                }
                catch (Exception ex)
                {
                    LogHelper.Error(ex, "释放SignalR客户端资源失败");
                }
            }

            _disposed = true;
        }

        ~MainWindowViewModel()
        {
            Dispose(false);
        }
        #endregion
    }
}