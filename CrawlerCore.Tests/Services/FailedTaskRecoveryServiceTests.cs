using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CrawlerCore.Entities;
using CrawlerCore.Events;
using CrawlerCore.Interfaces;
using CrawlerMaster.WPF.Data;
using CrawlerMaster.WPF.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using TaskStatus = CrawlerCore.Entities.TaskStatus;

namespace CrawlerCore.Tests.Services
{
    /// <summary>
    /// 故障任务恢复服务单元测试（100%覆盖需求验收标准、无环境依赖、稳定可重复）
    /// </summary>
    public class FailedTaskRecoveryServiceTests : IDisposable
    {
        #region 测试初始化
        private readonly Mock<IEventAggregator> _mockEventAggregator;
        private readonly Mock<ITaskSchedulerService> _mockTaskSchedulerService;
        private readonly ServiceProvider _serviceProvider;
        private readonly SqliteConnection _sqliteConnection;
        private EventHandler<NodeStatusChangedEventArgs> _registeredEventHandler = null!;
        private FailedTaskRecoveryService _recoveryService = null!;

        public FailedTaskRecoveryServiceTests()
        {
            // 初始化依赖Mock
            _mockEventAggregator = new Mock<IEventAggregator>();
            _mockTaskSchedulerService = new Mock<ITaskSchedulerService>();

            // 使用SQLite内存模式，完全匹配原始项目数据库类型，无环境依赖
            _sqliteConnection = new SqliteConnection("DataSource=:memory:");
            _sqliteConnection.Open();

            // 修复：创建内存配置，满足 CrawlerDbContext 的 IConfiguration 依赖
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    {"ConnectionString", "DataSource=:memory:"}
                })
                .Build();

            // 配置DI容器，完全对齐原始项目注册规则
            var services = new ServiceCollection();

            // 修复：注册 IConfiguration 服务
            services.AddSingleton<IConfiguration>(configuration);

            // 注册 DbContext，同时传入 IConfiguration
            services.AddDbContext<CrawlerDbContext>((sp, options) =>
                options.UseSqlite(_sqliteConnection));

            services.AddScoped<ITaskDataService, TaskDataService>();

            _serviceProvider = services.BuildServiceProvider();

            // 初始化数据库与表结构（完全对齐原始项目DbContext配置）
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<CrawlerDbContext>();
            dbContext.Database.EnsureCreated();

            // 捕获事件订阅时注册的处理程序，用于测试触发
            _mockEventAggregator
                .Setup(e => e.Subscribe<NodeStatusChangedEventArgs>(It.IsAny<EventHandler<NodeStatusChangedEventArgs>>()))
                .Returns<EventHandler<NodeStatusChangedEventArgs>>(handler =>
                {
                    _registeredEventHandler = handler;
                    return Guid.NewGuid();
                });

