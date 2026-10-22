using CrawlerCore.Protos;
using CrawlerWorker.Communication;
using Grpc.Core;

namespace CrawlerCore.Tests.Worker;

/// <summary>进程级发件箱（W-03）与连接决策规则（W-02）</summary>
public class WorkerOutboxTests
{
    /// <summary>只有“当前时间”可手动推进的时间源；计时器仍用系统实现</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 22, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static FromWorker Heartbeat() => new() { Heartbeat = new Heartbeat { NodeId = "n" } };

    private static FromWorker Result(string taskId) => new() { CrawlResult = new CrawlerCore.Protos.CrawlResult { TaskId = taskId } };

    private static FromWorker Progress(string taskId, CrawlerCore.Protos.TaskStatus status) => new() { TaskProgress = new TaskProgress { TaskId = taskId, CurrentStatus = status } };

    private static async Task<WorkerOutbox.OutboxItem?> TryReadAsync(WorkerOutbox outbox, int timeoutMs = 200)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            return await outbox.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>模拟写循环把所有可读消息写出，返回写出的消息</summary>
    private static async Task<List<FromWorker>> DrainAsync(WorkerOutbox outbox)
    {
        var written = new List<FromWorker>();
        while (await TryReadAsync(outbox) is { } item)
        {
            outbox.OnWritten(item);
            written.Add(item.Message);
        }
        return written;
    }

    [Fact(DisplayName = "即时消息（心跳、进度）只在有流时入队，断线期间丢弃")]
    public async Task TransientMessages_DroppedWhileDisconnected()
    {
        var outbox = new WorkerOutbox(TimeProvider.System);
        Assert.False(outbox.TrySendTransient(Heartbeat()));

        outbox.OnStreamAccepted();
        Assert.True(outbox.TrySendTransient(Heartbeat()));

        var written = await DrainAsync(outbox);
        Assert.Single(written);
    }

    [Fact(DisplayName = "断线期间产生的结果保留下来，建流后按顺序发出（结果在前、Completed 进度在后）")]
    public async Task ResultQueuedWhileDisconnected_SentAfterReconnect()
    {
        var outbox = new WorkerOutbox(TimeProvider.System);
        outbox.EnqueueResult("t1", [Result("t1"), Progress("t1", CrawlerCore.Protos.TaskStatus.Completed)], requiresAck: true);

        outbox.OnStreamAccepted();
        var written = await DrainAsync(outbox);

        Assert.Equal(new[] { FromWorker.MessageOneofCase.CrawlResult, FromWorker.MessageOneofCase.TaskProgress }, written.Select(m => m.MessageCase));
        Assert.True(outbox.HasPendingResult("t1"));
    }

    [Fact(DisplayName = "写出后连接断开、没有收到回执：新流建立后重发；收到回执后不再重发")]
    public async Task WrittenButUnacknowledged_ResentOnNextStream()
    {
        var outbox = new WorkerOutbox(TimeProvider.System);
        outbox.OnStreamAccepted();
        outbox.EnqueueResult("t1", [Result("t1")], requiresAck: true);
        Assert.Single(await DrainAsync(outbox));

        // 连接断开（结果可能随连接丢失），重新建流
        outbox.OnStreamClosed();
        outbox.OnStreamAccepted();
        Assert.Single(await DrainAsync(outbox));

        Assert.True(outbox.Acknowledge("t1"));
        outbox.OnStreamClosed();
        outbox.OnStreamAccepted();
        Assert.Empty(await DrainAsync(outbox));
        Assert.False(outbox.HasPendingResult("t1"));
    }

    [Fact(DisplayName = "写入失败的那条消息由下一条流先发")]
    public async Task FailedWrite_RetriedFirst()
    {
        var outbox = new WorkerOutbox(TimeProvider.System);
        outbox.OnStreamAccepted();
        outbox.EnqueueResult("t1", [Result("t1")], requiresAck: true);
        outbox.EnqueueResult("t2", [Result("t2")], requiresAck: true);

        var first = await TryReadAsync(outbox);
        outbox.OnWriteFailed(first!);
        outbox.OnStreamClosed();
        outbox.OnStreamAccepted();

        var written = await DrainAsync(outbox);
        Assert.Equal(new[] { "t1", "t2" }, written.Select(m => m.CrawlResult.TaskId));
    }

    [Fact(DisplayName = "收到回执后，队列中尚未写出的旧副本被跳过")]
    public async Task AcknowledgedEntry_StaleCopiesSkipped()
    {
        var outbox = new WorkerOutbox(TimeProvider.System);
        outbox.EnqueueResult("t1", [Result("t1")], requiresAck: true);
        outbox.Acknowledge("t1");
        outbox.OnStreamAccepted();

        Assert.Empty(await DrainAsync(outbox));
    }

    [Fact(DisplayName = "不需要回执的最终状态（Failed）写出后即完成")]
    public async Task FailedProgress_RemovedAfterWrite()
    {
        var outbox = new WorkerOutbox(TimeProvider.System);
        outbox.OnStreamAccepted();
        outbox.EnqueueResult("t1", [Progress("t1", CrawlerCore.Protos.TaskStatus.Failed)], requiresAck: false);

        Assert.Single(await DrainAsync(outbox));
        Assert.False(outbox.HasPendingResult("t1"));
    }

