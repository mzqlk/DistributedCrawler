using CrawlerCore.Entities;
using CrawlerCore.Interfaces;
using CrawlerMaster.WPF.Data;
using CrawlerMaster.WPF.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;
using Microsoft.Data.Sqlite;
using TaskStatus = CrawlerCore.Entities.TaskStatus;

namespace CrawlerCore.Tests.Services
{
    public class TaskDataServiceTests : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly CrawlerDbContext _dbContext;
        private readonly ITaskDataService _taskService;

        public TaskDataServiceTests()
        {
            // SQLite内存数据库（与生产环境一致，支持JSON转换/枚举）
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();

            // 模拟IConfiguration（修复DbContext空引用）
            var mockConfig = new Mock<IConfiguration>();

            var options = new DbContextOptionsBuilder<CrawlerDbContext>()
                .UseSqlite(_connection)
                .Options;

            _dbContext = new CrawlerDbContext(options, mockConfig.Object);
            _dbContext.Database.EnsureCreated(); // 创建表结构
            _taskService = new TaskDataService(_dbContext);
        }

        [Fact]
        public async Task AddTaskAsync_ValidTask_Success()
        {
            // Arrange
            var task = new CrawlerTask
            {
                TaskName = "单元测试任务",
                StartUrls = new List<string> { "https://test.com" },
                PluginName = "TestPlugin"
            };

            // Act
            var result = await _taskService.AddTaskAsync(task);
            var dbTask = await _taskService.GetTaskByIdAsync(result.TaskId);

            // Assert
            Assert.NotNull(result);
            Assert.NotNull(dbTask);
            Assert.Equal(task.TaskName, dbTask.TaskName);
        }

        [Fact]
        public async Task GetAllTasksAsync_ReturnsCorrectCount()
        {
            await _taskService.AddTaskAsync(new CrawlerTask { TaskName = "任务1" });
            await _taskService.AddTaskAsync(new CrawlerTask { TaskName = "任务2" });
            var list = await _taskService.GetAllTasksAsync();
            Assert.Equal(2, list.Count);
        }

        [Fact]
        public async Task UpdateTaskStatusAsync_Success()
        {
            var task = await _taskService.AddTaskAsync(new CrawlerTask { TaskName = "状态测试" });
            var updateResult = await _taskService.UpdateTaskStatusAsync(task.TaskId, TaskStatus.Running);
            var updatedTask = await _taskService.GetTaskByIdAsync(task.TaskId);

            Assert.True(updateResult);
            Assert.Equal(TaskStatus.Running, updatedTask!.Status);
        }

        [Fact]
        public async Task AssignNodeToTaskAsync_Success()
        {
            var task = await _taskService.AddTaskAsync(new CrawlerTask { TaskName = "节点测试" });
            const string nodeId = "TestNode_001";
            var result = await _taskService.AssignNodeToTaskAsync(task.TaskId, nodeId);
            var updatedTask = await _taskService.GetTaskByIdAsync(task.TaskId);

            Assert.True(result);
            Assert.Equal(nodeId, updatedTask!.AssignedNode);
        }

        [Fact]
        public async Task DeleteTaskAsync_Success()
        {
            var task = await _taskService.AddTaskAsync(new CrawlerTask { TaskName = "删除测试" });
            var result = await _taskService.DeleteTaskAsync(task.TaskId);
            var deletedTask = await _taskService.GetTaskByIdAsync(task.TaskId);

            Assert.True(result);
            Assert.Null(deletedTask);
        }

        // 释放数据库连接
        public void Dispose()
        {
            _dbContext.Database.EnsureDeleted();
            _dbContext.Dispose();
            _connection.Close();
            _connection.Dispose();
        }
    }
}