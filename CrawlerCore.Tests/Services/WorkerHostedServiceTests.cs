using CrawlerCore.Entities;
using CrawlerCore.Infrastructure;
using CrawlerCore.Protos;
using CrawlerWorker.Communication;
using CrawlerWorker.Services;
using CrawlerWorker.Services.Engine;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;
using CrawlerTask = CrawlerCore.Entities.CrawlerTask;

namespace CrawlerWorker.Tests.Services
{
    /// <summary>
    /// WorkerHostedService 的任务接收（W-22 ①、W-23）。不启动托管服务，消费循环不运行，收到的任务停在本地队列
    /// </summary>
    public sealed class WorkerHostedServiceTests : IDisposable
    {
        private readonly Mock<IWorkerSessionManager> _sessionManager = new();
        private readonly WorkerOutbox _outbox = new(TimeProvider.System);
        private readonly WorkerHostedService _service;

        public WorkerHostedServiceTests()
        {
            var nodeInfo = new Mock<INodeInfoCollector>();
            nodeInfo.Setup(n => n.GetNodeId()).Returns("unit-node");

            _service = new WorkerHostedService(
                new Mock<IGrpcClientManager>().Object,
                nodeInfo.Object,
                _sessionManager.Object,
                Options.Create(new GrpcSettings { NodeAuthToken = "unit-test-token" }),
                Options.Create(new CrawlerSettings { MaxConcurrentTasks = 2 }),
                Options.Create(new SignalRSettings()),
                new Mock<IHostApplicationLifetime>().Object,
                new Mock<ICrawlerEngine>().Object,
                _outbox,
                new Mock<IPluginManager>().Object);

            // 模拟已连接，便于观察是否发出了消息
            _outbox.OnStreamAccepted();
        }

        public void Dispose() => _service.Dispose();

        private static CrawlerTask NewTask(string id) => new() { TaskId = id, TaskName = "单元测试", MaxPages = 10, Priority = 3 };

        private async Task<FromWorker?> TryReadOutboxAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            try
            {
                return (await _outbox.ReadAsync(cts.Token))?.Message;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        [Fact(DisplayName = "W-22 ①：收到任务只放入本地队列，不再上报 Pending")]
        public async Task ProcessNewTask_QueuesWithoutReportingPending()
        {
            _service.ProcessNewTask(NewTask("task-001"));

            Assert.Equal(1, _service.AcceptedTaskCount);
            _sessionManager.Verify(m => m.IncrementActiveTasks(), Times.Once);
            Assert.Null(await TryReadOutboxAsync());
        }

        [Fact(DisplayName = "W-23：同一 TaskId 重复下发时只接收一次")]
        public void ProcessNewTask_DuplicateTaskId_AcceptedOnce()
        {
            _service.ProcessNewTask(NewTask("task-dup"));
            _service.ProcessNewTask(NewTask("task-dup"));

            Assert.Equal(1, _service.AcceptedTaskCount);
            _sessionManager.Verify(m => m.IncrementActiveTasks(), Times.Once);
        }

        [Fact(DisplayName = "W-23：已执行完、结果待确认的任务再次下发时补发结果，不重新执行")]
        public async Task ProcessNewTask_WithPendingResult_ResendsInsteadOfExecuting()
        {
            var result = new FromWorker { CrawlResult = new CrawlerCore.Protos.CrawlResult { TaskId = "task-done" } };
            _outbox.EnqueueResult("task-done", [result], requiresAck: true);
            var first = await _outbox.ReadAsync(CancellationToken.None);
            _outbox.OnWritten(first!);

            _service.ProcessNewTask(NewTask("task-done"));

            Assert.Equal(0, _service.AcceptedTaskCount);
            var resent = await TryReadOutboxAsync();
            Assert.Equal("task-done", resent?.CrawlResult?.TaskId);
        }

        [Fact(DisplayName = "没有 TaskId 的任务被忽略")]
        public void ProcessNewTask_EmptyTaskId_Ignored()
        {
            _service.ProcessNewTask(NewTask(string.Empty));
            Assert.Equal(0, _service.AcceptedTaskCount);
        }
    }
}