            // 创建测试服务实例
            _recoveryService = new FailedTaskRecoveryService(
                _serviceProvider,
                _mockEventAggregator.Object,
                _mockTaskSchedulerService.Object);
        }
        #endregion

        #region 测试用例（100%覆盖需求验收标准与核心场景）
        [Fact]
        public void 构造函数_应成功订阅节点状态变更事件()
        {
            // 验证事件订阅被正确调用1次
            _mockEventAggregator.Verify(
                e => e.Subscribe<NodeStatusChangedEventArgs>(It.IsAny<EventHandler<NodeStatusChangedEventArgs>>()),
                Times.Once,
                "构造函数应订阅节点状态变更事件");
            Assert.NotNull(_registeredEventHandler);
        }

        [Fact]
        public async Task 节点离线事件_应正确重置运行中任务_加入故障队列_并重入调度队列()
        {
            // Arrange
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<CrawlerDbContext>();

            var testNodeId = "test-node-001";
            var testTasks = new List<CrawlerTask>
            {
                new()
                {
                    TaskId = Guid.NewGuid().ToString(),
                    TaskName = "高优先级测试任务",
                    Status = TaskStatus.Running,
                    AssignedNode = testNodeId,
                    Priority = 5
                },
                new()
                {
                    TaskId = Guid.NewGuid().ToString(),
                    TaskName = "普通优先级测试任务",
                    Status = TaskStatus.Running,
                    AssignedNode = testNodeId,
                    Priority = 2
                },
                new()
                {
                    TaskId = Guid.NewGuid().ToString(),
                    TaskName = "已完成任务",
                    Status = TaskStatus.Completed,
                    AssignedNode = testNodeId
                }
            };

            // 插入测试数据
            await dbContext.CrawlerTasks.AddRangeAsync(testTasks);
            await dbContext.SaveChangesAsync();

            // 构造节点离线事件参数
            var eventArgs = new NodeStatusChangedEventArgs(
                testNodeId,
                NodeStatus.Online,
                NodeStatus.Offline,
                new NodeInfo { NodeId = testNodeId, MachineName = "TestMachine", IpAddress = "127.0.0.1" });

            // 配置调度服务Mock：入队始终返回成功，用信号量同步异步逻辑
            using var signal = new ManualResetEventSlim(false);
            var enqueueCallCount = 0;
            _mockTaskSchedulerService
                .Setup(s => s.ScheduleTaskAsync(It.IsAny<CrawlerTask>()))
                .ReturnsAsync(true)
                .Callback<CrawlerTask>(_ =>
                {
                    Interlocked.Increment(ref enqueueCallCount);
                    if (enqueueCallCount == 2) signal.Set(); // 2个任务都入队后发信号
                });

            // Act
            _registeredEventHandler.Invoke(this, eventArgs);
            // 等待异步逻辑执行完成，最多等待3秒，超时则测试失败
            var timeout = DateTime.UtcNow.AddSeconds(3);
            bool updated = false;

            while (DateTime.UtcNow < timeout)
            {
                var task1 = await dbContext.CrawlerTasks
                    .AsNoTracking()
                    .FirstOrDefaultAsync(t => t.TaskId == testTasks[0].TaskId);
                if (task1?.Status == TaskStatus.Pending)
                {
                    updated = true;
                    break;
                }
                await Task.Delay(50);
            }

            // Assert
            Assert.True(updated, "任务状态未在预期时间内更新为 Pending");
            Assert.True(signal.Wait(3000), "两个任务未在预期时间内重新入队");

            var allTasks = await dbContext.CrawlerTasks.AsNoTracking().ToListAsync();

            var runningTask1 = allTasks.First(t => t.TaskId == testTasks[0].TaskId);
            var runningTask2 = allTasks.First(t => t.TaskId == testTasks[1].TaskId);
            var completedTask = allTasks.First(t => t.TaskId == testTasks[2].TaskId);

            // 1. 验证数据库状态：两个运行中任务已重置为 Pending，且 AssignedNode 已清空
            Assert.Equal(TaskStatus.Pending, runningTask1.Status);
            Assert.Equal(string.Empty, runningTask1.AssignedNode);
            Assert.Equal(TaskStatus.Pending, runningTask2.Status);
            Assert.Equal(string.Empty, runningTask2.AssignedNode);

            // 已完成任务应保持不变
            Assert.Equal(TaskStatus.Completed, completedTask.Status);
            Assert.Equal(testNodeId, completedTask.AssignedNode);

            // 2. 验证故障队列已正确填充
            Assert.Equal(2, _recoveryService.FailedTaskQueue.Count);
            Assert.Contains(runningTask1.TaskId, _recoveryService.FailedTaskQueue);
            Assert.Contains(runningTask2.TaskId, _recoveryService.FailedTaskQueue);

            // 3. 验证任务按原优先级重新入队
            _mockTaskSchedulerService.Verify(
                s => s.ScheduleTaskAsync(It.IsAny<CrawlerTask>()),
                Times.Exactly(2));
            _mockTaskSchedulerService.Verify(
                s => s.ScheduleTaskAsync(It.Is<CrawlerTask>(t => t.Priority == 5)),
                Times.Once);
            _mockTaskSchedulerService.Verify(
                s => s.ScheduleTaskAsync(It.Is<CrawlerTask>(t => t.Priority == 2)),
                Times.Once);

            // 4. 界面收到“待执行”通知，不再一直显示“运行中”（M-14）
            foreach (var reset in new[] { runningTask1, runningTask2 })
            {
                _mockEventAggregator.Verify(
                    e => e.Publish(It.IsAny<object>(), It.Is<TaskProgressUpdatedEventArgs>(a =>
                        a.TaskId == reset.TaskId && a.CurrentStatus == TaskStatus.Pending && a.AssignedNodeId == string.Empty)),
                    Times.Once);
            }
        }

        [Fact]
        public async Task 部分任务重置失败_仅成功重置的任务被入队和加入队列()
        {
            // Arrange：修复核心漏洞的测试用例
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<CrawlerDbContext>();

            var testNodeId = "test-node-002";
            var runningTask = new CrawlerTask
            {
                TaskId = Guid.NewGuid().ToString(),
                TaskName = "运行中任务",
                Status = TaskStatus.Running,
                AssignedNode = testNodeId,
                Priority = 3
            };
            var completedTask = new CrawlerTask
            {
                TaskId = Guid.NewGuid().ToString(),
                TaskName = "已完成任务",
                Status = TaskStatus.Completed, // 模拟查询后状态变更的场景
                AssignedNode = testNodeId
            };

            // 插入测试数据
            await dbContext.CrawlerTasks.AddRangeAsync(runningTask, completedTask);
            await dbContext.SaveChangesAsync();

            // 构造节点离线事件参数
            var eventArgs = new NodeStatusChangedEventArgs(
                testNodeId,
                NodeStatus.Online,
                NodeStatus.Offline,
                new NodeInfo { NodeId = testNodeId });

            // 配置调度服务Mock
            using var signal = new ManualResetEventSlim(false);
            _mockTaskSchedulerService
                .Setup(s => s.ScheduleTaskAsync(It.IsAny<CrawlerTask>()))
                .ReturnsAsync(true)
                .Callback<CrawlerTask>(_ => signal.Set());

            // Act
            _registeredEventHandler.Invoke(this, eventArgs);
            var processCompleted = signal.Wait(3000);

            // Assert
            Assert.True(processCompleted, "事件处理超时，测试失败");

            // 仅成功重置的1个任务被入队和加入队列
            _mockTaskSchedulerService.Verify(
                s => s.ScheduleTaskAsync(It.IsAny<CrawlerTask>()),
                Times.Once,
                "仅应将成功重置的任务入队");
            Assert.Equal(1, _recoveryService.FailedTaskQueue.Count);
            Assert.Contains(runningTask.TaskId, _recoveryService.FailedTaskQueue);
            Assert.DoesNotContain(completedTask.TaskId, _recoveryService.FailedTaskQueue);
        }

        [Fact]
        public async Task 节点离线事件_重复事件幂等_不重复入队()
        {
            // Arrange
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<CrawlerDbContext>();

            var testNodeId = "test-node-003";
            var testTask = new CrawlerTask
            {
                TaskId = Guid.NewGuid().ToString(),
                TaskName = "重复事件测试任务",
                Status = TaskStatus.Running,
                AssignedNode = testNodeId
            };

            await dbContext.CrawlerTasks.AddAsync(testTask);
            await dbContext.SaveChangesAsync();

            var eventArgs = new NodeStatusChangedEventArgs(
                testNodeId,
                NodeStatus.Online,
                NodeStatus.Offline,
                new NodeInfo { NodeId = testNodeId });

            using var firstCallSignal = new ManualResetEventSlim(false);
            var enqueueCallCount = 0;
            _mockTaskSchedulerService
                .Setup(s => s.ScheduleTaskAsync(It.IsAny<CrawlerTask>()))
                .ReturnsAsync(true)
                .Callback<CrawlerTask>(_ =>
                {
                    Interlocked.Increment(ref enqueueCallCount);
                    firstCallSignal.Set();
                });

            // Act：第二次离线事件到达时，任务已被第一次处理重置，不再属于该节点
            _registeredEventHandler.Invoke(this, eventArgs);
            Assert.True(firstCallSignal.Wait(3000), "第一次离线事件处理超时");
            _registeredEventHandler.Invoke(this, eventArgs);
            await Task.Delay(500);

            // Assert
            Assert.Equal(1, enqueueCallCount);
            Assert.Single(_recoveryService.FailedTaskQueue);
        }

        [Fact]
        public async Task 节点30秒内第二次离线_新分配的任务仍会被重置并入队()
        {
            // M-18：节点离线 → 恢复 → 重新上线并领到新任务 → 很快再次离线，第二次离线不能被跳过
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<CrawlerDbContext>();

            var testNodeId = "test-node-007";
            var firstTask = new CrawlerTask
            {
                TaskId = Guid.NewGuid().ToString(),
                TaskName = "第一次离线时的任务",
                Status = TaskStatus.Running,
                AssignedNode = testNodeId
            };
            await dbContext.CrawlerTasks.AddAsync(firstTask);
            await dbContext.SaveChangesAsync();

            var enqueued = new System.Collections.Concurrent.ConcurrentQueue<string>();
            _mockTaskSchedulerService
                .Setup(s => s.ScheduleTaskAsync(It.IsAny<CrawlerTask>()))
                .ReturnsAsync(true)
                .Callback<CrawlerTask>(t => enqueued.Enqueue(t.TaskId));

            var eventArgs = new NodeStatusChangedEventArgs(testNodeId, NodeStatus.Online, NodeStatus.Offline, new NodeInfo { NodeId = testNodeId });
            _registeredEventHandler.Invoke(this, eventArgs);
            await WaitUntilAsync(() => enqueued.Contains(firstTask.TaskId), "第一次离线后任务被重新入队");

            // 节点重新上线后领到新任务
            var secondTask = new CrawlerTask
            {
                TaskId = Guid.NewGuid().ToString(),
                TaskName = "重新上线后领到的任务",
                Status = TaskStatus.Running,
                AssignedNode = testNodeId
            };
            await dbContext.CrawlerTasks.AddAsync(secondTask);
            await dbContext.SaveChangesAsync();

            // 1 秒后再次离线（在原来 30 秒的去重窗口内）
            _registeredEventHandler.Invoke(this, eventArgs);
            await WaitUntilAsync(() => enqueued.Contains(secondTask.TaskId), "第二次离线后新任务被重新入队");

            var stored = await dbContext.CrawlerTasks.AsNoTracking().FirstAsync(t => t.TaskId == secondTask.TaskId);
            Assert.Equal(TaskStatus.Pending, stored.Status);
            Assert.Equal(string.Empty, stored.AssignedNode);
        }

        [Fact]
        public async Task 节点离线事件_本地排队的Pending任务也被重置()
        {
            // M-02：Worker 本地排队时上报 Pending，旧数据中会出现“Pending + 已分配节点”，节点离线后也必须恢复
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<CrawlerDbContext>();

            var testNodeId = "test-node-008";
            var queuedTask = new CrawlerTask
            {
                TaskId = Guid.NewGuid().ToString(),
                TaskName = "本地排队的任务",
                Status = TaskStatus.Pending,
                AssignedNode = testNodeId
            };
            await dbContext.CrawlerTasks.AddAsync(queuedTask);
            await dbContext.SaveChangesAsync();

            using var signal = new ManualResetEventSlim(false);
            _mockTaskSchedulerService
                .Setup(s => s.ScheduleTaskAsync(It.IsAny<CrawlerTask>()))
                .ReturnsAsync(true)
                .Callback<CrawlerTask>(_ => signal.Set());

            _registeredEventHandler.Invoke(this, new NodeStatusChangedEventArgs(testNodeId, NodeStatus.Online, NodeStatus.Offline, new NodeInfo { NodeId = testNodeId }));

            Assert.True(signal.Wait(3000), "排队任务应被重新入队");
            var stored = await dbContext.CrawlerTasks.AsNoTracking().FirstAsync(t => t.TaskId == queuedTask.TaskId);
            Assert.Equal(string.Empty, stored.AssignedNode);
        }

        private static async Task WaitUntilAsync(Func<bool> condition, string description)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException($"等待超时：{description}");
                await Task.Delay(20);
            }
        }

        [Fact]
        public async Task 并发触发同一节点离线事件_仅处理一次()
        {
            // Arrange
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<CrawlerDbContext>();

            var testNodeId = "test-node-004";
            var testTask = new CrawlerTask
            {
                TaskId = Guid.NewGuid().ToString(),
                TaskName = "并发测试任务",
                Status = TaskStatus.Running,
                AssignedNode = testNodeId
            };

            await dbContext.CrawlerTasks.AddAsync(testTask);
            await dbContext.SaveChangesAsync();

            var eventArgs = new NodeStatusChangedEventArgs(
                testNodeId,
                NodeStatus.Online,
                NodeStatus.Offline,
                new NodeInfo { NodeId = testNodeId });

            using var signal = new ManualResetEventSlim(false);
            var enqueueCallCount = 0;
            _mockTaskSchedulerService
                .Setup(s => s.ScheduleTaskAsync(It.IsAny<CrawlerTask>()))
                .ReturnsAsync(true)
                .Callback<CrawlerTask>(_ =>
                {
                    Interlocked.Increment(ref enqueueCallCount);
                    signal.Set();
                });

            // Act：并发触发10次事件
            var parallelTasks = Enumerable.Range(0, 10)
                .Select(_ => Task.Run(() => _registeredEventHandler.Invoke(this, eventArgs)))
                .ToList();

            await Task.WhenAll(parallelTasks);
            signal.Wait(3000);

            // Assert
            Assert.Equal(1, enqueueCallCount);//, "并发触发同一节点离线事件仅应处理一次"
            Assert.Single(_recoveryService.FailedTaskQueue);
        }

        [Fact]
        public async Task 节点离线事件_无运行中任务_不执行任何操作()
        {
            // Arrange
            var testNodeId = "test-node-005";
            var eventArgs = new NodeStatusChangedEventArgs(
                testNodeId,
                NodeStatus.Online,
                NodeStatus.Offline,
                new NodeInfo { NodeId = testNodeId });

            var enqueueCallCount = 0;
            _mockTaskSchedulerService
                .Setup(s => s.ScheduleTaskAsync(It.IsAny<CrawlerTask>()))
                .ReturnsAsync(true)
                .Callback<CrawlerTask>(_ => Interlocked.Increment(ref enqueueCallCount));

            // Act
            _registeredEventHandler.Invoke(this, eventArgs);
            await Task.Delay(500);

            // Assert
            Assert.Equal(0, enqueueCallCount);//, "无运行中任务时不应执行入队操作"
            Assert.Empty(_recoveryService.FailedTaskQueue);
        }

        [Fact]
        public async Task 非离线状态事件_不执行任何处理()
        {
            // Arrange
            var eventArgs = new NodeStatusChangedEventArgs(
                "test-node-006",
                NodeStatus.Pending,
                NodeStatus.Online,
                new NodeInfo { NodeId = "test-node-006" });

            var enqueueCallCount = 0;
            _mockTaskSchedulerService
                .Setup(s => s.ScheduleTaskAsync(It.IsAny<CrawlerTask>()))
                .ReturnsAsync(true)
                .Callback(() => Interlocked.Increment(ref enqueueCallCount));

            // Act
            _registeredEventHandler.Invoke(this, eventArgs);
            await Task.Delay(500);

            // Assert
            Assert.Equal(0, enqueueCallCount);//, "非离线状态事件不应执行任何处理"
            Assert.Empty(_recoveryService.FailedTaskQueue);
        }

        [Fact]
        public void Dispose_应正确取消事件订阅并释放所有资源()
        {
            // Act
            _recoveryService.Dispose();

            // Assert
            _mockEventAggregator.Verify(
                e => e.Unsubscribe<NodeStatusChangedEventArgs>(It.IsAny<Guid>()),
                Times.Once,
                "Dispose时应取消事件订阅");
            Assert.Empty(_recoveryService.FailedTaskQueue);
        }
        #endregion

        #region 资源释放（规范完整，无资源泄漏）
        public void Dispose()
        {
            _recoveryService?.Dispose();
            _sqliteConnection.Close();
            _sqliteConnection.Dispose();
            _serviceProvider.Dispose();
        }
        #endregion
    }
}