    [Fact(DisplayName = "写出后超过回执超时仍未确认的结果重发，之后的超时翻倍")]
    public async Task UnacknowledgedResult_ResentAfterTimeout()
    {
        var time = new ManualTimeProvider();
        var outbox = new WorkerOutbox(time);
        outbox.OnStreamAccepted();
        outbox.EnqueueResult("t1", [Result("t1")], requiresAck: true);
        await DrainAsync(outbox);

        time.Now += TimeSpan.FromSeconds(59);
        Assert.Equal(0, outbox.ResendUnacknowledged(TimeSpan.FromSeconds(60)));
        time.Now += TimeSpan.FromSeconds(2);
        Assert.Equal(1, outbox.ResendUnacknowledged(TimeSpan.FromSeconds(60)));
        Assert.Single(await DrainAsync(outbox));

        // 第二次重发要等 120 秒
        time.Now += TimeSpan.FromSeconds(61);
        Assert.Equal(0, outbox.ResendUnacknowledged(TimeSpan.FromSeconds(60)));
        time.Now += TimeSpan.FromSeconds(60);
        Assert.Equal(1, outbox.ResendUnacknowledged(TimeSpan.FromSeconds(60)));
    }

    [Fact(DisplayName = "停止时等待回执：收到回执后返回 true，断线时立即返回 false")]
    public async Task WaitForAcknowledgements()
    {
        var outbox = new WorkerOutbox(TimeProvider.System);
        outbox.OnStreamAccepted();
        outbox.EnqueueResult("t1", [Result("t1")], requiresAck: true);

        var waiting = outbox.WaitForAcknowledgementsAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        outbox.Acknowledge("t1");
        Assert.True(await waiting);

        outbox.EnqueueResult("t2", [Result("t2")], requiresAck: true);
        outbox.OnStreamClosed();
        Assert.False(await outbox.WaitForAcknowledgementsAsync(TimeSpan.FromSeconds(5), CancellationToken.None));
    }

    [Fact(DisplayName = "Complete 之后写循环读完已有消息即结束")]
    public async Task Complete_EndsReaderAfterDrain()
    {
        var outbox = new WorkerOutbox(TimeProvider.System);
        outbox.OnStreamAccepted();
        outbox.TrySendTransient(Heartbeat());
        outbox.Complete();

        Assert.NotNull(await outbox.ReadAsync(CancellationToken.None));
        Assert.Null(await outbox.ReadAsync(CancellationToken.None));
    }

    [Theory(DisplayName = "W-02：流结束的状态码决定处理方式")]
    [InlineData(StatusCode.PermissionDenied, StreamEndAction.ReRegister)]
    [InlineData(StatusCode.NotFound, StreamEndAction.ReRegister)]
    [InlineData(StatusCode.Unauthenticated, StreamEndAction.Exit)]
    [InlineData(StatusCode.Unavailable, StreamEndAction.Retry)]
    [InlineData(StatusCode.Internal, StreamEndAction.Retry)]
    [InlineData(StatusCode.AlreadyExists, StreamEndAction.Retry)]
    [InlineData(StatusCode.DeadlineExceeded, StreamEndAction.Retry)]
    [InlineData(null, StreamEndAction.Retry)]
    public void ClassifyStreamEnd(StatusCode? status, StreamEndAction expected)
        => Assert.Equal(expected, ConnectionPolicy.ClassifyStreamEnd(status));

    [Theory(DisplayName = "W-11：只有令牌错误、参数非法的注册失败才退出")]
    [InlineData(StatusCode.Unauthenticated, true)]
    [InlineData(StatusCode.InvalidArgument, true)]
    [InlineData(StatusCode.Unavailable, false)]
    [InlineData(StatusCode.AlreadyExists, false)]
    [InlineData(StatusCode.DeadlineExceeded, false)]
    public void FatalRegistrationErrors(StatusCode status, bool fatal)
        => Assert.Equal(fatal, ConnectionPolicy.IsFatalRegistrationError(status));

    [Fact(DisplayName = "W-02：退避从初始间隔开始翻倍，不超过上限，抖动在 ±20% 以内")]
    public void Backoff_ExponentialWithCapAndJitter()
    {
        var initial = TimeSpan.FromSeconds(1);
        var max = TimeSpan.FromSeconds(30);

        Assert.Equal(TimeSpan.FromSeconds(1), ConnectionPolicy.ComputeBackoff(1, initial, max, 0.5));
        Assert.Equal(TimeSpan.FromSeconds(2), ConnectionPolicy.ComputeBackoff(2, initial, max, 0.5));
        Assert.Equal(TimeSpan.FromSeconds(8), ConnectionPolicy.ComputeBackoff(4, initial, max, 0.5));
        Assert.Equal(TimeSpan.FromSeconds(30), ConnectionPolicy.ComputeBackoff(10, initial, max, 0.5));
        Assert.Equal(TimeSpan.FromSeconds(30), ConnectionPolicy.ComputeBackoff(100, initial, max, 0.99));
        Assert.Equal(TimeSpan.FromSeconds(0.8), ConnectionPolicy.ComputeBackoff(1, initial, max, 0));
        Assert.True(ConnectionPolicy.ComputeBackoff(1, initial, max, 0.999) < TimeSpan.FromSeconds(1.2));
        Assert.Equal(TimeSpan.Zero, ConnectionPolicy.ComputeBackoff(0, initial, max, 0.5));
    }
}
