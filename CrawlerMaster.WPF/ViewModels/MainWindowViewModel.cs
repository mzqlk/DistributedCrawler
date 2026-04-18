using CrawlerCore.Entities;
using CrawlerCore.Events;
using CrawlerCore.Infrastructure;
using CrawlerCore.Interfaces;
using CrawlerMaster.WPF.Models;
using CrawlerMaster.WPF.Services.Hubs;
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
    /// <summary>
    /// 主窗口ViewModel
    /// </summary>
    public class MainWindowViewModel : IAsyncDisposable, INotifyPropertyChanged
    {
        #region 私有字段
        private readonly IEventAggregator _eventAggregator;
        private readonly IServiceProvider _serviceProvider;
        private readonly ITaskSchedulerService _taskSchedulerService;
        private readonly Dispatcher _uiDispatcher;
        private readonly SignalRSettings _signalRSettings;

        // 订阅ID
        private Guid _nodeStatusSubscriptionId = Guid.Empty;
        private Guid _taskProgressSubscriptionId = Guid.Empty;

        // SignalR相关
        private HubConnection? _hubConnection;
        private string? _hubUrl;
        private string _connectionState = "未连接";
        private bool _isSignalRConnected;

        // 资源释放标记
        private bool _disposed = false;
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

            // 【修复1】双事件订阅：节点状态+任务进度
            _nodeStatusSubscriptionId = _eventAggregator.Subscribe<NodeStatusChangedEventArgs>(OnNodeStatusChanged);
            _taskProgressSubscriptionId = _eventAggregator.Subscribe<TaskProgressUpdatedEventArgs>(OnTaskProgressUpdated);

            // 初始加载节点列表
            _ = LoadNodesAsync();

            // 初始化SignalR
            _signalRSettings = signalROptions.Value ?? throw new ArgumentNullException(nameof(signalROptions));
            _ = Task.Run(InitializeSignalRAsync);
        }
        #endregion

        #region SignalR客户端初始化（修复版）
        private async Task InitializeSignalRAsync()
        {
            if (_disposed) return;

            try
            {
                LogHelper.Info("开始初始化SignalR客户端连接");

                _hubUrl = $"http://localhost:{_signalRSettings.Port}{_signalRSettings.HubPath}";
                _hubConnection = new HubConnectionBuilder()
                    .WithUrl(_hubUrl)
                    // 【修复2】优化重连策略：1/2/4/8/10秒，避免重连风暴
                    .WithAutomaticReconnect(new[]
                    {
                        TimeSpan.FromSeconds(1),
                        TimeSpan.FromSeconds(2),
                        TimeSpan.FromSeconds(4),
                        TimeSpan.FromSeconds(8),
                        TimeSpan.FromSeconds(10)
                    })
                    .Build();

                // 绑定连接状态事件
                _hubConnection.Reconnecting += OnHubReconnecting;
                _hubConnection.Reconnected += OnHubReconnected;
                _hubConnection.Closed += OnHubClosed;

                // 【修复3】强类型方法名注册消息订阅，编译时校验
                RegisterMessageSubscriptions();

                // 启动连接
                await StartConnectionWithRetryAsync();
            }
            catch (Exception ex)
            {
                LogHelper.Error(ex, "SignalR客户端初始化失败");
                UpdateConnectionState("初始化失败", false);
            }
        }

        /// <summary>
        /// 【修复】强类型方法名注册消息订阅，与服务端推送方法100%匹配
        /// </summary>
        private void RegisterMessageSubscriptions()
        {
            if (_hubConnection == null || _disposed) return;

            // 节点状态更新订阅（强类型方法名）
            _hubConnection.On<NodeUpdateMessage>(
                nameof(ICrawlerHubClient.OnNodeUpdated),
                async (message) =>
                {
                    if (_disposed || _uiDispatcher.HasShutdownStarted) return;
                    try
                    {
                        await _uiDispatcher.InvokeAsync(() =>
                        {
                            var existingNode = Nodes.FirstOrDefault(n => n.NodeId == message.NodeId);
                            if (existingNode != null)
                            {
                                existingNode.MachineName = message.MachineName;
                                existingNode.IpAddress = message.IpAddress;
                                existingNode.Status = message.Status;
                                existingNode.CpuUsage = Math.Max(0, message.CpuUsage);
                                existingNode.MemoryUsageMb = Math.Max(0, message.MemoryUsageMb);
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
                                    CpuUsage = Math.Max(0, message.CpuUsage),
                                    MemoryUsageMb = Math.Max(0, message.MemoryUsageMb),
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

            // 任务进度更新订阅（强类型方法名）
            _hubConnection.On<TaskProgressMessage>(
                nameof(ICrawlerHubClient.OnTaskProgressUpdated),
                async (message) =>
                {
                    if (_disposed || _uiDispatcher.HasShutdownStarted) return;
                    try
                    {
                        await _uiDispatcher.InvokeAsync(() =>
                        {
                            var existingTask = Tasks.FirstOrDefault(t => t.TaskId == message.TaskId);
                            if (existingTask != null)
                            {
                                existingTask.TaskName = message.TaskName;
                                existingTask.Status = message.Status;
                                existingTask.CompletedPages = Math.Max(0, message.CompletedPages);
                                existingTask.TotalPages = Math.Max(0, message.TotalPages);
                                existingTask.AssignedNode = message.AssignedNode;
                            }
                            else
                            {
                                Tasks.Add(new TaskDisplayInfo
                                {
                                    TaskId = message.TaskId,
                                    TaskName = message.TaskName,
                                    Status = message.Status,
                                    CompletedPages = Math.Max(0, message.CompletedPages),
                                    TotalPages = Math.Max(0, message.TotalPages),
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
            if (_hubConnection == null || _disposed) return;

            var retryCount = 0;
            const int maxRetry = 5;

            while (retryCount < maxRetry && !_disposed)
            {
                try
                {
                    await _hubConnection.StartAsync();
                    LogHelper.Info($"SignalR客户端连接成功，服务端地址：{_hubUrl}");
                    UpdateConnectionState("实时已连接", true);
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
            UpdateConnectionState("连接失败", false);
        }

        private Task OnHubReconnecting(Exception? ex)
        {
            if (_disposed) return Task.CompletedTask;
            try
            {
                UpdateConnectionState("正在重连...", false);
                LogHelper.Warn($"SignalR连接状态：重连中，异常: {ex?.Message}");
            }
            catch (Exception e)
            {
                LogHelper.Error(e, "OnHubReconnecting 处理失败");
            }
            return Task.CompletedTask;
        }

        private Task OnHubReconnected(string? connectionId)
        {
            if (_disposed) return Task.CompletedTask;
            try
            {
                UpdateConnectionState("实时已连接", true);
                LogHelper.Info($"SignalR连接状态：已连接，ConnectionId: {connectionId}");
            }
            catch (Exception e)
            {
                LogHelper.Error(e, "OnHubReconnected 处理失败");
            }
            return Task.CompletedTask;
        }

        private Task OnHubClosed(Exception? ex)
        {
            if (_disposed) return Task.CompletedTask;
            try
            {
                UpdateConnectionState("实时已断开", false);
                if (ex != null)
                {
                    LogHelper.Error(ex, "SignalR连接异常关闭");
                }
                else
                {
                    LogHelper.Info("SignalR连接已正常关闭");
                }
            }
            catch (Exception e)
            {
                LogHelper.Error(e, "OnHubClosed 处理失败");
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// UI线程安全更新连接状态
        /// </summary>
        private void UpdateConnectionState(string state, bool isConnected)
        {
            if (_uiDispatcher.HasShutdownStarted) return;
            _uiDispatcher.Invoke(() =>
            {
                ConnectionState = state;
                IsSignalRConnected = isConnected;
            });
        }
        #endregion

        #region 事件总线处理（修复版：双事件订阅）
        /// <summary>
        /// 节点状态变更事件处理（本地事件兜底）
        /// </summary>
        private void OnNodeStatusChanged(object? sender, NodeStatusChangedEventArgs args)
        {
            if (_disposed || _uiDispatcher.HasShutdownStarted || args.NodeInfo == null) return;
            try
            {
                _uiDispatcher.Invoke(() =>
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
                });
            }
            catch (Exception ex)
            {
                LogHelper.Error(ex, "UI线程更新节点信息失败");
            }
        }

        /// <summary>
        /// 【修复】任务进度更新事件处理（本地事件兜底）
        /// </summary>
        private void OnTaskProgressUpdated(object? sender, TaskProgressUpdatedEventArgs args)
        {
            if (_disposed || _uiDispatcher.HasShutdownStarted) return;
            try
            {
                _uiDispatcher.Invoke(() =>
                {
                    var existingTask = Tasks.FirstOrDefault(t => t.TaskId == args.TaskId);
                    if (existingTask != null)
                    {
                        existingTask.TaskName = args.TaskName;
                        existingTask.Status = args.CurrentStatus;
                        existingTask.CompletedPages = Math.Max(0, args.CrawledPages);
                        existingTask.TotalPages = Math.Max(0, args.TotalPages);
                        existingTask.AssignedNode = args.AssignedNodeId;
                    }
                    else
                    {
                        Tasks.Add(new TaskDisplayInfo
                        {
                            TaskId = args.TaskId,
                            TaskName = args.TaskName,
                            Status = args.CurrentStatus,
                            CompletedPages = Math.Max(0, args.CrawledPages),
                            TotalPages = Math.Max(0, args.TotalPages),
                            AssignedNode = args.AssignedNodeId
                        });
                    }
                });
            }
            catch (Exception ex)
            {
                LogHelper.Error(ex, "UI线程更新任务进度失败");
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
                CpuUsage = Math.Max(0, nodeInfo.CpuUsage),
                MemoryUsageMb = Math.Max(0, nodeInfo.MemoryUsageMb),
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
            displayInfo.CpuUsage = Math.Max(0, nodeInfo.CpuUsage);
            displayInfo.MemoryUsageMb = Math.Max(0, nodeInfo.MemoryUsageMb);
            displayInfo.LastHeartbeat = nodeInfo.LastHeartbeat;
        }
        #endregion

        #region 公共方法
        public async Task LoadNodesAsync()
        {
            if (_disposed) return;
            try
            {
                LogHelper.Info("开始加载节点列表");

                using var scope = _serviceProvider.CreateScope();
                var nodeService = scope.ServiceProvider.GetRequiredService<INodeManagementService>();
                var activeNodes = await nodeService.GetActiveNodesAsync();

                if (!_uiDispatcher.HasShutdownStarted)
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
            if (_disposed) return;
            try
            {
                LogHelper.Info("=== 手动触发测试任务下发 ===");

                var testTask = new CrawlerTask
                {
                    TaskName = $"手动测试-{DateTime.Now:HHmmss}",
                    PluginName = "MockTestPlugin",
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

        #region 资源释放（修复版：IAsyncDisposable+全异步释放）
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            // 1. 取消事件订阅
            if (_nodeStatusSubscriptionId != Guid.Empty)
            {
                try
                {
                    _eventAggregator.Unsubscribe<NodeStatusChangedEventArgs>(_nodeStatusSubscriptionId);
                    _nodeStatusSubscriptionId = Guid.Empty;
                    LogHelper.Info("已取消节点状态变更事件订阅");
                }
                catch (Exception ex)
                {
                    LogHelper.Error(ex, "取消节点状态变更事件订阅失败");
                }
            }

            if (_taskProgressSubscriptionId != Guid.Empty)
            {
                try
                {
                    _eventAggregator.Unsubscribe<TaskProgressUpdatedEventArgs>(_taskProgressSubscriptionId);
                    _taskProgressSubscriptionId = Guid.Empty;
                    LogHelper.Info("已取消任务进度更新事件订阅");
                }
                catch (Exception ex)
                {
                    LogHelper.Error(ex, "取消任务进度更新事件订阅失败");
                }
            }

            // 2. 释放SignalR资源（全异步，无死锁）
            if (_hubConnection != null)
            {
                try
                {
                    _hubConnection.Reconnecting -= OnHubReconnecting;
                    _hubConnection.Reconnected -= OnHubReconnected;
                    _hubConnection.Closed -= OnHubClosed;

                    await _hubConnection.StopAsync();
                    await _hubConnection.DisposeAsync();
                    _hubConnection = null;
                    LogHelper.Info("SignalR客户端资源已释放");
                }
                catch (Exception ex)
                {
                    LogHelper.Error(ex, "释放SignalR客户端资源失败");
                }
            }

            GC.SuppressFinalize(this);
        }

        public void Dispose()
        {
            // 同步Dispose仅调用异步Dispose，不等待
            _ = DisposeAsync().AsTask();
        }

        ~MainWindowViewModel()
        {
            Dispose();
        }
        #endregion
    }
}