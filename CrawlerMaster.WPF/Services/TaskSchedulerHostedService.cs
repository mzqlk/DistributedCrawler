using CrawlerCore.Entities;
using CrawlerCore.Events;
using CrawlerCore.Interfaces;
using CrawlerCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TaskStatus = CrawlerCore.Entities.TaskStatus;

namespace CrawlerMaster.WPF.Services
{
    /// <summary>
    /// 一次分配尝试的结论
    /// </summary>
    public enum TaskAssignmentOutcome
    {
        /// <summary>已写库（Running + 节点）并放入该节点的发送通道</summary>
        Assigned,
        /// <summary>没有可用节点（无在线且已连接的节点，或都已达并发上限），稍后重试</summary>
        NoAvailableNode,
        /// <summary>候选节点都下发失败（流刚好断开），稍后重试</summary>
        SendFailed,
        /// <summary>任务不存在或已不是 Pending（已被认领、已完成、已删除），不再调度</summary>
        NotDispatchable
    }

    /// <summary>
    /// 分配结果
    /// </summary>
    public sealed record TaskAssignmentResult(TaskAssignmentOutcome Outcome, string? NodeId = null);

    /// <summary>
    /// 优先级任务调度后台服务（IHostedService，完美解决DI生命周期）
    /// 优先级规则：数值越大优先级越高（1最低，5最高）
    /// 相同优先级按入队顺序（FIFO）出队
    /// </summary>
    public class TaskSchedulerHostedService : BackgroundService, ITaskSchedulerService
    {
        // 线程安全优先级队列：使用元组 (Primary, Secondary) 实现大值优先 + FIFO
        // Primary = int.MaxValue - priority （值越小优先级越高）
        // Secondary = 入队序号（值越小越早入队）
        private readonly PriorityQueue<CrawlerTask, (int Primary, long Secondary)> _taskQueue = new();
        private readonly object _queueLock = new();
        private int _queueCount = 0;
        private long _enqueueCounter = 0;

        // 服务范围工厂（本服务是单例，Scoped 的 ITaskDataService 必须通过作用域获取）
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILoadBalancingScheduler _loadBalancer;
        private readonly IEventAggregator _eventAggregator;

        /// <summary>
        /// 获取当前队列中的任务数量
        /// </summary>
        public int QueueCount
        {
            get
            {
                lock (_queueLock)
                {
                    return _queueCount;
                }
            }
        }

        public TaskSchedulerHostedService(IServiceScopeFactory scopeFactory, ILoadBalancingScheduler loadBalancer, IEventAggregator eventAggregator)
        {
            _scopeFactory = scopeFactory;
            _loadBalancer = loadBalancer;
            _eventAggregator = eventAggregator;
            LogHelper.Info("任务调度后台服务已初始化");
        }

        /// <summary>
        /// 任务入队：新任务先持久化；已存在的任务必须仍为 Pending（故障恢复、重启恢复已在库中重置），否则不入队
        /// </summary>
        /// <param name="task">爬虫任务</param>
        /// <returns>是否入队</returns>
        public async Task<bool> ScheduleTaskAsync(CrawlerTask task)
        {
            // 空值校验
            if (task == null)
            {
                LogHelper.Error("任务入队失败：任务实体为空");
                return false;
            }

            // 优先级范围校验（1-5）
            if (task.Priority < 1 || task.Priority > 5)
            {
                LogHelper.Error($"任务入队失败：优先级{task.Priority}超出范围[1-5]，TaskId={task.TaskId}");
                return false;
            }

            // 使用 Scope 访问 Scoped 服务
            using var scope = _scopeFactory.CreateScope();
            var taskDataService = scope.ServiceProvider.GetRequiredService<ITaskDataService>();

            try
            {
                var existingTask = await taskDataService.GetTaskByIdAsync(task.TaskId);
                if (existingTask != null)
                {
                    // 已存在：只确认仍为 Pending 并清空分配节点。不能整体覆盖库中记录，
                    // 否则重置到入队之间被节点认领或已完成的任务会被改回 Pending
                    if (!await taskDataService.ClearAssignmentIfPendingAsync(task.TaskId))
                    {
                        LogHelper.Info($"任务 {task.TaskId} 当前为 {existingTask.Status}，不再入队");
                        return false;
                    }
                    LogHelper.Info($"已存在任务重新入队：TaskId={task.TaskId}");
                }
                else
                {
                    await taskDataService.AddTaskAsync(task);
                    LogHelper.Info($"新任务已持久化：TaskId={task.TaskId}");
                }
            }
            catch (Exception ex)
            {
                LogHelper.Error(ex, $"任务入队失败：数据库保存异常，TaskId={task.TaskId}");
                return false;
            }

            Enqueue(task);
            LogHelper.Info($"任务入队成功：TaskId={task.TaskId}, TaskName={task.TaskName}, Priority={task.Priority}, 当前队列数={QueueCount}");
            return true;
        }

