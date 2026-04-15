using CrawlerCore.Entities;
using CrawlerCore.Events;
using CrawlerCore.Infrastructure;
using CrawlerMaster.WPF.Models;
using CrawlerMaster.WPF.Services.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using TaskStatus = CrawlerCore.Entities.TaskStatus;

namespace CrawlerMaster.WPF.Services
{
    /// <summary>
    /// 实时数据同步中枢服务（最终零错误版本）
    /// </summary>
    public class RealtimeDataSyncService : BackgroundService
    {
        #region 私有字段
        private readonly IHubContext<CrawlerHub, ICrawlerHubClient> _hubContext;
        private readonly IEventAggregator _eventAggregator;
        private readonly SignalRSettings _signalRSettings;
        private readonly ILogger<RealtimeDataSyncService> _logger;

        private readonly Channel<Func<IHubContext<CrawlerHub, ICrawlerHubClient>, Task>> _pushChannel;
        private readonly ConcurrentDictionary<string, DateTime> _nodeLastPushTime = new();
        private readonly ConcurrentDictionary<string, DateTime> _taskLastPushTime = new();

        // 【修复】订阅状态锁，避免重复订阅
        private readonly object _subscriptionLock = new();
        private bool _isSubscribed = false;
        private Guid _nodeStatusSubscriptionId;
        private Guid _taskProgressSubscriptionId;
        private bool _isStopped = false;
        #endregion

        #region 构造函数
        public RealtimeDataSyncService(
            IHubContext<CrawlerHub, ICrawlerHubClient> hubContext,
            IEventAggregator eventAggregator,
            IOptions<SignalRSettings> signalRSettingsOptions,
            ILogger<RealtimeDataSyncService> logger)
        {
            _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
            _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
            _signalRSettings = signalRSettingsOptions?.Value ?? throw new ArgumentNullException(nameof(signalRSettingsOptions));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _pushChannel = Channel.CreateUnbounded<Func<IHubContext<CrawlerHub, ICrawlerHubClient>, Task>>(
                new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false
                });
        }
        #endregion

        #region 托管服务生命周期（修复重复订阅+异常隔离）
        public override Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("实时数据同步服务启动");
            _isStopped = false;

            // 【修复】锁+状态判断，避免重复订阅
            lock (_subscriptionLock)
            {
                if (_isSubscribed)
                {
                    _logger.LogWarning("服务已订阅事件，跳过重复订阅");
                    return base.StartAsync(cancellationToken);
                }

                _nodeStatusSubscriptionId = _eventAggregator.Subscribe<NodeStatusChangedEventArgs>(OnNodeStatusChanged);
                _taskProgressSubscriptionId = _eventAggregator.Subscribe<TaskProgressUpdatedEventArgs>(OnTaskProgressUpdated);
                _isSubscribed = true;
            }

            return base.StartAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("实时推送循环已启动");

            try
            {
                await foreach (var pushDelegate in _pushChannel.Reader.ReadAllAsync(stoppingToken))
                {
                    if (stoppingToken.IsCancellationRequested || _isStopped) break;

                    try
                    {
                        await pushDelegate(_hubContext);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "推送委托执行异常");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("实时推送循环因服务停止已取消");
            }
            catch (ChannelClosedException)
            {
                _logger.LogInformation("推送通道已关闭，循环终止");
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "实时推送循环发生致命异常");
                throw;
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("实时数据同步服务停止中");
            _isStopped = true;

            // 取消事件订阅，异常隔离
            lock (_subscriptionLock)
            {
                if (_isSubscribed)
                {
                    try
                    {
                        _eventAggregator.Unsubscribe<NodeStatusChangedEventArgs>(_nodeStatusSubscriptionId);
                        _eventAggregator.Unsubscribe<TaskProgressUpdatedEventArgs>(_taskProgressSubscriptionId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "事件订阅取消异常");
                    }
                    _isSubscribed = false;
                }
            }

            // 关闭通道，超时强制退出
            _pushChannel.Writer.TryComplete();
            try
            {
                await _pushChannel.Reader.Completion.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("推送队列等待超时，强制关闭");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "通道关闭异常");
            }

            // 全量清理内存
            _nodeLastPushTime.Clear();
            _taskLastPushTime.Clear();

