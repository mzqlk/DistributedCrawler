using CrawlerCore.Entities;
using CrawlerCore.Events;
using CrawlerCore.Tests.Integration.Infrastructure;
using CrawlerMaster.WPF.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;
using TaskStatus = CrawlerCore.Entities.TaskStatus;

namespace CrawlerCore.Tests.Integration;

/// <summary>
/// Master–Worker 端到端集成测试：进程内启动真实的 Master 后台服务与 1~2 个真实 Worker，
/// Worker 用 MockTestPlugin 爬取本地 Kestrel 静态网站，结果写入临时 SQLite 文件。
/// 单独运行：dotnet test --filter "Category=Integration"
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait(IntegrationTestCollection.Category, IntegrationTestCollection.Integration)]
public sealed class MasterWorkerEndToEndTests : IntegrationTestBase
{
    public MasterWorkerEndToEndTests(ITestOutputHelper output) : base(output)
    {
    }

    [Fact]
    public async Task 注册并上报心跳后节点变为Online()
    {
        var master = await StartMasterAsync();
        var statusEvents = new List<NodeStatusChangedEventArgs>();
        master.Events.Subscribe<NodeStatusChangedEventArgs>((_, e) => { lock (statusEvents) statusEvents.Add(e); });

        await StartWorkerAsync("it-node-a", master);

        var node = await master.WaitForNodeOnlineAsync("it-node-a");
        Assert.Equal(Environment.MachineName, node.MachineName);
        Assert.True(node.CpuCores > 0, "注册信息应包含 CPU 核数");
        Assert.True(node.TotalMemoryGb > 0, "注册信息应包含总内存");
        Assert.Equal(TestNodeInfoCollector.FixedMemoryUsageMb, node.MemoryUsageMb);
        Assert.Contains("it-node-a", master.Services.GetRequiredService<SessionManager>().GetAllOnlineNodeIds());
        lock (statusEvents)
        {
            Assert.Contains(statusEvents, e => e.NodeId == "it-node-a" && e.NewStatus == NodeStatus.Online);
        }

        // 心跳持续到达：LastHeartbeat 不断刷新，节点保持 Online
        var firstHeartbeat = node.LastHeartbeat;
        await Poll.UntilAsync(async () =>
        {
            var current = await master.GetNodeAsync("it-node-a");
            return current is { Status: NodeStatus.Online } && current.LastHeartbeat > firstHeartbeat;
        }, "后续心跳刷新 LastHeartbeat");
    }

    [Fact]
    public async Task 创建任务后分配并爬取_结果落库_状态为Completed且进度不回退()
    {
        var master = await StartMasterAsync();
        await StartWorkerAsync("it-node-a", master);
        await master.WaitForNodeOnlineAsync("it-node-a");

        using var progress = master.RecordTaskProgress();
        var task = await master.ScheduleTaskAsync(NewSiteTask("端到端-单节点"));

        var pages = await master.WaitForTaskCompletedAsync(task.TaskId);
        var completedAt = DateTime.UtcNow;

        // 1. 任务状态与分配节点
        var storedTask = await master.GetTaskAsync(task.TaskId);
        Assert.NotNull(storedTask);
        Assert.Equal(TaskStatus.Completed, storedTask.Status);
        Assert.Equal("it-node-a", storedTask.AssignedNode);

        // 2. 结果落库：深度 2 只爬首页与 a/b/c，重复链接、锚点、mailto 被过滤，深度 3 的页面不被请求
        Assert.Equal(1, await master.CountCrawlResultsAsync(task.TaskId));
        AssertPagesMatchDepthTwoSite(pages);
        var index = pages.Single(p => p.Url == Site.PageUrl("index.html"));
        Assert.Equal(
            new[] { "a.html", "b.html", "c.html", "index.html#top" }.Select(Site.PageUrl).OrderBy(u => u),
            index.Links.OrderBy(u => u));
        Assert.DoesNotContain("/site/a1.html", Site.RequestedPaths);
        Assert.DoesNotContain("/site/b1.html", Site.RequestedPaths);

        // 3. 进度不回退：Master 发布给界面的进度事件中，状态与已爬页数都单调不减，最后一条是 Completed
        var events = progress.For(task.TaskId);
        AssertProgressMonotonic(events);
        Assert.Equal(TaskStatus.Completed, events[^1].CurrentStatus);
        Assert.Equal(TestWebSite.DepthTwoPages.Length, events[^1].CrawledPages);
        Assert.Contains(events, e => e.CurrentStatus == TaskStatus.Running && e.CrawledPages > 0);
        Assert.All(events, e => Assert.Equal("it-node-a", e.AssignedNodeId));

        // 4. 完成之后没有迟到的旧进度把状态改回去：等 Worker 在完成后再发一次心跳（同一条流按序处理，
        //    此前的任务消息必然已处理完），且心跳显示已无活跃任务，再确认状态仍为 Completed
        await Poll.UntilAsync(async () =>
        {
            var node = await master.GetNodeAsync("it-node-a");
            return node != null && node.LastHeartbeat > completedAt && node.ActiveTasks == 0;
        }, "任务完成后的下一次心跳（活跃任务数为 0）");
        Assert.Equal(TaskStatus.Completed, (await master.GetTaskAsync(task.TaskId))!.Status);
        Assert.Equal(events.Count, progress.For(task.TaskId).Count);
        Assert.Equal(1, await master.CountCrawlResultsAsync(task.TaskId));
    }

