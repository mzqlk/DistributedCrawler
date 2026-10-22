using CrawlerCore.Entities;
using CrawlerCore.Events;
using CrawlerCore.Protos;
using CrawlerCore.Tests.Integration.Infrastructure;
using CrawlerMaster.WPF.Services;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;
using TaskStatus = CrawlerCore.Entities.TaskStatus;
using ProtoTaskStatus = CrawlerCore.Protos.TaskStatus;

namespace CrawlerCore.Tests.Integration;

/// <summary>
/// Master 可靠性回归测试（路线图 T4，对应 docs/review/master.md 的 M 编号）：
/// 认证、任务状态机、旧节点隔离、负载均衡与并发上限、断流与失联后的恢复、重启恢复。
/// 只与 Master 有关的行为用 <see cref="RawNodeClient"/>（直接调用 gRPC 接口的假节点）验证，端到端行为用真实 Worker 验证。
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait(IntegrationTestCollection.Category, IntegrationTestCollection.Integration)]
public sealed class MasterReliabilityTests : IntegrationTestBase
{
    public MasterReliabilityTests(ITestOutputHelper output) : base(output)
    {
    }

    #region 认证（M-01、M-09）
    [Fact]
    public async Task 缺少令牌的StartStreaming被拒绝()
    {
        var master = await StartMasterAsync();
        var intruder = CreateRawNode(master, "it-intruder", token: null);

        intruder.StartStream(sessionId: "guessed-session");
        await intruder.WaitForStreamEndAsync("没有令牌的流被 Master 结束");

        var error = Assert.IsType<RpcException>(intruder.StreamError);
        Assert.Equal(StatusCode.Unauthenticated, error.StatusCode);
    }

    [Fact]
    public async Task 令牌正确但会话无效的StartStreaming返回PermissionDenied而不是Internal()
    {
        var master = await StartMasterAsync();
        var node = CreateRawNode(master, "it-stale-session");

        node.StartStream(sessionId: "session-from-previous-master");
        await node.WaitForStreamEndAsync("会话无效的流被 Master 结束");

        // Worker 依赖这个状态码决定重新注册（T5 修复 D1 时使用）
        var error = Assert.IsType<RpcException>(node.StreamError);
        Assert.Equal(StatusCode.PermissionDenied, error.StatusCode);
    }
    #endregion

    #region 旧节点隔离与结果去重（M-04、M-10、M-11、结果回执）
    [Fact]
    public async Task 被改派后旧节点发来的进度和结果被丢弃_任务只保留新节点的结果()
    {
        var master = await StartMasterAsync();
        var nodeA = CreateRawNode(master, "it-raw-a");
        await ConnectAsync(master, nodeA);

        var task = await master.ScheduleTaskAsync(NewSiteTask("隔离旧节点"));
        await nodeA.WaitForMessageAsync(m => m.NewTask?.TaskId == task.TaskId, "任务下发给 A");
        await nodeA.SendProgressAsync(task.TaskId, ProtoTaskStatus.Running, crawledPages: 1);

        // A 断开（流结束）→ 判离线 → 任务被重置
        await nodeA.DisposeAsync();
        await master.WaitForNodeStatusAsync(nodeA.NodeId, NodeStatus.Offline);
        await master.WaitForTaskStatusAsync(task.TaskId, TaskStatus.Pending);

        using var progress = master.RecordTaskProgress();
        var nodeB = CreateRawNode(master, "it-raw-b");
        await ConnectAsync(master, nodeB);
        await nodeB.WaitForMessageAsync(m => m.NewTask?.TaskId == task.TaskId, "任务改派给 B");

        // A 恢复后重新注册（没有活跃流，允许），继续发送它那份执行的进度与结果
        var nodeAAgain = CreateRawNode(master, "it-raw-a");
        await ConnectAsync(master, nodeAAgain);
        await nodeAAgain.SendProgressAsync(task.TaskId, ProtoTaskStatus.Running, crawledPages: 3);
        await nodeAAgain.SendProgressAsync(task.TaskId, ProtoTaskStatus.Completed, crawledPages: 4);
        await nodeAAgain.SendResultAsync(task.TaskId);
        var rejected = await nodeAAgain.WaitForMessageAsync(m => m.ResultAck?.TaskId == task.TaskId, "A 收到结果回执");
        Assert.False(rejected.ResultAck.Accepted);

        await nodeB.SendProgressAsync(task.TaskId, ProtoTaskStatus.Running, crawledPages: 1);
        await nodeB.SendProgressAsync(task.TaskId, ProtoTaskStatus.Completed, crawledPages: 1);
        await nodeB.SendResultAsync(task.TaskId);
        var accepted = await nodeB.WaitForMessageAsync(m => m.ResultAck?.TaskId == task.TaskId, "B 收到结果回执");
        Assert.True(accepted.ResultAck.Accepted);

        var stored = await master.GetTaskAsync(task.TaskId);
        Assert.Equal(TaskStatus.Completed, stored!.Status);
        Assert.Equal(nodeB.NodeId, stored.AssignedNode);
        Assert.Equal(1, await master.CountCrawlResultsAsync(task.TaskId));
        var page = Assert.Single((await master.GetPageResultsAsync(task.TaskId))!);
        Assert.EndsWith(nodeB.NodeId, page.Url);
        Assert.All(progress.For(task.TaskId), e => Assert.Equal(nodeB.NodeId, e.AssignedNodeId));
    }

