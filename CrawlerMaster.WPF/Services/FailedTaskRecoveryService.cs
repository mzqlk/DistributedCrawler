using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CrawlerCore.Entities;
using CrawlerCore.Events;
using CrawlerCore.Interfaces;
using CrawlerCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CrawlerMaster.WPF.Services
{
    /// <summary>
    /// 故障任务恢复服务：监听节点离线事件，把分配给该节点、尚未结束（Pending 或 Running）的任务重置为 Pending 并重新入队。
    /// 同一节点的离线事件串行处理（等待而不是跳过）；重置只处理仍分配给该节点的任务，本身幂等，重复事件不会重复入队
    /// </summary>
    public class FailedTaskRecoveryService : IDisposable
    {
        #region 私有字段
        private readonly IServiceProvider _serviceProvider;
        private readonly IEventAggregator _eventAggregator;
        private readonly ITaskSchedulerService _taskSchedulerService;

        // 故障任务ID内存队列（供外部查看最近被恢复的任务）
        private readonly ConcurrentQueue<string> _failedTaskQueue = new();
        // 每个节点一把锁：同一节点的离线事件依次处理
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _nodeLocks = new();
        private readonly object _disposeLock = new();

        // 事件订阅ID：用于安全取消订阅
        private Guid _eventSubscriptionId;
        // 释放标记：volatile解决多线程内存屏障问题
        private volatile bool _disposed = false;
        #endregion

        #region 构造函数
        public FailedTaskRecoveryService(
            IServiceProvider serviceProvider,
            IEventAggregator eventAggregator,
            ITaskSchedulerService taskSchedulerService)
        {
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
            _taskSchedulerService = taskSchedulerService ?? throw new ArgumentNullException(nameof(taskSchedulerService));

            _eventSubscriptionId = _eventAggregator.Subscribe<NodeStatusChangedEventArgs>(OnNodeStatusChanged);

            LogHelper.Info("故障任务恢复服务初始化完成，节点离线事件订阅成功");
        }
        #endregion

        #region 公开属性
        /// <summary>
        /// 故障任务ID内存队列，对外只读线程安全访问
        /// </summary>
        public IReadOnlyCollection<string> FailedTaskQueue => _failedTaskQueue.ToList();
        #endregion

        #region 核心事件处理逻辑
        /// <summary>
        /// 节点状态变更事件处理程序（事件处理器，内部捕获全部异常）
        /// </summary>
        private async void OnNodeStatusChanged(object? sender, NodeStatusChangedEventArgs args)
        {
            if (_disposed || args.NewStatus != NodeStatus.Offline) return;

            var nodeId = args.NodeId;
            if (string.IsNullOrWhiteSpace(nodeId))
            {
                LogHelper.Warn("节点离线事件处理失败，节点ID为空");
                return;
            }

            try
            {
                await RecoverNodeTasksAsync(nodeId, args.NodeInfo).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 防止事件处理异常导致进程崩溃
                LogHelper.Error(ex, $"节点 {nodeId} 故障任务恢复流程发生未捕获异常");
            }
        }

        /// <summary>
        /// 恢复一个离线节点上的任务：重置（事务内）→ 通知界面 → 按原优先级重新入队
        /// </summary>
        private async Task RecoverNodeTasksAsync(string nodeId, NodeInfo? nodeInfo)
        {
            var gate = _nodeLocks.GetOrAdd(nodeId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed) return;

                LogHelper.Info($"检测到节点离线，启动故障任务恢复流程 | NodeId:{nodeId} 机器名:{nodeInfo?.MachineName} IP:{nodeInfo?.IpAddress}");

                List<CrawlerTask> resetTasks;
                using (var scope = _serviceProvider.CreateScope())
                {
                    var taskDataService = scope.ServiceProvider.GetRequiredService<ITaskDataService>();
                    resetTasks = await taskDataService.ResetUnfinishedTasksOfNodeAsync(nodeId).ConfigureAwait(false);
                }

                if (resetTasks.Count == 0)
                {
                    LogHelper.Info($"节点 {nodeId} 没有未结束的任务，无需执行故障恢复");
                    return;
                }

                LogHelper.Info($"节点 {nodeId} 成功重置 {resetTasks.Count} 个任务");

                int requeuedCount = 0;
                foreach (var task in resetTasks)
                {
                    _failedTaskQueue.Enqueue(task.TaskId);
                    PublishTaskReset(task);

                    if (await _taskSchedulerService.ScheduleTaskAsync(task).ConfigureAwait(false))
                    {
                        requeuedCount++;
                        LogHelper.Info($"故障任务重新入队成功 | TaskId:{task.TaskId} 优先级:{task.Priority}");
                    }
                    else
                    {
                        // 重置之后、入队之前任务可能已被重连的节点认领或已完成，此时不应再入队
                        LogHelper.Warn($"故障任务未重新入队（任务状态已变化或入队失败） | TaskId:{task.TaskId}");
                    }
                }

                LogHelper.Info($"节点 {nodeId} 故障任务恢复流程完成 | 成功入队 {requeuedCount}/{resetTasks.Count} 个任务");
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>
        /// 任务被重置为 Pending 后通知界面（任务列表据此显示“待执行”，而不是一直停在“运行中”）
        /// </summary>
        private void PublishTaskReset(CrawlerTask task)
        {
            try
            {
                _eventAggregator.Publish(this, new TaskProgressUpdatedEventArgs(
                    task.TaskId, task.TaskName, task.Status, 0, task.MaxPages, string.Empty));
            }
            catch (Exception ex)
            {
                LogHelper.Error(ex, $"发布任务 {task.TaskId} 重置事件失败");
            }
        }
        #endregion

        #region 公开辅助方法
        /// <summary>
        /// 线程安全地从故障队列中取出一个任务ID
        /// </summary>
        /// <param name="taskId">取出的任务ID</param>
        /// <returns>是否取出成功</returns>
        public bool TryDequeueFailedTask(out string taskId)
        {
            return _failedTaskQueue.TryDequeue(out taskId!);
        }

        /// <summary>
        /// 线程安全地清空故障队列
        /// </summary>
        public void ClearFailedTaskQueue()
        {
            while (_failedTaskQueue.TryDequeue(out _)) { }
            LogHelper.Info("故障任务队列已清空");
        }
        #endregion

        #region IDisposable 标准实现
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            // 双重检查锁定，避免多线程下重复释放
            if (_disposed) return;
            lock (_disposeLock)
            {
                if (_disposed) return;

                if (disposing)
                {
                    if (_eventSubscriptionId != Guid.Empty)
                    {
                        _eventAggregator.Unsubscribe<NodeStatusChangedEventArgs>(_eventSubscriptionId);
                        _eventSubscriptionId = Guid.Empty;
                        LogHelper.Info("故障任务恢复服务已取消节点状态事件订阅");
                    }

                    ClearFailedTaskQueue();
                }

                _disposed = true;
            }
        }
        #endregion
    }
}
