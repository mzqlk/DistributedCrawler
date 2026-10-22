using CrawlerCore.Entities;
using CrawlerCore.Events;
using CrawlerCore.Interfaces;
using CrawlerMaster.WPF.Services;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;
using TaskStatus = CrawlerCore.Entities.TaskStatus;

namespace CrawlerCore.Tests.Services
{
    public class TaskSchedulerHostedServiceTests : IDisposable
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IServiceScope _scope;
        private readonly Mock<INodeManagementService> _mockNodeManager;
        private readonly Mock<ITaskDataService> _mockTaskDataService;
        private readonly Mock<ICrawlerCoordinatorService> _mockCoordinator;
        private readonly Mock<ILoadBalancingScheduler> _mockLoadBalancer;
        private readonly List<TaskProgressUpdatedEventArgs> _publishedEvents = new();

        public TaskSchedulerHostedServiceTests()
        {
            _mockNodeManager = new Mock<INodeManagementService>();
            _mockTaskDataService = new Mock<ITaskDataService>();
            _mockCoordinator = new Mock<ICrawlerCoordinatorService>();
            _mockLoadBalancer = new Mock<ILoadBalancingScheduler>();

            // 默认：两个节点都有活跃流、不限并发，下发成功
            _mockCoordinator.Setup(c => c.SendTaskToNodeAsync(It.IsAny<string>(), It.IsAny<CrawlerTask>()))
                            .ReturnsAsync(true);
            _mockCoordinator.Setup(c => c.IsNodeConnected(It.IsAny<string>())).Returns(true);
            _mockCoordinator.Setup(c => c.GetMaxConcurrentTasks(It.IsAny<string>())).Returns(0);

            // 默认负载均衡返回 node-1
            _mockLoadBalancer.Setup(l => l.SelectOptimalNode(
                    It.IsAny<IReadOnlyCollection<NodeCandidate>>(),
                    It.IsAny<CrawlerTask>()))
                .Returns(new NodeInfo { NodeId = "node-1" });

            _mockNodeManager.Setup(m => m.GetActiveNodesAsync())
                .ReturnsAsync(new List<NodeInfo>
                {
                    new() { NodeId = "node-1", ActiveTasks = 0, CpuUsage = 10, Status = NodeStatus.Online },
                    new() { NodeId = "node-2", ActiveTasks = 2, CpuUsage = 30, Status = NodeStatus.Online }
                });

            _mockTaskDataService.Setup(m => m.AddTaskAsync(It.IsAny<CrawlerTask>()))
                .ReturnsAsync((CrawlerTask t) => t);
            _mockTaskDataService.Setup(m => m.GetTaskByIdAsync(It.IsAny<string>()))
                .ReturnsAsync((string id) => new CrawlerTask { TaskId = id, Status = TaskStatus.Pending });
            _mockTaskDataService.Setup(m => m.ClearAssignmentIfPendingAsync(It.IsAny<string>())).ReturnsAsync(true);
            _mockTaskDataService.Setup(m => m.TryMarkTaskDispatchedAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
            _mockTaskDataService.Setup(m => m.RevertTaskDispatchAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
            _mockTaskDataService.Setup(m => m.GetUnfinishedTaskCountsByNodeAsync())
                .ReturnsAsync(new Dictionary<string, int>());

            var eventAggregator = new EventAggregator();
            eventAggregator.Subscribe<TaskProgressUpdatedEventArgs>((_, e) => { lock (_publishedEvents) _publishedEvents.Add(e); });

            var services = new ServiceCollection();
            services.AddScoped(_ => _mockNodeManager.Object);
            services.AddScoped(_ => _mockTaskDataService.Object);
            services.AddScoped(_ => _mockCoordinator.Object);
            services.AddSingleton(_ => _mockLoadBalancer.Object);
            services.AddSingleton<IEventAggregator>(eventAggregator);

            services.AddSingleton<TaskSchedulerHostedService>();
            services.AddSingleton<ITaskSchedulerService>(sp => sp.GetRequiredService<TaskSchedulerHostedService>());

            _serviceProvider = services.BuildServiceProvider();
            _scope = _serviceProvider.CreateScope();
        }

        private TaskSchedulerHostedService Scheduler => _scope.ServiceProvider.GetRequiredService<TaskSchedulerHostedService>();

        [Fact(DisplayName = "多优先级入队：高优先级优先出队")]
        public async Task ScheduleTask_MultiPriority_DequeueOrderCorrect()
        {
            var scheduler = _scope.ServiceProvider.GetRequiredService<ITaskSchedulerService>();

            await scheduler.ScheduleTaskAsync(new CrawlerTask { TaskName = "P1", Priority = 1 });
            await scheduler.ScheduleTaskAsync(new CrawlerTask { TaskName = "P5", Priority = 5 });
            await scheduler.ScheduleTaskAsync(new CrawlerTask { TaskName = "P3", Priority = 3 });

            scheduler.TryDequeue(out var task1);
            scheduler.TryDequeue(out var task2);
            scheduler.TryDequeue(out var task3);

            Assert.Equal(5, task1!.Priority);
            Assert.Equal(3, task2!.Priority);
            Assert.Equal(1, task3!.Priority);
        }

        [Fact(DisplayName = "空队列：出队返回false")]
        public void TryDequeue_EmptyQueue_ReturnFalse()
        {
            var scheduler = _scope.ServiceProvider.GetRequiredService<ITaskSchedulerService>();
            var result = scheduler.TryDequeue(out var task);
            Assert.False(result);
            Assert.Null(task);
        }

        [Fact(DisplayName = "优先级越界：入队失败")]
        public async Task ScheduleTask_InvalidPriority_ReturnFalse()
        {
            var scheduler = _scope.ServiceProvider.GetRequiredService<ITaskSchedulerService>();
            var result0 = await scheduler.ScheduleTaskAsync(new CrawlerTask { Priority = 0 });
            var result6 = await scheduler.ScheduleTaskAsync(new CrawlerTask { Priority = 6 });

            Assert.False(result0);
            Assert.False(result6);
        }

        [Fact(DisplayName = "空任务入队：拦截失败")]
        public async Task ScheduleTask_NullTask_ReturnFalse()
        {
            var scheduler = _scope.ServiceProvider.GetRequiredService<ITaskSchedulerService>();
            var result = await scheduler.ScheduleTaskAsync(null!);
            Assert.False(result);
        }

        [Fact(DisplayName = "相同优先级：按入队顺序出队（FIFO）")]
        public async Task ScheduleTask_SamePriority_FifoOrder()
        {
            var scheduler = _scope.ServiceProvider.GetRequiredService<ITaskSchedulerService>();
            var task1 = new CrawlerTask { TaskName = "Task1", Priority = 3 };
            var task2 = new CrawlerTask { TaskName = "Task2", Priority = 3 };
            var task3 = new CrawlerTask { TaskName = "Task3", Priority = 3 };

            await scheduler.ScheduleTaskAsync(task1);
            await scheduler.ScheduleTaskAsync(task2);
            await scheduler.ScheduleTaskAsync(task3);

            scheduler.TryDequeue(out var dequeued1);
            scheduler.TryDequeue(out var dequeued2);
            scheduler.TryDequeue(out var dequeued3);

            Assert.Equal(task1.TaskId, dequeued1!.TaskId);
            Assert.Equal(task2.TaskId, dequeued2!.TaskId);
            Assert.Equal(task3.TaskId, dequeued3!.TaskId);
        }

        [Fact(DisplayName = "已存在但不再是 Pending 的任务：不入队，也不覆盖库中状态（M-10）")]
        public async Task ScheduleTask_ExistingTaskNoLongerPending_NotEnqueued()
        {
            _mockTaskDataService.Setup(m => m.GetTaskByIdAsync(It.IsAny<string>()))
                .ReturnsAsync((string id) => new CrawlerTask { TaskId = id, Status = TaskStatus.Completed });
            _mockTaskDataService.Setup(m => m.ClearAssignmentIfPendingAsync(It.IsAny<string>())).ReturnsAsync(false);

            var result = await Scheduler.ScheduleTaskAsync(new CrawlerTask { TaskName = "已完成", Priority = 3 });

            Assert.False(result);
            Assert.Equal(0, Scheduler.QueueCount);
            _mockTaskDataService.Verify(m => m.UpdateTaskAsync(It.IsAny<CrawlerTask>()), Times.Never);
        }

        [Fact(DisplayName = "任务分配：先写库为 Running 再下发，并发布 Running 事件（M-02、M-14）")]
        public async Task AssignTaskToOptimalNodeAsync_MarksRunningBeforeSending()
        {
            var testTask = new CrawlerTask { TaskName = "TestTask", Priority = 3 };
            var callOrder = new List<string>();
            _mockTaskDataService.Setup(m => m.TryMarkTaskDispatchedAsync(testTask.TaskId, "node-1"))
                .ReturnsAsync(true)
                .Callback(() => callOrder.Add("写库"));
            _mockCoordinator.Setup(c => c.SendTaskToNodeAsync("node-1", It.IsAny<CrawlerTask>()))
                .ReturnsAsync(true)
                .Callback(() => callOrder.Add("下发"));

            var nodeId = await Scheduler.AssignTaskToOptimalNodeAsync(testTask.TaskId);

            Assert.Equal("node-1", nodeId);
            Assert.Equal(new[] { "写库", "下发" }, callOrder);
            var published = Assert.Single(_publishedEvents);
            Assert.Equal(TaskStatus.Running, published.CurrentStatus);
            Assert.Equal("node-1", published.AssignedNodeId);
        }

        [Fact(DisplayName = "负载均衡输入：只包含有活跃流的节点，并带上已分配任务数与并发上限（M-06、M-17④）")]
        public async Task TryAssignTaskAsync_BuildsCandidatesFromConnectedNodes()
        {
            _mockCoordinator.Setup(c => c.IsNodeConnected("node-2")).Returns(false);
            _mockCoordinator.Setup(c => c.GetMaxConcurrentTasks("node-1")).Returns(2);
            _mockTaskDataService.Setup(m => m.GetUnfinishedTaskCountsByNodeAsync())
                .ReturnsAsync(new Dictionary<string, int> { ["node-1"] = 1, ["node-2"] = 0 });
            IReadOnlyCollection<NodeCandidate>? received = null;
            _mockLoadBalancer.Setup(l => l.SelectOptimalNode(It.IsAny<IReadOnlyCollection<NodeCandidate>>(), It.IsAny<CrawlerTask>()))
                .Returns((IReadOnlyCollection<NodeCandidate> c, CrawlerTask _) => { received = c; return c.First().Node; });

            var result = await Scheduler.TryAssignTaskAsync("task-x");

            Assert.Equal(TaskAssignmentOutcome.Assigned, result.Outcome);
            var candidate = Assert.Single(received!);
            Assert.Equal("node-1", candidate.Node.NodeId);
            Assert.Equal(1, candidate.AssignedTasks);
            Assert.Equal(2, candidate.MaxConcurrentTasks);
        }

        [Fact(DisplayName = "下发失败：撤销写库并换下一个节点（M-02③、M-17④）")]
        public async Task TryAssignTaskAsync_WhenSendFails_RevertsAndTriesNextNode()
        {
            _mockLoadBalancer.Setup(l => l.SelectOptimalNode(It.IsAny<IReadOnlyCollection<NodeCandidate>>(), It.IsAny<CrawlerTask>()))
                .Returns((IReadOnlyCollection<NodeCandidate> c, CrawlerTask _) => c.First().Node);
            _mockCoordinator.Setup(c => c.SendTaskToNodeAsync("node-1", It.IsAny<CrawlerTask>())).ReturnsAsync(false);
            _mockCoordinator.Setup(c => c.SendTaskToNodeAsync("node-2", It.IsAny<CrawlerTask>())).ReturnsAsync(true);

            var result = await Scheduler.TryAssignTaskAsync("task-y");

            Assert.Equal(TaskAssignmentOutcome.Assigned, result.Outcome);
            Assert.Equal("node-2", result.NodeId);
            _mockTaskDataService.Verify(m => m.RevertTaskDispatchAsync("task-y", "node-1"), Times.Once);
            _mockTaskDataService.Verify(m => m.TryMarkTaskDispatchedAsync("task-y", "node-2"), Times.Once);
        }

        [Fact(DisplayName = "所有节点都下发失败：返回 SendFailed，任务应重新入队")]
        public async Task TryAssignTaskAsync_WhenAllSendsFail_ReturnsSendFailed()
        {
            _mockLoadBalancer.Setup(l => l.SelectOptimalNode(It.IsAny<IReadOnlyCollection<NodeCandidate>>(), It.IsAny<CrawlerTask>()))
                .Returns((IReadOnlyCollection<NodeCandidate> c, CrawlerTask _) => c.First().Node);
            _mockCoordinator.Setup(c => c.SendTaskToNodeAsync(It.IsAny<string>(), It.IsAny<CrawlerTask>())).ReturnsAsync(false);

            var result = await Scheduler.TryAssignTaskAsync("task-z");

            Assert.Equal(TaskAssignmentOutcome.SendFailed, result.Outcome);
            _mockTaskDataService.Verify(m => m.RevertTaskDispatchAsync("task-z", It.IsAny<string>()), Times.Exactly(2));
        }

        [Fact(DisplayName = "负载均衡返回 null：没有可用节点，不写库不下发")]
        public async Task TryAssignTaskAsync_WhenNoOptimalNode_ReturnsNoAvailableNode()
        {
            _mockLoadBalancer.Setup(l => l.SelectOptimalNode(It.IsAny<IReadOnlyCollection<NodeCandidate>>(), It.IsAny<CrawlerTask>()))
                             .Returns((NodeInfo?)null);

            var result = await Scheduler.TryAssignTaskAsync("task-none");

            Assert.Equal(TaskAssignmentOutcome.NoAvailableNode, result.Outcome);
            Assert.Null(await Scheduler.AssignTaskToOptimalNodeAsync("task-none"));
            _mockTaskDataService.Verify(m => m.TryMarkTaskDispatchedAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            _mockCoordinator.Verify(c => c.SendTaskToNodeAsync(It.IsAny<string>(), It.IsAny<CrawlerTask>()), Times.Never);
        }

        [Fact(DisplayName = "任务已不是 Pending：不调度（M-17③）")]
        public async Task TryAssignTaskAsync_WhenTaskNotPending_ReturnsNotDispatchable()
        {
            _mockTaskDataService.Setup(m => m.GetTaskByIdAsync("task-done"))
                .ReturnsAsync(new CrawlerTask { TaskId = "task-done", Status = TaskStatus.Completed });

            var result = await Scheduler.TryAssignTaskAsync("task-done");

            Assert.Equal(TaskAssignmentOutcome.NotDispatchable, result.Outcome);
            _mockCoordinator.Verify(c => c.SendTaskToNodeAsync(It.IsAny<string>(), It.IsAny<CrawlerTask>()), Times.Never);
        }

        [Fact(DisplayName = "任务不存在：不调度（M-17③）")]
        public async Task TryAssignTaskAsync_WhenTaskMissing_ReturnsNotDispatchable()
        {
            _mockTaskDataService.Setup(m => m.GetTaskByIdAsync("task-missing")).ReturnsAsync((CrawlerTask?)null);

            var result = await Scheduler.TryAssignTaskAsync("task-missing");

            Assert.Equal(TaskAssignmentOutcome.NotDispatchable, result.Outcome);
        }

        [Fact(DisplayName = "写库时任务已被认领：不下发（M-02③）")]
        public async Task TryAssignTaskAsync_WhenMarkDispatchedFails_DoesNotSend()
        {
            _mockTaskDataService.Setup(m => m.TryMarkTaskDispatchedAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(false);

            var result = await Scheduler.TryAssignTaskAsync("task-raced");

            Assert.Equal(TaskAssignmentOutcome.NotDispatchable, result.Outcome);
            _mockCoordinator.Verify(c => c.SendTaskToNodeAsync(It.IsAny<string>(), It.IsAny<CrawlerTask>()), Times.Never);
        }

        [Fact(DisplayName = "调度循环：分配时抛异常，任务重新入队而不是丢失（M-17①）")]
        public async Task ExecuteAsync_WhenAssignmentThrows_RequeuesTask()
        {
            var scheduler = Scheduler;
            await scheduler.ScheduleTaskAsync(new CrawlerTask { TaskName = "异常任务", Priority = 3 });
            var calls = 0;
            _mockNodeManager.Setup(m => m.GetActiveNodesAsync())
                .Callback(() => System.Threading.Interlocked.Increment(ref calls))
                .ThrowsAsync(new InvalidOperationException("模拟数据库繁忙"));

            using var cts = new System.Threading.CancellationTokenSource();
            await scheduler.StartAsync(cts.Token);
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (Volatile.Read(ref calls) < 2 && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(50);
                }
            }
            finally
            {
                await scheduler.StopAsync(default);
            }

            Assert.True(calls >= 2, "抛异常的任务应重新入队并再次尝试分配");
        }

        public void Dispose()
        {
            _scope.Dispose();
            (_serviceProvider as IDisposable)?.Dispose();
        }
    }
}