    [Fact]
    public async Task Completed进度之后结果未到时任务保持Running_结果落库后才完成并回执()
    {
        var master = await StartMasterAsync();
        var node = CreateRawNode(master, "it-raw-result");
        await ConnectAsync(master, node);

        var task = await master.ScheduleTaskAsync(NewSiteTask("完成以结果为准"));
        await node.WaitForMessageAsync(m => m.NewTask?.TaskId == task.TaskId, "任务下发");
        await node.SendProgressAsync(task.TaskId, ProtoTaskStatus.Completed, crawledPages: 1);

        // 进度与心跳在同一条流上按序处理：心跳落库后，Completed 进度一定已经处理完
        var before = DateTime.UtcNow;
        await node.SendHeartbeatAsync();
        await Poll.UntilAsync(async () => (await master.GetNodeAsync(node.NodeId))!.LastHeartbeat > before, "后续心跳已处理");
        Assert.Equal(TaskStatus.Running, (await master.GetTaskAsync(task.TaskId))!.Status);
        Assert.Equal(0, await master.CountCrawlResultsAsync(task.TaskId));

        await node.SendResultAsync(task.TaskId);
        var ack = await node.WaitForMessageAsync(m => m.ResultAck?.TaskId == task.TaskId, "结果回执");
        Assert.True(ack.ResultAck.Accepted);
        Assert.Equal(TaskStatus.Completed, (await master.GetTaskAsync(task.TaskId))!.Status);
        Assert.Equal(1, await master.CountCrawlResultsAsync(task.TaskId));
    }

    [Fact]
    public async Task 短暂断流后Worker用旧会话重连_重复执行的任务只保留一份结果且完成后状态不回退()
    {
        var master = await StartMasterAsync();
        var worker = await StartWorkerAsync("it-node-a", master);
        await master.WaitForNodeOnlineAsync("it-node-a");

        Site.HoldRequests();
        using var progress = master.RecordTaskProgress();
        var task = await master.ScheduleTaskAsync(NewSiteTask("断流重连"));
        await master.WaitForTaskStatusAsync(task.TaskId, TaskStatus.Running);
        await Site.WaitForHeldRequestAsync(Poll.DefaultTimeout);

        // 网络抖动：连接被重置 → Master 立即判离线并重置任务 → 连接恢复后 Worker 用旧会话重连（M-03：会话未被删除）
        worker.Proxy.Sever();
        await master.WaitForNodeStatusAsync("it-node-a", NodeStatus.Offline);
        worker.Proxy.Restore();
        await master.WaitForNodeStatusAsync("it-node-a", NodeStatus.Online);
        Site.ReleaseRequests();

        var pages = await master.WaitForTaskCompletedAsync(task.TaskId);
        Assert.Equal(TestWebSite.DepthTwoPages.Length, pages.Count);

        // 等两次执行都结束（心跳显示没有活跃任务），再确认只有一份结果、状态仍为 Completed
        var completedAt = DateTime.UtcNow;
        await Poll.UntilAsync(async () =>
        {
            var node = await master.GetNodeAsync("it-node-a");
            return node != null && node.LastHeartbeat > completedAt && node.ActiveTasks == 0;
        }, "两次执行都已结束");
        Assert.Equal(1, await master.CountCrawlResultsAsync(task.TaskId));
        Assert.Equal(TaskStatus.Completed, (await master.GetTaskAsync(task.TaskId))!.Status);

        var events = progress.For(task.TaskId);
        var firstCompleted = events.ToList().FindIndex(e => e.CurrentStatus == TaskStatus.Completed);
        Assert.True(firstCompleted >= 0, "应发布 Completed 事件");
        Assert.Equal(firstCompleted, events.Count - 1);
    }

