using CrawlerCore.Entities;
using CrawlerCore.Events;
using CrawlerCore.Infrastructure;
using CrawlerCore.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Serilog;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Timer = System.Timers.Timer;

namespace CrawlerMaster.WPF.Services
{
    /// <summary>
    /// 节点健康检查后台服务（定时将超时节点标记为离线）
    /// </summary>
    public class NodeHealthCheckService : IDisposable
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IEventAggregator _eventAggregator;
        private readonly Timer _timer;
        private readonly TimeSpan _checkInterval;
        private readonly TimeSpan _timeoutThreshold;
        private readonly ICrawlerCoordinatorService? _coordinator;
        private Guid _nodeStatusSubscriptionId = Guid.Empty; // 保存订阅ID
        private bool _disposed = false;
        // 上一次检查尚未结束时跳过本次，避免检查重叠执行
        private int _checkRunning;

        // 直接注入Singleton的EventAggregator和根ServiceProvider
        // 检查间隔与超时阈值来自 HealthCheckSettings（默认 15 秒 / 60 秒），未配置时使用默认值
        public NodeHealthCheckService(
            IServiceProvider serviceProvider,
            IEventAggregator eventAggregator,
            IOptions<HealthCheckSettings>? healthCheckOptions = null,
            ICrawlerCoordinatorService? coordinator = null)
        {
            _serviceProvider = serviceProvider;
            _eventAggregator = eventAggregator;
            _coordinator = coordinator;
            var settings = healthCheckOptions?.Value ?? new HealthCheckSettings();
            _checkInterval = TimeSpan.FromSeconds(settings.CheckIntervalSeconds > 0 ? settings.CheckIntervalSeconds : 15);
            _timeoutThreshold = TimeSpan.FromSeconds(settings.NodeTimeoutSeconds > 0 ? settings.NodeTimeoutSeconds : 60);
            _timer = new Timer(_checkInterval.TotalMilliseconds);
            _timer.Elapsed += OnTimerElapsed;
            _timer.AutoReset = true;
        }

        public void Start()
        {
            // 订阅Singleton的EventAggregator，保存订阅ID
            _nodeStatusSubscriptionId = _eventAggregator.Subscribe<NodeStatusChangedEventArgs>(OnNodeStatusChanged);

            _timer.Start();
            Log.Information("节点健康检查服务已启动，检查间隔 {Interval}，超时阈值 {Timeout}",
                _checkInterval, _timeoutThreshold);
        }

        public void Stop()
        {
            _timer.Stop();

            // 使用订阅ID取消订阅
            if (_nodeStatusSubscriptionId != Guid.Empty)
            {
                _eventAggregator.Unsubscribe<NodeStatusChangedEventArgs>(_nodeStatusSubscriptionId);
                _nodeStatusSubscriptionId = Guid.Empty;
            }

            Log.Information("节点健康检查服务已停止");
        }

        // 事件处理程序
        private void OnNodeStatusChanged(object? sender, NodeStatusChangedEventArgs args)
        {
            // 结构化日志记录所有状态变更
            if (args.OldStatus != args.NewStatus)
            {
                Log.Information("节点状态变更 | NodeId:{NodeId} 旧状态:{OldStatus} 新状态:{NewStatus} 时间:{Time}",
                    args.NodeId, args.OldStatus, args.NewStatus, args.ChangedTime);
            }

            // 离线状态打印警告日志
            if (args.NewStatus == NodeStatus.Offline)
            {
                Log.Warning("节点离线告警 | NodeId:{NodeId} 机器名:{MachineName} IP:{IpAddress}",
                    args.NodeId, args.NodeInfo.MachineName, args.NodeInfo.IpAddress);
            }
        }

        private async void OnTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            if (_disposed) return;
            if (Interlocked.Exchange(ref _checkRunning, 1) == 1) return;

            try
            {
                await CheckOnceAsync();
            }
            catch (Exception ex)
            {
                // 退出时容器可能已释放（ObjectDisposedException），这里一并捕获，不让异常逃出 async void
                Log.Error(ex, "健康检查执行过程中发生异常");
            }
            finally
            {
                Interlocked.Exchange(ref _checkRunning, 0);
            }
        }

        /// <summary>
        /// 执行一次检查：把超时节点标记为离线，并断开它们的双向流。
        /// 失联节点的流在 Master 侧不会自己结束（收不到 RST），不断开的话节点恢复后新流和重新注册都会被拒绝
        /// </summary>
        public async Task CheckOnceAsync()
        {
            // 每次检查创建新的Scoped Scope
            using var scope = _serviceProvider.CreateScope();
            var nodeService = scope.ServiceProvider.GetRequiredService<INodeManagementService>();

            var offlineNodeIds = await nodeService.MarkInactiveNodesAsOfflineAsync(_timeoutThreshold);
            if (offlineNodeIds.Count == 0) return;

            Log.Information("健康检查：已将 {Count} 个超时节点标记为离线: {NodeIds}", offlineNodeIds.Count, offlineNodeIds);
            foreach (var nodeId in offlineNodeIds)
            {
                _coordinator?.DisconnectNode(nodeId);
            }
        }

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
                Stop();
                _timer?.Dispose();
            }

            _disposed = true;
        }

        ~NodeHealthCheckService()
        {
            Dispose(false);
        }
    }
}