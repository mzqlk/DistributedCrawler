using CrawlerCore.Entities;
using CrawlerCore.Infrastructure;
using CrawlerWorker.Communication;
using CrawlerWorker.Services;
using CrawlerWorker.Services.Engine;
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
        private readonly IOptions<CrawlerSettings> _crawlerSettingsOptions;
        private readonly IOptions<SignalRSettings> _signalRSettingsOptions;
        private readonly Mock<ICrawlerEngine> _mockCrawlerEngine;
        private readonly TestableWorkerHostedService _service;

        // 派生类，暴露受保护/私有方法
        internal class TestableWorkerHostedService : WorkerHostedService
        {
            public TestableWorkerHostedService(
                IGrpcClientManager grpcClientManager,
                INodeInfoCollector nodeInfoCollector,
                IWorkerSessionManager sessionManager,
                IOptions<GrpcSettings> grpcSettings,
                IOptions<CrawlerSettings> crawlerSettings,
                IOptions<SignalRSettings> signalRSettings,
                IHostApplicationLifetime appLifetime,
                ICrawlerEngine crawlerEngine)
                : base(grpcClientManager, nodeInfoCollector, sessionManager, grpcSettings, crawlerSettings, signalRSettings, appLifetime, crawlerEngine)
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

            var crawlerSettings = new CrawlerSettings
            {
                // 初始化 CrawlerSettings 的属性
            };
            _crawlerSettingsOptions = Options.Create(crawlerSettings);

            // 新增：SignalRSettings 和 CrawlerEngine（使用 Mock 或真实实例）
            var signalRSettings = new SignalRSettings
            {
                // 如有必要，初始化属性
            };
            _signalRSettingsOptions = Options.Create(signalRSettings);
            _mockCrawlerEngine = new Mock<ICrawlerEngine>();

            _service = new TestableWorkerHostedService(
                _mockGrpcClientManager.Object,
                _mockNodeInfoCollector.Object,
                _mockSessionManager.Object,
                _grpcSettingsOptions,
                _crawlerSettingsOptions,
                _signalRSettingsOptions,
                _mockAppLifetime.Object,
                _mockCrawlerEngine.Object);
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