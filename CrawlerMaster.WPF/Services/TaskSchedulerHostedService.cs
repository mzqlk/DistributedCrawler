using CrawlerCore.Entities;
using CrawlerCore.Interfaces;
using CrawlerCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace CrawlerMaster.WPF.Services
{
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
        // 注入负载均衡调度器
        private readonly ILoadBalancingScheduler _loadBalancer;

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

        public TaskSchedulerHostedService(IServiceScopeFactory scopeFactory, ILoadBalancingScheduler loadBalancer)
        {
            _scopeFactory = scopeFactory;
            _loadBalancer = loadBalancer;
            LogHelper.Info("任务调度后台服务已初始化（已注入资源感知负载均衡调度器，预留调用点）");
        }

        /// <summary>
        /// 任务入队
        /// </summary>
        /// <param name="task">爬虫任务</param>
        /// <returns>入队结果</returns>
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
                // 检查任务是否已存在于数据库
                var existingTask = await taskDataService.GetTaskByIdAsync(task.TaskId);
                if (existingTask != null)
                {
                    // 已存在：仅更新状态为 Pending，清空分配节点（故障恢复场景）
                    existingTask.Status = CrawlerCore.Entities.TaskStatus.Pending;
                    existingTask.AssignedNode = string.Empty; // 清空原分配节点
                    await taskDataService.UpdateTaskAsync(existingTask);

                    LogHelper.Info($"已存在任务重新入队：TaskId={task.TaskId}, 状态已重置为Pending");
                }
                else
                {
                    // 不存在：正常插入数据库
                    await taskDataService.AddTaskAsync(task);
                    LogHelper.Info($"新任务已持久化：TaskId={task.TaskId}");
                }
            }
            catch (Exception ex)
            {
                LogHelper.Error(ex, $"任务入队失败：数据库保存异常，TaskId={task.TaskId}");
                return false;
            }

            // 赋值优先级并入队（线程安全 + FIFO保障）
            lock (_queueLock)
            {
                var secondary = Interlocked.Increment(ref _enqueueCounter);
                _taskQueue.Enqueue(task, (int.MaxValue - task.Priority, secondary));
                _queueCount++;
            }

            // 结构化日志记录
            LogHelper.Info($"任务入队成功：TaskId={task.TaskId}, TaskName={task.TaskName}, Priority={task.Priority}, 当前队列数={_queueCount}");

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
        public async Task<string?> AssignTaskToOptimalNodeAsync(string taskId)
        {
            LogHelper.Info($"开始分配并下发任务：TaskId={taskId}");

            // 使用 Scope 解析 Scoped 服务
            using var scope = _scopeFactory.CreateScope();
            var nodeManager = scope.ServiceProvider.GetRequiredService<INodeManagementService>();
            var taskDataService = scope.ServiceProvider.GetRequiredService<ITaskDataService>();
            var coordinatorService = scope.ServiceProvider.GetRequiredService<ICrawlerCoordinatorService>();

            // 1. 获取任务详情
            var task = await taskDataService.GetTaskByIdAsync(taskId).ConfigureAwait(false);
            if (task == null)
            {
                LogHelper.Error($"任务分配失败：任务 {taskId} 不存在");
                return null;
            }

            // 2. 获取在线节点
            var activeNodes = await nodeManager.GetActiveNodesAsync().ConfigureAwait(false);
            if (activeNodes.Count == 0)
            {
                LogHelper.Warn($"任务分配失败：无可用在线节点，TaskId={taskId}");
                return null;
            }

            // 3. 负载均衡选择最优节点
            var optimalNode = _loadBalancer.SelectOptimalNode(activeNodes, task);
            if (optimalNode == null)
            {
                LogHelper.Warn($"任务分配失败：无满足条件的节点，TaskId={taskId}");
                return null;
            }

            // 4. 通过 gRPC 下发任务
            bool sendSuccess = await coordinatorService.SendTaskToNodeAsync(optimalNode.NodeId, task).ConfigureAwait(false);
            if (!sendSuccess)
            {
                LogHelper.Error($"任务下发失败：gRPC 通信异常，TaskId={taskId}, NodeId={optimalNode.NodeId}");
                return null;
            }

            // 5. 更新数据库状态
            bool updateSuccess = await taskDataService.AssignNodeToTaskAsync(taskId, optimalNode.NodeId).ConfigureAwait(false)
                                && await taskDataService.UpdateTaskStatusAsync(taskId, CrawlerCore.Entities.TaskStatus.Running).ConfigureAwait(false);
            if (!updateSuccess)
            {
                LogHelper.Error($"任务状态更新失败：数据库异常，TaskId={taskId}, NodeId={optimalNode.NodeId}");
                // 最终一致性由心跳/进度上报修复
                return null;
            }

            LogHelper.Info($"任务下发成功：TaskId={taskId}, NodeId={optimalNode.NodeId}");
            return optimalNode.NodeId;
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
                    // 1. 尝试出队最高优先级任务
                    if (TryDequeue(out var task))
                    {
                        // 2. 分配任务到最优节点
                        var nodeId = await AssignTaskToOptimalNodeAsync(task!.TaskId).ConfigureAwait(false);

                        // 3. 如果分配失败，将任务重新入队（延迟1秒，使用新的入队序号）
                        if (nodeId == null)
                        {
                            await Task.Delay(1000, stoppingToken).ConfigureAwait(false);
                            lock (_queueLock)
                            {
                                var secondary = Interlocked.Increment(ref _enqueueCounter);
                                _taskQueue.Enqueue(task, (int.MaxValue - task.Priority, secondary));
                                _queueCount++;
                            }
                            LogHelper.Warn($"任务重新入队：TaskId={task.TaskId}, Priority={task.Priority}");
                        }
                    }
                    else
                    {
                        // 4. 队列为空，延迟100ms再检查
                        await Task.Delay(100, stoppingToken).ConfigureAwait(false);
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
        /// 从数据库恢复未调度的Pending任务（重启自动入队）
        /// 筛选条件：状态=Pending + 未分配节点 + 按创建时间升序执行
        /// </summary>
        public async Task RecoverPendingTasksFromDatabaseAsync()
        {
            try
            {
                LogHelper.Info("开始执行Master重启任务恢复，加载未调度的Pending任务");

                // 1. 查询符合条件的任务：Pending状态 + 未分配节点
                List<CrawlerTask> allTasks;
                using (var scope = _scopeFactory.CreateScope())
                {
                    allTasks = await scope.ServiceProvider.GetRequiredService<ITaskDataService>().GetAllTasksAsync();
                }
                var pendingTasks = allTasks
                    .Where(t => t.Status == CrawlerCore.Entities.TaskStatus.Pending
                                && string.IsNullOrWhiteSpace(t.AssignedNode))
                    .OrderBy(t => t.CreatedAt) // 按创建时间升序，保证先创建先调度
                    .ToList();

                if (!pendingTasks.Any())
                {
                    LogHelper.Info("无待恢复的未调度任务，恢复流程结束");
                    return;
                }

                LogHelper.Info($"检测到 {pendingTasks.Count} 个待恢复任务，开始批量入队");

                // 2. 逐个重新入队调度
                int successCount = 0;
                foreach (var task in pendingTasks)
                {
                    bool enqueueSuccess = await ScheduleTaskAsync(task);
                    if (enqueueSuccess)
                    {
                        successCount++;
                        LogHelper.Debug($"任务恢复入队成功 | TaskId:{task.TaskId} 优先级:{task.Priority}");
                    }
                    else
                    {
                        LogHelper.Error($"任务恢复入队失败 | TaskId:{task.TaskId}");
                    }
                }

                LogHelper.Info($"任务恢复完成 | 总计:{pendingTasks.Count} 成功:{successCount} 失败:{pendingTasks.Count - successCount}");
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
    }
}