            await base.StopAsync(cancellationToken);
            _logger.LogInformation("实时数据同步服务已完全停止");
        }

        public override void Dispose()
        {
            _pushChannel.Writer.TryComplete();
            _nodeLastPushTime.Clear();
            _taskLastPushTime.Clear();
            base.Dispose();
            GC.SuppressFinalize(this);
        }
        #endregion

        #region 事件处理方法（修复空校验+Channel异常隔离）
        private void OnNodeStatusChanged(object? sender, NodeStatusChangedEventArgs args)
        {
            if (_isStopped || args == null || string.IsNullOrWhiteSpace(args.NodeId) || args.NodeInfo == null)
            {
                _logger.LogWarning("收到无效的节点状态变更事件，已跳过");
                return;
            }

            try
            {
                var now = DateTime.UtcNow;
                var throttleInterval = TimeSpan.FromSeconds(_signalRSettings.NodeUpdateThrottleSeconds);
                var lastPushTime = _nodeLastPushTime.GetOrAdd(args.NodeId, DateTime.MinValue);

                if (now - lastPushTime < throttleInterval)
                {
                    _logger.LogDebug($"节点 {args.NodeId} 推送未达节流间隔，跳过写入通道");
                    return;
                }

                var pushDelegate = BuildNodePushDelegate(args);
                // 【修复】ChannelClosedException捕获
                if (!_pushChannel.Writer.TryWrite(pushDelegate))
                {
                    _logger.LogWarning($"节点 {args.NodeId} 状态推送写入通道失败");
                }
            }
            catch (ChannelClosedException)
            {
                _logger.LogWarning("推送通道已关闭，跳过节点状态推送");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"节点 {args.NodeId} 状态事件处理异常");
            }
        }

        private void OnTaskProgressUpdated(object? sender, TaskProgressUpdatedEventArgs args)
        {
            if (_isStopped || args == null || string.IsNullOrWhiteSpace(args.TaskId))
            {
                _logger.LogWarning("收到无效的任务进度更新事件，已跳过");
                return;
            }

            try
            {
                var now = DateTime.UtcNow;
                var throttleInterval = TimeSpan.FromSeconds(_signalRSettings.TaskProgressThrottleSeconds);
                var lastPushTime = _taskLastPushTime.GetOrAdd(args.TaskId, DateTime.MinValue);

                if (now - lastPushTime < throttleInterval)
                {
                    _logger.LogDebug($"任务 {args.TaskId} 推送未达节流间隔，跳过写入通道");
                    return;
                }

                var pushDelegate = BuildTaskPushDelegate(args);
                if (!_pushChannel.Writer.TryWrite(pushDelegate))
                {
                    _logger.LogWarning($"任务 {args.TaskId} 进度推送写入通道失败");
                }
            }
            catch (ChannelClosedException)
            {
                _logger.LogWarning("推送通道已关闭，跳过任务进度推送");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"任务 {args.TaskId} 进度事件处理异常");
            }
        }
        #endregion

        #region 推送委托构建（线程安全+数值兜底）
        private Func<IHubContext<CrawlerHub, ICrawlerHubClient>, Task> BuildNodePushDelegate(NodeStatusChangedEventArgs args)
        {
            return async (hubContext) =>
            {
                var now = DateTime.UtcNow;
                var throttleInterval = TimeSpan.FromSeconds(_signalRSettings.NodeUpdateThrottleSeconds);
                var shouldPush = false;

                // 原子操作更新推送时间，线程安全
                _nodeLastPushTime.AddOrUpdate(
                    args.NodeId,
                    now,
                    (key, oldValue) =>
                    {
                        if (now - oldValue >= throttleInterval)
                        {
                            shouldPush = true;
                            return now;
                        }
                        shouldPush = false;
                        return oldValue;
                    });

                if (!shouldPush) return;

                // 【修复】数值兜底，避免负数导致UI异常
                var message = new NodeUpdateMessage
                {
                    NodeId = args.NodeId,
                    MachineName = args.NodeInfo.MachineName ?? string.Empty,
                    IpAddress = args.NodeInfo.IpAddress ?? string.Empty,
                    Status = args.NewStatus,
                    CpuUsage = Math.Max(0, args.NodeInfo.CpuUsage),
                    MemoryUsageMb = Math.Max(0, args.NodeInfo.MemoryUsageMb),
                    ActiveTasks = Math.Max(0, args.NodeInfo.ActiveTasks),
                    LastHeartbeat = args.NodeInfo.LastHeartbeat
                };

                await hubContext.Clients.All.OnNodeUpdated(message);
                _logger.LogInformation($"节点 {args.NodeId} 状态已推送至前端，新状态：{args.NewStatus}");
            };
        }

        private Func<IHubContext<CrawlerHub, ICrawlerHubClient>, Task> BuildTaskPushDelegate(TaskProgressUpdatedEventArgs args)
        {
            return async (hubContext) =>
            {
                var now = DateTime.UtcNow;
                var throttleInterval = TimeSpan.FromSeconds(_signalRSettings.TaskProgressThrottleSeconds);
                var shouldPush = false;

                _taskLastPushTime.AddOrUpdate(
                    args.TaskId,
                    now,
                    (key, oldValue) =>
                    {
                        if (now - oldValue >= throttleInterval)
                        {
                            shouldPush = true;
                            return now;
                        }
                        shouldPush = false;
                        return oldValue;
                    });

                if (!shouldPush) return;

                var message = new TaskProgressMessage
                {
                    TaskId = args.TaskId,
                    TaskName = args.TaskName,
                    Status = args.CurrentStatus,
                    CompletedPages = args.CrawledPages,
                    TotalPages = args.TotalPages,
                    AssignedNode = args.AssignedNodeId
                };

                await hubContext.Clients.All.OnTaskProgressUpdated(message);
                _logger.LogInformation($"任务 {args.TaskId} 进度已推送至前端，进度：{message.ProgressPercent}%");

                // 任务结束清理内存
                if (args.CurrentStatus is TaskStatus.Completed or TaskStatus.Failed)
                {
                    _taskLastPushTime.TryRemove(args.TaskId, out _);
                    _logger.LogDebug($"任务 {args.TaskId} 已结束，清理节流记录");
                }
            };
        }
        #endregion
    }
}