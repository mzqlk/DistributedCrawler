using CrawlerCore.Entities;
using CrawlerCore.Events;
using CrawlerCore.Infrastructure;
using CrawlerCore.Interfaces;
using CrawlerCore.Protos;
using CrawlerMaster.WPF.Data;
using CrawlerMaster.WPF.Services;
using Grpc.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TaskStatus = CrawlerCore.Entities.TaskStatus;

namespace CrawlerCore.Tests.Grpc;

/// <summary>
/// 协调服务双向流与消息处理的单元测试（T4：M-05②③、M-11、M-12、M-16、结果回执）。
/// 用假的请求/响应流直接调用 StartStreaming，数据库为临时 SQLite 文件（每个作用域独立连接，与生产一致）。
/// </summary>
public sealed class CoordinatorStreamTests : IDisposable
{
    private const string NodeId = "unit-node";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly string _databasePath;
    private readonly ServiceProvider _services;
    private readonly SessionManager _sessionManager;
    private readonly CrawlerCoordinatorService _coordinator;
    private readonly List<NodeStatusChangedEventArgs> _nodeEvents = new();
    private readonly List<TaskProgressUpdatedEventArgs> _progressEvents = new();

    public CoordinatorStreamTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"coordinator-tests-{Guid.NewGuid():N}.db");

        var eventAggregator = new EventAggregator();
        eventAggregator.Subscribe<NodeStatusChangedEventArgs>((_, e) => { lock (_nodeEvents) _nodeEvents.Add(e); });
        eventAggregator.Subscribe<TaskProgressUpdatedEventArgs>((_, e) => { lock (_progressEvents) _progressEvents.Add(e); });

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<IEventAggregator>(eventAggregator);
        services.AddDbContext<CrawlerDbContext>(o => o.UseSqlite($"Data Source={_databasePath}"));
        services.AddScoped<ITaskDataService, TaskDataService>();
        services.AddScoped<INodeManagementService, NodeManagementService>();
        _services = services.BuildServiceProvider();

        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CrawlerDbContext>();
            db.Database.EnsureCreated();
            db.NodeInfos.Add(new NodeInfo { NodeId = NodeId, Status = NodeStatus.Online, LastHeartbeat = DateTime.UtcNow });
            db.SaveChanges();
        }

        _sessionManager = new SessionManager(NullLogger<SessionManager>.Instance);
        _coordinator = new CrawlerCoordinatorService(
            _sessionManager,
            Options.Create(new GrpcSettings()),
            NullLogger<CrawlerCoordinatorService>.Instance,
            eventAggregator,
            _services);
    }

    public void Dispose()
    {
        _coordinator.Dispose();
        _services.Dispose();
        SqliteConnection.ClearAllPools();
        try { File.Delete(_databasePath); } catch (IOException) { }
    }

    #region 辅助方法
    private sealed record StreamHandle(Task Call, FakeRequestStream<FromWorker> Requests, FakeResponseStream<FromMaster> Responses);

    private StreamHandle OpenStream(string sessionId, CancellationToken callCancellation = default)
    {
        var requests = new FakeRequestStream<FromWorker>();
        var responses = new FakeResponseStream<FromMaster>();
        var context = new TestServerCallContext(new Metadata { { "node-id", NodeId }, { "session-id", sessionId } }, callCancellation);
        var call = Task.Run(() => _coordinator.StartStreaming(requests, responses, context));
        return new StreamHandle(call, requests, responses);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string description)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"等待超时：{description}");
            await Task.Delay(20);
        }
    }

    private async Task<T> QueryAsync<T>(Func<CrawlerDbContext, Task<T>> query)
    {
        using var scope = _services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<CrawlerDbContext>());
    }

    private Task<NodeStatus> NodeStatusAsync() => QueryAsync(db => db.NodeInfos.AsNoTracking().Where(n => n.NodeId == NodeId).Select(n => n.Status).SingleAsync());

    private Task<CrawlerCore.Entities.CrawlerTask> TaskAsync(string taskId) => QueryAsync(db => db.CrawlerTasks.AsNoTracking().SingleAsync(t => t.TaskId == taskId));

    private int OfflineEventCount() { lock (_nodeEvents) return _nodeEvents.Count(e => e.NodeId == NodeId && e.NewStatus == NodeStatus.Offline); }

    private async Task<string> AddRunningTaskAsync()
    {
        var task = new CrawlerCore.Entities.CrawlerTask { TaskName = "单元测试任务", Status = TaskStatus.Running, AssignedNode = NodeId, MaxPages = 5 };
        await QueryAsync(async db => { db.CrawlerTasks.Add(task); return await db.SaveChangesAsync(); });
        return task.TaskId;
    }

    private static FromWorker Progress(string taskId, CrawlerCore.Protos.TaskStatus status, int pages)
        => new() { TaskProgress = new TaskProgress { TaskId = taskId, CurrentStatus = status, CrawledPages = pages, TotalPages = 5 } };

    private static FromWorker Result(string taskId, int pages)
    {
        var result = new CrawlerCore.Protos.CrawlResult { TaskId = taskId };
        for (var i = 0; i < pages; i++)
        {
            result.PageResults.Add(new CrawlerCore.Protos.PageResult { Url = $"http://example.test/{i}", Finalurl = $"http://example.test/{i}", StatusCode = 200 });
        }
        return new FromWorker { CrawlResult = result };
    }
    #endregion

    [Fact(DisplayName = "读循环异常结束时 StartStreaming 返回，节点只被标记离线一次（M-16①③）")]
    public async Task ReadLoopFault_EndsCall_AndMarksOfflineOnce()
    {
        var sessionId = _sessionManager.ReplaceSession(NodeId);
        var stream = OpenStream(sessionId);
        await WaitUntilAsync(() => _coordinator.IsNodeConnected(NodeId), "流已登记");

        // 例如单条消息超过上限：读取失败，而写循环还在等通道
        stream.Requests.Complete(new RpcException(new Status(StatusCode.ResourceExhausted, "消息超过上限")));

        await stream.Call.WaitAsync(Timeout);
        Assert.False(_coordinator.IsNodeConnected(NodeId));
        Assert.Equal(NodeStatus.Offline, await NodeStatusAsync());
        Assert.Equal(1, OfflineEventCount());
    }

    [Fact(DisplayName = "同一会话的新流替换旧流：旧流被关闭，其清理不删除新流、不把节点标记离线（M-05②、M-16②）")]
    public async Task NewStreamReplacesOld_OldCleanupDoesNotAffectNew()
    {
        var sessionId = _sessionManager.ReplaceSession(NodeId);
        var oldStream = OpenStream(sessionId);
        await WaitUntilAsync(() => _coordinator.IsNodeConnected(NodeId), "旧流已登记");

        var newStream = OpenStream(sessionId);

        await oldStream.Call.WaitAsync(Timeout);
        await Task.Delay(200);
        Assert.False(newStream.Call.IsCompleted, "新流不应被拒绝或关闭");
        Assert.True(_coordinator.IsNodeConnected(NodeId));
        Assert.Equal(NodeStatus.Online, await NodeStatusAsync());
        Assert.Equal(0, OfflineEventCount());

        newStream.Requests.Complete();
        await newStream.Call.WaitAsync(Timeout);
        Assert.Equal(NodeStatus.Offline, await NodeStatusAsync());
        Assert.Equal(1, OfflineEventCount());
    }

    [Fact(DisplayName = "DisconnectNode 关闭节点的流（健康检查判离线后调用，M-05③）")]
    public async Task DisconnectNode_EndsStream()
    {
        var sessionId = _sessionManager.ReplaceSession(NodeId);
        var stream = OpenStream(sessionId);
        await WaitUntilAsync(() => _coordinator.IsNodeConnected(NodeId), "流已登记");

        _coordinator.DisconnectNode(NodeId);

        await stream.Call.WaitAsync(Timeout);
        Assert.False(_coordinator.IsNodeConnected(NodeId));
    }

    [Fact(DisplayName = "会话无效时 StartStreaming 返回 PermissionDenied")]
    public async Task InvalidSession_IsPermissionDenied()
    {
        _sessionManager.ReplaceSession(NodeId);
        var stream = OpenStream("not-the-session");

        var error = await Assert.ThrowsAsync<RpcException>(() => stream.Call.WaitAsync(Timeout));
        Assert.Equal(StatusCode.PermissionDenied, error.StatusCode);
    }

    [Fact(DisplayName = "Completed 进度不完成任务；结果落库后才 Completed，并发送结果回执（M-11、W-03 Master 侧）")]
    public async Task CompletedProgress_WaitsForResult_ThenAcks()
    {
        var taskId = await AddRunningTaskAsync();
        var sessionId = _sessionManager.ReplaceSession(NodeId);
        var stream = OpenStream(sessionId);
        await WaitUntilAsync(() => _coordinator.IsNodeConnected(NodeId), "流已登记");

        stream.Requests.Write(Progress(taskId, CrawlerCore.Protos.TaskStatus.Completed, 2));
        await WaitUntilAsync(() => { lock (_progressEvents) return _progressEvents.Any(e => e.TaskId == taskId && e.CrawledPages == 2); }, "Completed 进度被处理");
        Assert.Equal(TaskStatus.Running, (await TaskAsync(taskId)).Status);
        lock (_progressEvents)
        {
            Assert.All(_progressEvents.Where(e => e.TaskId == taskId), e => Assert.Equal(TaskStatus.Running, e.CurrentStatus));
        }

        stream.Requests.Write(Result(taskId, 2));
        await WaitUntilAsync(() => stream.Responses.Written.Any(m => m.MessageCase == FromMaster.MessageOneofCase.ResultAck), "收到结果回执");

        var ack = stream.Responses.Written.Single(m => m.MessageCase == FromMaster.MessageOneofCase.ResultAck).ResultAck;
        Assert.Equal(taskId, ack.TaskId);
        Assert.True(ack.Accepted);
        Assert.Equal(TaskStatus.Completed, (await TaskAsync(taskId)).Status);
        lock (_progressEvents)
        {
            Assert.Equal(TaskStatus.Completed, _progressEvents.Last(e => e.TaskId == taskId).CurrentStatus);
        }

        // 重复结果：仍回执 accepted=true，库中只有一份
        stream.Requests.Write(Result(taskId, 2));
        await WaitUntilAsync(() => stream.Responses.Written.Count(m => m.MessageCase == FromMaster.MessageOneofCase.ResultAck) == 2, "收到第二个回执");
        Assert.Equal(1, await QueryAsync(db => db.CrawlResults.CountAsync(r => r.TaskId == taskId)));

        stream.Requests.Complete();
        await stream.Call.WaitAsync(Timeout);
    }

    [Fact(DisplayName = "结果落库失败：任务标为 Failed 并通知界面，不发送回执（M-12）")]
    public async Task ResultPersistenceFailure_MarksTaskFailed()
    {
        var taskId = await AddRunningTaskAsync();
        // 删掉单页结果表，让结果写入失败（任务表仍可写）
        await QueryAsync(db => db.Database.ExecuteSqlRawAsync("DROP TABLE PageResults"));

        var sessionId = _sessionManager.ReplaceSession(NodeId);
        var stream = OpenStream(sessionId);
        await WaitUntilAsync(() => _coordinator.IsNodeConnected(NodeId), "流已登记");

        stream.Requests.Write(Result(taskId, 1));
        await WaitUntilAsync(() => { lock (_progressEvents) return _progressEvents.Any(e => e.TaskId == taskId && e.CurrentStatus == TaskStatus.Failed); }, "任务失败事件");

        Assert.Equal(TaskStatus.Failed, (await TaskAsync(taskId)).Status);
        Assert.DoesNotContain(stream.Responses.Written, m => m.MessageCase == FromMaster.MessageOneofCase.ResultAck);
        Assert.True(_coordinator.IsNodeConnected(NodeId), "单条结果落库失败不应中断整条流");

        stream.Requests.Complete();
        await stream.Call.WaitAsync(Timeout);
    }

    [Fact(DisplayName = "来自非执行节点的结果被丢弃，回执 accepted=false（M-04①）")]
    public async Task ResultFromNodeNotAssigned_IsDiscarded()
    {
        var task = new CrawlerCore.Entities.CrawlerTask { TaskName = "属于其他节点", Status = TaskStatus.Running, AssignedNode = "other-node" };
        await QueryAsync(async db => { db.CrawlerTasks.Add(task); return await db.SaveChangesAsync(); });

        var sessionId = _sessionManager.ReplaceSession(NodeId);
        var stream = OpenStream(sessionId);
        await WaitUntilAsync(() => _coordinator.IsNodeConnected(NodeId), "流已登记");

        stream.Requests.Write(Progress(task.TaskId, CrawlerCore.Protos.TaskStatus.Running, 1));
        stream.Requests.Write(Result(task.TaskId, 1));
        await WaitUntilAsync(() => stream.Responses.Written.Any(m => m.MessageCase == FromMaster.MessageOneofCase.ResultAck), "收到结果回执");

        Assert.False(stream.Responses.Written.Single(m => m.MessageCase == FromMaster.MessageOneofCase.ResultAck).ResultAck.Accepted);
        Assert.Equal(0, await QueryAsync(db => db.CrawlResults.CountAsync()));
        var stored = await TaskAsync(task.TaskId);
        Assert.Equal(TaskStatus.Running, stored.Status);
        Assert.Equal("other-node", stored.AssignedNode);
        lock (_progressEvents)
        {
            Assert.DoesNotContain(_progressEvents, e => e.TaskId == task.TaskId);
        }

        stream.Requests.Complete();
        await stream.Call.WaitAsync(Timeout);
    }
}