    [Fact]
    public async Task 被判离线的节点恢复后_其进度和结果被丢弃_任务只在幸存节点上有一份结果()
    {
        var master = await StartMasterAsync();
        var workers = new[]
        {
            await StartWorkerAsync("it-node-a", master),
            await StartWorkerAsync("it-node-b", master),
        };
        await master.WaitForNodeOnlineAsync("it-node-a");
        await master.WaitForNodeOnlineAsync("it-node-b");

        Site.HoldRequests();
        using var progress = master.RecordTaskProgress();
        var task = await master.ScheduleTaskAsync(NewSiteTask("隔离恢复的旧节点"));
        var running = await Poll.ValueAsync(
            () => Task.FromResult(progress.For(task.TaskId).FirstOrDefault(e => e.CurrentStatus == TaskStatus.Running)),
            "任务开始运行");
        await Site.WaitForHeldRequestAsync(Poll.DefaultTimeout);
        var victim = workers.Single(w => w.NodeId == running.AssignedNodeId);
        var survivor = workers.Single(w => w.NodeId != running.AssignedNodeId);

        victim.Proxy.Sever();
        await master.WaitForNodeStatusAsync(victim.NodeId, NodeStatus.Offline);
        await Poll.UntilAsync(async () => (await master.GetTaskAsync(task.TaskId))?.AssignedNode == survivor.NodeId, "任务改派给幸存节点");

        // 旧节点恢复：它那份执行仍在进行，之后的进度和结果都应被丢弃
        victim.Proxy.Restore();
        await master.WaitForNodeStatusAsync(victim.NodeId, NodeStatus.Online);
        Site.ReleaseRequests();

        var pages = await master.WaitForTaskCompletedAsync(task.TaskId);
        Assert.Equal(TestWebSite.DepthTwoPages.Length, pages.Count);
        var settled = DateTime.UtcNow;
        await Poll.UntilAsync(async () =>
        {
            var node = await master.GetNodeAsync(victim.NodeId);
            return node != null && node.LastHeartbeat > settled && node.ActiveTasks == 0;
        }, "旧节点上的执行已结束");

        var stored = await master.GetTaskAsync(task.TaskId);
        Assert.Equal(TaskStatus.Completed, stored!.Status);
        Assert.Equal(survivor.NodeId, stored.AssignedNode);
        Assert.Equal(1, await master.CountCrawlResultsAsync(task.TaskId));
    }
    #endregion