        /// <summary>
        /// 非阻塞出队：获取最高优先级任务（优先级相同则按入队顺序）
        /// </summary>
        /// <param name="task">出队任务（空则为null）</param>
        /// <returns>是否出队成功</returns>
        public bool TryDequeue(out CrawlerTask? task)
        {
            lock (_queueLock)
            {
                var result = _taskQueue.TryDequeue(out task, out _);
                if (result)
                {
                    _queueCount--;
                    LogHelper.Info($"任务出队成功：TaskId={task!.TaskId}, Priority={task.Priority}, 剩余队列数={_queueCount}");
                }
                return result;
            }
        }

        /// <summary>
        /// 分配任务到最优节点
        /// </summary>
        /// <returns>分配到的节点ID；未分配时为 null</returns>
        public async Task<string?> AssignTaskToOptimalNodeAsync(string taskId)
        {
            var result = await TryAssignTaskAsync(taskId).ConfigureAwait(false);
            return result.Outcome == TaskAssignmentOutcome.Assigned ? result.NodeId : null;
        }

        /// <summary>
        /// 尝试分配一个任务：
        /// 1. 任务必须仍为 Pending；
        /// 2. 候选节点为库中 Online 且当前有活跃流的节点，附带各自已分配的未结束任务数与并发上限；
        /// 3. 先把任务写为“Running + 节点”，再放入该节点的发送通道（Worker 之后的进度一定晚于这次写库）；
        ///    下发失败则撤销写库，并在本次尝试中排除该节点，换下一个候选节点。
        /// </summary>
        public async Task<TaskAssignmentResult> TryAssignTaskAsync(string taskId)
        {
            LogHelper.Info($"开始分配并下发任务：TaskId={taskId}");

            using var scope = _scopeFactory.CreateScope();
            var nodeManager = scope.ServiceProvider.GetRequiredService<INodeManagementService>();
            var taskDataService = scope.ServiceProvider.GetRequiredService<ITaskDataService>();
            var coordinatorService = scope.ServiceProvider.GetRequiredService<ICrawlerCoordinatorService>();

            var task = await taskDataService.GetTaskByIdAsync(taskId).ConfigureAwait(false);
            if (task == null || task.Status != TaskStatus.Pending)
            {
                LogHelper.Info($"任务 {taskId} 不存在或已不是 Pending（当前 {task?.Status.ToString() ?? "不存在"}），不再调度");
                return new TaskAssignmentResult(TaskAssignmentOutcome.NotDispatchable);
            }

            var activeNodes = await nodeManager.GetActiveNodesAsync().ConfigureAwait(false);
            var assignedCounts = await taskDataService.GetUnfinishedTaskCountsByNodeAsync().ConfigureAwait(false);
            var excludedNodes = new HashSet<string>();
            var sendFailed = false;

            while (true)
            {
                var candidates = activeNodes
                    .Where(n => !excludedNodes.Contains(n.NodeId) && coordinatorService.IsNodeConnected(n.NodeId))
                    .Select(n => new NodeCandidate(
                        n,
                        assignedCounts.TryGetValue(n.NodeId, out var count) ? count : 0,
                        coordinatorService.GetMaxConcurrentTasks(n.NodeId)))
                    .ToList();

                var optimalNode = candidates.Count == 0 ? null : _loadBalancer.SelectOptimalNode(candidates, task);
                if (optimalNode == null)
                {
                    if (!sendFailed)
                    {
                        LogHelper.Warn($"任务分配失败：没有可用节点，TaskId={taskId}");
                    }
                    return new TaskAssignmentResult(sendFailed ? TaskAssignmentOutcome.SendFailed : TaskAssignmentOutcome.NoAvailableNode);
                }

                var nodeId = optimalNode.NodeId;
                if (!await taskDataService.TryMarkTaskDispatchedAsync(taskId, nodeId).ConfigureAwait(false))
                {
                    LogHelper.Info($"任务 {taskId} 在分配期间状态已变化，不再调度");
                    return new TaskAssignmentResult(TaskAssignmentOutcome.NotDispatchable);
                }

                task.Status = TaskStatus.Running;
                task.AssignedNode = nodeId;
                // 先发布再下发：界面上的 Running 一定早于 Worker 发来的进度
                PublishTaskStatus(task);

                if (await coordinatorService.SendTaskToNodeAsync(nodeId, task).ConfigureAwait(false))
                {
                    LogHelper.Info($"任务下发成功：TaskId={taskId}, NodeId={nodeId}");
                    return new TaskAssignmentResult(TaskAssignmentOutcome.Assigned, nodeId);
                }

                LogHelper.Warn($"任务下发失败（节点 {nodeId} 的流已断开），撤销分配并尝试其他节点：TaskId={taskId}");
                if (await taskDataService.RevertTaskDispatchAsync(taskId, nodeId).ConfigureAwait(false))
                {
                    task.Status = TaskStatus.Pending;
                    task.AssignedNode = string.Empty;
                    PublishTaskStatus(task);
                }
                excludedNodes.Add(nodeId);
                sendFailed = true;
            }
        }