    [Fact]
    public async Task Worker崩溃后被判离线_任务重置并由另一节点完成()
    {
        var master = await StartMasterAsync();
        var workers = new[]
        {
            await StartWorkerAsync("it-node-a", master),
            await StartWorkerAsync("it-node-b", master),
        };
        await master.WaitForNodeOnlineAsync("it-node-a");
        await master.WaitForNodeOnlineAsync("it-node-b");

        var (task, victim, survivor) = await StartTaskAndHoldItRunningAsync(master, workers, "端到端-节点崩溃");

        // 崩溃：连接被重置，Master 的流随即结束并把节点标记为离线
        await victim.CrashAsync();
        await master.WaitForNodeStatusAsync(victim.NodeId, NodeStatus.Offline);
        Site.ReleaseRequests();

        var pages = await master.WaitForTaskCompletedAsync(task.TaskId);
        AssertPagesMatchDepthTwoSite(pages);
        var storedTask = await master.GetTaskAsync(task.TaskId);
        Assert.Equal(survivor.NodeId, storedTask!.AssignedNode);
        Assert.Equal(1, await master.CountCrawlResultsAsync(task.TaskId));
        Assert.Equal(NodeStatus.Online, (await master.GetNodeAsync(survivor.NodeId))!.Status);
    }

    [Fact]
    public async Task Worker失联后由健康检查判定离线_任务重置并由另一节点完成()
    {
        var master = await StartMasterAsync();
        var workers = new[]
        {
            await StartWorkerAsync("it-node-a", master),
            await StartWorkerAsync("it-node-b", master),
        };
        await master.WaitForNodeOnlineAsync("it-node-a");
        await master.WaitForNodeOnlineAsync("it-node-b");

        var (task, victim, survivor) = await StartTaskAndHoldItRunningAsync(master, workers, "端到端-节点失联");

        // 失联：TCP 连接仍在，流不会结束，只有健康检查能发现（测试中超时缩短为数秒）
        victim.Hang();
        await master.WaitForNodeStatusAsync(victim.NodeId, NodeStatus.Offline);
        Assert.True(victim.Proxy.ActiveConnectionCount > 0, "失联场景下连接应仍然保持，离线由健康检查判定");
        Site.ReleaseRequests();

        var pages = await master.WaitForTaskCompletedAsync(task.TaskId);
        AssertPagesMatchDepthTwoSite(pages);
        var storedTask = await master.GetTaskAsync(task.TaskId);
        Assert.Equal(survivor.NodeId, storedTask!.AssignedNode);
        Assert.Equal(1, await master.CountCrawlResultsAsync(task.TaskId));
        Assert.Equal(NodeStatus.Online, (await master.GetNodeAsync(survivor.NodeId))!.Status);
    }