    #region 本地排队的任务（M-02）
    [Fact]
    public async Task Worker本地排队的任务在节点崩溃后由另一节点完成()
    {
        var master = await StartMasterAsync();
        // T5 起 Worker 注册时上报并发上限，Master 不再超额分配（W-22 ②）。这里让节点上报 0（不限，相当于旧版 Worker），
        // 才能构造出“在 Worker 本地排队”的任务
        var workers = new[]
        {
            await StartWorkerAsync("it-node-a", master, maxConcurrentTasks: 1, reportedMaxConcurrentTasks: 0),
            await StartWorkerAsync("it-node-b", master, maxConcurrentTasks: 1, reportedMaxConcurrentTasks: 0),
        };
        await master.WaitForNodeOnlineAsync("it-node-a");
        await master.WaitForNodeOnlineAsync("it-node-b");

        Site.HoldRequests();
        var tasks = new List<CrawlerCore.Entities.CrawlerTask>();
        for (var i = 1; i <= 3; i++)
        {
            tasks.Add(await master.ScheduleTaskAsync(NewSiteTask($"本地排队-{i}")));
        }

        // 3 个任务分给 2 个节点：一个节点执行 1 个、在本地排队 1 个（Worker 并发为 1）
        var assignments = await Poll.ValueAsync(async () =>
        {
            var stored = await Task.WhenAll(tasks.Select(t => master.GetTaskAsync(t.TaskId)));
            return stored.All(t => t is { Status: TaskStatus.Running } && !string.IsNullOrEmpty(t.AssignedNode))
                ? stored.Select(t => t!).ToList()
                : null;
        }, "3 个任务都已分配");
        var busiest = assignments.GroupBy(t => t.AssignedNode).OrderByDescending(g => g.Count()).First();
        Assert.Equal(2, busiest.Count());
        var victim = workers.Single(w => w.NodeId == busiest.Key);
        var survivor = workers.Single(w => w.NodeId != busiest.Key);

        await victim.CrashAsync();
        await master.WaitForNodeStatusAsync(victim.NodeId, NodeStatus.Offline);
        Site.ReleaseRequests();

        foreach (var task in tasks)
        {
            var pages = await master.WaitForTaskCompletedAsync(task.TaskId, TimeSpan.FromSeconds(40));
            Assert.Equal(TestWebSite.DepthTwoPages.Length, pages.Count);
            Assert.Equal(1, await master.CountCrawlResultsAsync(task.TaskId));
        }
        foreach (var task in busiest)
        {
            Assert.Equal(survivor.NodeId, (await master.GetTaskAsync(task.TaskId))!.AssignedNode);
        }
    }
    #endregion

    #region 负载均衡与并发上限（M-06）
    [Fact]
    public async Task 一次创建4个任务时两个节点各分到2个()
    {
        var master = await StartMasterAsync();
        await StartWorkerAsync("it-node-a", master);
        await StartWorkerAsync("it-node-b", master);
        await master.WaitForNodeOnlineAsync("it-node-a");
        await master.WaitForNodeOnlineAsync("it-node-b");

        Site.HoldRequests();
        var tasks = new List<CrawlerCore.Entities.CrawlerTask>();
        for (var i = 1; i <= 4; i++)
        {
            tasks.Add(await master.ScheduleTaskAsync(NewSiteTask($"均衡-{i}")));
        }

        var assignments = await Poll.ValueAsync(async () =>
        {
            var stored = await Task.WhenAll(tasks.Select(t => master.GetTaskAsync(t.TaskId)));
            return stored.All(t => t is { Status: TaskStatus.Running }) ? stored.Select(t => t!.AssignedNode).ToList() : null;
        }, "4 个任务都已分配");

        Assert.Equal(2, assignments.Count(n => n == "it-node-a"));
        Assert.Equal(2, assignments.Count(n => n == "it-node-b"));
        Site.ReleaseRequests();
        foreach (var task in tasks)
        {
            await master.WaitForTaskCompletedAsync(task.TaskId);
        }
    }

    [Fact]
    public async Task 任务数已达并发上限的节点不再分配任务()
    {
        var master = await StartMasterAsync();
        // A 资源更空闲，但并发上限为 1；B 不限
        var nodeA = CreateRawNode(master, "it-raw-capacity-a");
        await ConnectAsync(master, nodeA, maxConcurrentTasks: 1);
        var nodeB = CreateRawNode(master, "it-raw-capacity-b");
        await ConnectAsync(master, nodeB);
        await nodeB.SendAsync(new FromWorker { Heartbeat = new Heartbeat { NodeId = nodeB.NodeId, CpuUsage = 40, MemoryUsageMb = TestNodeInfoCollector.FixedMemoryUsageMb } });
        await Poll.UntilAsync(async () => (await master.GetNodeAsync(nodeB.NodeId))!.CpuUsage == 40, "B 的负载已更新");

        // t1 → A（任务数相同时 A 得分高）；t2 → B（A 已有 1 个）；
        // t3：两者都有 1 个，按得分本该给 A，但 A 已满 → 必须给 B
        var t1 = await master.ScheduleTaskAsync(NewSiteTask("上限-1"));
        await nodeA.WaitForMessageAsync(m => m.NewTask?.TaskId == t1.TaskId, "t1 下发给 A");
        var t2 = await master.ScheduleTaskAsync(NewSiteTask("上限-2"));
        await nodeB.WaitForMessageAsync(m => m.NewTask?.TaskId == t2.TaskId, "t2 下发给 B");
        var t3 = await master.ScheduleTaskAsync(NewSiteTask("上限-3"));
        await nodeB.WaitForMessageAsync(m => m.NewTask?.TaskId == t3.TaskId, "t3 下发给 B");

        Assert.Equal(new[] { t1.TaskId }, nodeA.ReceivedTaskIds);
        Assert.Equal(nodeB.NodeId, (await master.GetTaskAsync(t3.TaskId))!.AssignedNode);
    }
    #endregion