        /// <summary>
        /// 后台调度循环（由 IHostedService 统一管理生命周期）
        /// </summary>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            LogHelper.Info("任务调度后台循环已启动");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (!TryDequeue(out var task))
                    {
                        // 队列为空，延迟100ms再检查
                        await Task.Delay(100, stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    TaskAssignmentResult result;
                    try
                    {
                        result = await TryAssignTaskAsync(task!.TaskId).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // 例如 SQLite 繁忙超时：任务已出队，必须重新入队，否则要到 Master 重启才会恢复
                        LogHelper.Error(ex, $"分配任务 {task!.TaskId} 时发生异常，稍后重试");
                        result = new TaskAssignmentResult(TaskAssignmentOutcome.SendFailed);
                    }

                    if (result.Outcome is TaskAssignmentOutcome.NoAvailableNode or TaskAssignmentOutcome.SendFailed)
                    {
                        await Task.Delay(1000, stoppingToken).ConfigureAwait(false);
                        Enqueue(task);
                        LogHelper.Warn($"任务重新入队：TaskId={task.TaskId}, Priority={task.Priority}");
                    }
                }
                catch (OperationCanceledException)
                {
                    LogHelper.Info("任务调度后台循环已取消");
                    break;
                }
                catch (Exception ex)
                {
                    LogHelper.Error(ex, "任务调度后台循环发生异常");
                    await Task.Delay(1000, stoppingToken).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Master 启动时从数据库恢复任务：所有 Pending 任务（不论是否已分配），以及执行节点已不在线的 Running 任务，
        /// 在库中重置为“Pending + 未分配”后按创建时间入队
        /// </summary>
        public async Task RecoverPendingTasksFromDatabaseAsync()
        {
            try
            {
                LogHelper.Info("开始执行Master重启任务恢复");

                List<CrawlerTask> tasks;
                using (var scope = _scopeFactory.CreateScope())
                {
                    tasks = await scope.ServiceProvider.GetRequiredService<ITaskDataService>().ResetTasksForStartupRecoveryAsync();
                }

                if (tasks.Count == 0)
                {
                    LogHelper.Info("无待恢复的任务，恢复流程结束");
                    return;
                }

                LogHelper.Info($"检测到 {tasks.Count} 个待恢复任务，开始批量入队");
                foreach (var task in tasks)
                {
                    PublishTaskStatus(task);
                    Enqueue(task);
                }
                LogHelper.Info($"任务恢复完成 | 入队:{tasks.Count}");
            }
            catch (Exception ex)
            {
                LogHelper.Error(ex, "Master重启任务恢复流程执行异常");
            }
        }

        /// <summary>
        /// 优雅关闭（由 IHostedService 统一调用）
        /// </summary>
        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            LogHelper.Info("任务调度后台服务正在停止...");
            await base.StopAsync(cancellationToken);
            LogHelper.Info("任务调度后台服务已停止");
        }

        private void Enqueue(CrawlerTask task)
        {
            lock (_queueLock)
            {
                var secondary = Interlocked.Increment(ref _enqueueCounter);
                _taskQueue.Enqueue(task, (int.MaxValue - task.Priority, secondary));
                _queueCount++;
            }
        }

        /// <summary>
        /// Master 自己改变任务状态（下发、撤销、恢复）后通知界面，界面状态与库中保持一致
        /// </summary>
        private void PublishTaskStatus(CrawlerTask task)
        {
            try
            {
                _eventAggregator.Publish(this, new TaskProgressUpdatedEventArgs(
                    task.TaskId, task.TaskName, task.Status, 0, task.MaxPages, task.AssignedNode));
            }
            catch (Exception ex)
            {
                LogHelper.Error(ex, $"发布任务 {task.TaskId} 状态事件失败");
            }
        }
    }
}
