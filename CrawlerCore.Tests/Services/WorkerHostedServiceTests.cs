using CrawlerCore.Entities;
using CrawlerCore.Infrastructure;
using CrawlerWorker.Communication;
using CrawlerWorker.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;
using System.Threading.Tasks;
using Xunit;

namespace CrawlerWorker.Tests.Services
{
    public class WorkerHostedServiceTests
    {
        private readonly Mock<IGrpcClientManager> _mockGrpcClientManager;
        private readonly Mock<INodeInfoCollector> _mockNodeInfoCollector;
        private readonly Mock<IWorkerSessionManager> _mockSessionManager;
        private readonly Mock<IHostApplicationLifetime> _mockAppLifetime;
        private readonly IOptions<GrpcSettings> _grpcSettingsOptions;
        private readonly TestableWorkerHostedService _service;

        // 派生类，暴露受保护/私有方法
        internal class TestableWorkerHostedService : WorkerHostedService
        {
            public TestableWorkerHostedService(
                IGrpcClientManager grpcClientManager,
                INodeInfoCollector nodeInfoCollector,
                IWorkerSessionManager sessionManager,
                IOptions<GrpcSettings> grpcSettings,
                IHostApplicationLifetime appLifetime)
                : base(grpcClientManager, nodeInfoCollector, sessionManager, grpcSettings, appLifetime)
            {
            }

            public Task PublicProcessNewTaskAsync(CrawlerTask task) => ProcessNewTaskAsync(task);
        }

        public WorkerHostedServiceTests()
        {
            _mockGrpcClientManager = new Mock<IGrpcClientManager>();
            _mockNodeInfoCollector = new Mock<INodeInfoCollector>();
            _mockSessionManager = new Mock<IWorkerSessionManager>();
            _mockAppLifetime = new Mock<IHostApplicationLifetime>();

            var grpcSettings = new GrpcSettings
            {
                HeartbeatIntervalSeconds = 5,
                MaxRetryAttempts = 3,
                RetryDelayMilliseconds = 1000,
                NodeAuthToken = "test-token"
            };
            _grpcSettingsOptions = Options.Create(grpcSettings);

            _service = new TestableWorkerHostedService(
                _mockGrpcClientManager.Object,
                _mockNodeInfoCollector.Object,
                _mockSessionManager.Object,
                _grpcSettingsOptions,
                _mockAppLifetime.Object);
        }

        [Fact]
        public async Task ProcessNewTaskAsync_EnqueuesTaskAndSendsProgress()
        {
            // Arrange
            var task = new CrawlerTask
            {
                TaskId = "task-001",
                MaxPages = 10,
                Priority = 3
            };

            // Act
            await _service.PublicProcessNewTaskAsync(task);

            // Assert
            _mockSessionManager.Verify(m => m.IncrementActiveTasks(), Times.Once);
            bool dequeued = _service.TryGetNextTask(out var dequeuedTask);
            Assert.True(dequeued);
            Assert.Equal("task-001", dequeuedTask!.TaskId);
        }

        [Fact]
        public void TryGetNextTask_WhenQueueEmpty_ReturnsFalse()
        {
            bool result = _service.TryGetNextTask(out var task);
            Assert.False(result);
            Assert.Null(task);
        }
    }
}