    #region 失联节点恢复（M-05）
    [Fact]
    public async Task 失联节点被判离线后Master断开其流_网络恢复后节点重新上线()
    {
        var master = await StartMasterAsync();
        var worker = await StartWorkerAsync("it-node-a", master);
        await master.WaitForNodeOnlineAsync("it-node-a");

        worker.Hang();
        await master.WaitForNodeStatusAsync("it-node-a", NodeStatus.Offline);
        // 健康检查判离线后主动断开 Master 侧的流，否则节点恢复后新流会被拒绝
        await Poll.UntilAsync(() => !master.Coordinator.IsNodeConnected("it-node-a"), "Master 断开失联节点的流");

        worker.Proxy.Restore();
        await master.WaitForNodeStatusAsync("it-node-a", NodeStatus.Online, TimeSpan.FromSeconds(30));
        await Poll.UntilAsync(() => master.Coordinator.IsNodeConnected("it-node-a"), "节点重新建立双向流");

        var task = await master.ScheduleTaskAsync(NewSiteTask("恢复后继续接收任务"));
        var pages = await master.WaitForTaskCompletedAsync(task.TaskId);
        Assert.Equal(TestWebSite.DepthTwoPages.Length, pages.Count);
    }
    #endregion

    #region 重启恢复（M-02②、M-17④）
    [Fact]
    public async Task Master重启后恢复已分配但未完成的任务_并跳过没有活跃流的旧在线节点()
    {
        var firstMaster = await StartMasterAsync("Master-1");
        var pendingAssigned = NewSiteTask("重启恢复-排队中");
        var runningOnGhost = NewSiteTask("重启恢复-执行节点已消失");
        await firstMaster.QueryAsync(async db =>
        {
            // 旧版本留下的数据：“Pending + 已分配”的排队任务、执行节点已不在线的 Running 任务，
            // 以及一个库中仍为 Online、资源最空闲、但再也不会连上来的节点
            db.NodeInfos.Add(new NodeInfo { NodeId = "it-ghost", Status = NodeStatus.Online, TotalMemoryGb = 64, CpuUsage = 0, LastHeartbeat = DateTime.UtcNow });
            pendingAssigned.Status = TaskStatus.Pending;
            pendingAssigned.AssignedNode = "it-ghost";
            runningOnGhost.Status = TaskStatus.Running;
            runningOnGhost.AssignedNode = "it-vanished";
            db.CrawlerTasks.AddRange(pendingAssigned, runningOnGhost);
            return await db.SaveChangesAsync();
        });
        await firstMaster.StopAsync();

        var secondMaster = await StartMasterAsync("Master-2");
        await StartWorkerAsync("it-node-a", secondMaster);
        await secondMaster.WaitForNodeOnlineAsync("it-node-a");

        foreach (var task in new[] { pendingAssigned, runningOnGhost })
        {
            var pages = await secondMaster.WaitForTaskCompletedAsync(task.TaskId);
            Assert.Equal(TestWebSite.DepthTwoPages.Length, pages.Count);
            Assert.Equal("it-node-a", (await secondMaster.GetTaskAsync(task.TaskId))!.AssignedNode);
        }
    }
    #endregion

    #region 辅助方法
    /// <summary>假节点注册、建流、发送首个心跳，并等待 Master 中节点为 Online</summary>
    private static async Task ConnectAsync(TestMaster master, RawNodeClient node, int maxConcurrentTasks = 0)
    {
        var registration = await node.RegisterAsync(maxConcurrentTasks);
        Assert.True(registration.Success, $"节点 {node.NodeId} 注册失败：{registration.Message}");
        node.StartStream();
        await Poll.UntilAsync(() => master.Coordinator.IsNodeConnected(node.NodeId), $"节点 {node.NodeId} 建立双向流");
        await node.SendHeartbeatAsync();
        await master.WaitForNodeOnlineAsync(node.NodeId);
    }
    #endregion
}
