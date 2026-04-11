using CrawlerCore.Entities;
using CrawlerCore.Interfaces;
using CrawlerMaster.WPF.Services;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace CrawlerCore.Tests.Services
{
    /// <summary>
    /// 任务调度优先级队列单元测试（Mock化，不依赖数据库）
    /// 验收标准：1.优先级排序正确 2.空队列返回false 3.参数越界拦截
    /// </summary>
    public class TaskSchedulerHostedServiceTests : IDisposable
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IServiceScope _scope;
        private readonly Mock<INodeManagementService> _mockNodeManager;
        private readonly Mock<ITaskDataService> _mockTaskDataService;

        public TaskSchedulerHostedServiceTests()
        {
            // 初始化 Mock 对象
            _mockNodeManager = new Mock<INodeManagementService>();
            _mockTaskDataService = new Mock<ITaskDataService>();

            // 配置 Mock 行为
            _mockNodeManager
                .Setup(m => m.GetActiveNodesAsync())
                .ReturnsAsync(new List<NodeInfo>
                {
                    new() { NodeId = "node-1", ActiveTasks = 0, CpuUsage = 10 },
                    new() { NodeId = "node-2", ActiveTasks = 2, CpuUsage = 30 }
                });

            _mockTaskDataService
                .Setup(m => m.AddTaskAsync(It.IsAny<CrawlerTask>()))
                .ReturnsAsync((CrawlerTask t) => t);

            _mockTaskDataService
                .Setup(m => m.AssignNodeToTaskAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(true);

            // 构建测试容器
            var services = new ServiceCollection();

            // 注册 Mock 服务（Scoped）
            services.AddScoped(_ => _mockNodeManager.Object);
            services.AddScoped(_ => _mockTaskDataService.Object);

            services.AddSingleton<LoadBalancingScheduler>();

            // 注册 TaskSchedulerHostedService（单例）
            services.AddSingleton<TaskSchedulerHostedService>();
            services.AddSingleton<ITaskSchedulerService>(sp => sp.GetRequiredService<TaskSchedulerHostedService>());

            // 构建服务提供程序
            _serviceProvider = services.BuildServiceProvider();
            _scope = _serviceProvider.CreateScope();
        }

        [Fact(DisplayName = "多优先级入队：高优先级优先出队")]
        public async Task ScheduleTask_MultiPriority_DequeueOrderCorrect()
        {
            var scheduler = _scope.ServiceProvider.GetRequiredService<ITaskSchedulerService>();

            await scheduler.ScheduleTaskAsync(new CrawlerTask { TaskName = "P1", Priority = 1});
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
            var result0 = await scheduler.ScheduleTaskAsync(new CrawlerTask{Priority = 0});
            var result6 = await scheduler.ScheduleTaskAsync(new CrawlerTask{Priority = 6});

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

        [Fact(DisplayName = "任务分配：选择活跃任务数最少的节点")]
        public async Task AssignTaskToOptimalNodeAsync_SelectLeastActiveNode()
        {
            var scheduler = _scope.ServiceProvider.GetRequiredService<ITaskSchedulerService>();
            var testTask = new CrawlerTask { TaskName = "TestTask", Priority = 3 };
            await scheduler.ScheduleTaskAsync(testTask);

            var nodeId = await scheduler.AssignTaskToOptimalNodeAsync(testTask.TaskId);

            Assert.Equal("node-1", nodeId);
            _mockTaskDataService.Verify(m => m.AssignNodeToTaskAsync(testTask.TaskId, "node-1"), Times.Once);
        }

        public void Dispose()
        {
            _scope.Dispose();
            (_serviceProvider as IDisposable)?.Dispose();
        }
    }
}