    [Fact]
    public async Task Master重启后恢复Pending任务并完成()
    {
        // 第一次启动：没有任何 Worker，任务只能停留在 Pending
        var firstMaster = await StartMasterAsync("Master-1");
        var low = await firstMaster.ScheduleTaskAsync(NewSiteTask("端到端-重启恢复-低优先级", priority: 2));
        var high = await firstMaster.ScheduleTaskAsync(NewSiteTask("端到端-重启恢复-高优先级", priority: 5));
        foreach (var id in new[] { low.TaskId, high.TaskId })
        {
            var stored = await firstMaster.GetTaskAsync(id);
            Assert.Equal(TaskStatus.Pending, stored!.Status);
            Assert.True(string.IsNullOrEmpty(stored.AssignedNode));
        }
        await firstMaster.StopAsync();

        // 第二次启动：同一个数据库文件，内存队列为空，只能靠启动时从数据库恢复
        var secondMaster = await StartMasterAsync("Master-2");
        await StartWorkerAsync("it-node-a", secondMaster);
        await secondMaster.WaitForNodeOnlineAsync("it-node-a");

        foreach (var id in new[] { low.TaskId, high.TaskId })
        {
            var pages = await secondMaster.WaitForTaskCompletedAsync(id);
            AssertPagesMatchDepthTwoSite(pages);
            Assert.Equal("it-node-a", (await secondMaster.GetTaskAsync(id))!.AssignedNode);
        }
    }

    #region 辅助方法

    /// <summary>
    /// 扣住测试网站的请求后创建任务，等任务在某个节点上真正开始执行（Master 已收到该节点的 Running 进度，
    /// 且该节点的首个页面请求已到达网站），返回执行它的节点（victim）与另一个节点（survivor）。
    /// </summary>
    private async Task<(CrawlerCore.Entities.CrawlerTask Task, TestWorker Victim, TestWorker Survivor)> StartTaskAndHoldItRunningAsync(
        TestMaster master, TestWorker[] workers, string taskName)
    {
        Site.HoldRequests();
        using var progress = master.RecordTaskProgress();
        var task = await master.ScheduleTaskAsync(NewSiteTask(taskName));

        var running = await Poll.ValueAsync(
            () => Task.FromResult(progress.For(task.TaskId).FirstOrDefault(e => e.CurrentStatus == TaskStatus.Running)),
            "任务在某个节点上开始运行");
        await Site.WaitForHeldRequestAsync(Poll.DefaultTimeout);

        var victim = workers.Single(w => w.NodeId == running.AssignedNodeId);
        var survivor = workers.Single(w => w.NodeId != running.AssignedNodeId);
        await Poll.UntilAsync(async () =>
        {
            var stored = await master.GetTaskAsync(task.TaskId);
            return stored is { Status: TaskStatus.Running } && stored.AssignedNode == victim.NodeId;
        }, $"数据库中任务为 Running 且分配给 {victim.NodeId}");

        return (task, victim, survivor);
    }

    private void AssertPagesMatchDepthTwoSite(List<PageResult> pages)
    {
        Assert.Equal(
            TestWebSite.DepthTwoPages.Select(Site.PageUrl).OrderBy(u => u),
            pages.Select(p => p.Url).OrderBy(u => u));
        Assert.All(pages, page =>
        {
            Assert.Equal(200, page.StatusCode);
            Assert.Equal(page.Url, page.FinalUrl);
            // MockTestPlugin 把页面 URL 写入解析结果
            Assert.True(page.ParsedData.TryGetValue("Url", out var parsedUrl), "ParsedData 应包含插件输出的 Url");
            Assert.Equal(page.Url, parsedUrl?.ToString());
        });
    }

    private static void AssertProgressMonotonic(IReadOnlyList<TaskProgressUpdatedEventArgs> events)
    {
        Assert.NotEmpty(events);
        for (var i = 1; i < events.Count; i++)
        {
            var previous = events[i - 1];
            var current = events[i];
            Assert.True(StatusRank(current.CurrentStatus) >= StatusRank(previous.CurrentStatus),
                $"任务状态回退：第 {i} 条 {previous.CurrentStatus} → 第 {i + 1} 条 {current.CurrentStatus}");
            Assert.True(current.CrawledPages >= previous.CrawledPages,
                $"已爬页数回退：第 {i} 条 {previous.CrawledPages} → 第 {i + 1} 条 {current.CrawledPages}");
        }
    }

    /// <summary>状态先后顺序：Pending → Running → Completed/Failed</summary>
    private static int StatusRank(TaskStatus status) => status switch
    {
        TaskStatus.Pending => 0,
        TaskStatus.Running => 1,
        _ => 2
    };

    #endregion
}
