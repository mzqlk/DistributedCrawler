using CrawlerCore.Entities;
using CrawlerCore.Protos;
using CrawlerCore.Tests.Integration.Infrastructure;
using Grpc.Core;
using Grpc.Net.Client;
using Xunit.Abstractions;
using TaskStatus = CrawlerCore.Entities.TaskStatus;

namespace CrawlerCore.Tests.Integration;

/// <summary>
/// 集成测试暴露出的生产代码缺陷。每个测试描述“修复后应有的行为”，未修复的用 Skip 跳过；
/// 修复对应缺陷后去掉 Skip，即成为回归测试。缺陷说明见 docs/project/overview.md “已知问题”。
/// D3、D4 属于 Master，已在 T4 修复；D1（W-02）、D2（W-01）属于 Worker，已在 T5 修复。
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait(IntegrationTestCollection.Category, IntegrationTestCollection.Integration)]
public sealed class MasterWorkerKnownDefectTests : IntegrationTestBase
{
    public MasterWorkerKnownDefectTests(ITestOutputHelper output) : base(output)
    {
    }

    /// <summary>D1（W-02，T5 已修复）：会话失效（PermissionDenied）时立即重新注册；Master 不可达期间按退避一直重试</summary>
    [Fact]
    public async Task Master重启后在线Worker自动重新注册并继续接收任务()
    {
        var firstMaster = await StartMasterAsync("Master-1");
        var worker = await StartWorkerAsync("it-node-a", firstMaster);
        await firstMaster.WaitForNodeOnlineAsync("it-node-a");

        // Master 进程崩溃：连接被重置；随后在新端口重新启动（同一数据库）。
        // 先确认节点在库中已是 Offline，保证后面看到的 Online 只能来自重新注册或新心跳
        worker.Proxy.Sever();
        await firstMaster.WaitForNodeStatusAsync("it-node-a", NodeStatus.Offline);
        await firstMaster.StopAsync();
        var secondMaster = await StartMasterAsync("Master-2");
        worker.Proxy.Restore(secondMaster.GrpcPort);

        // Worker 用旧会话重连会被拒绝（PermissionDenied），应立即重新注册
        await secondMaster.WaitForNodeOnlineAsync("it-node-a");
        var task = await secondMaster.ScheduleTaskAsync(NewSiteTask("缺陷-Master重启后重连"));
        var pages = await secondMaster.WaitForTaskCompletedAsync(task.TaskId);
        Assert.Equal(TestWebSite.DepthTwoPages.Length, pages.Count);
    }

    /// <summary>
    /// D2（W-01，T5 已修复）：停止时取消的任务不上报任何终态，流正常结束后 Master 判离线、重置任务，由另一节点完成；
    /// 停止过程不抛异常（TestWorker.StopAsync 不再吞异常）
    /// </summary>
    [Fact]
    public async Task Worker正常停止时正在执行的任务由另一节点重新完成()
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
        var task = await master.ScheduleTaskAsync(NewSiteTask("缺陷-Worker正常停止"));
        var running = await Poll.ValueAsync(
            () => Task.FromResult(progress.For(task.TaskId).FirstOrDefault(e => e.CurrentStatus == TaskStatus.Running)),
            "任务在某个节点上开始运行");
        await Site.WaitForHeldRequestAsync(Poll.DefaultTimeout);
        var victim = workers.Single(w => w.NodeId == running.AssignedNodeId);
        var survivor = workers.Single(w => w.NodeId != running.AssignedNodeId);

        // 相当于在 Worker 控制台按 Ctrl+C
        await victim.StopAsync();
        await master.WaitForNodeStatusAsync(victim.NodeId, NodeStatus.Offline);
        Site.ReleaseRequests();

        var pages = await master.WaitForTaskCompletedAsync(task.TaskId);
        Assert.Equal(TestWebSite.DepthTwoPages.Length, pages.Count);
        Assert.All(pages, p => Assert.Equal(200, p.StatusCode));
        Assert.Equal(survivor.NodeId, (await master.GetTaskAsync(task.TaskId))!.AssignedNode);
    }

    /// <summary>D3（M-03，T4 已修复）：注册时没有活跃流就替换旧会话</summary>
    [Fact]
    public async Task Worker崩溃后以相同NodeId重启能重新注册上线()
    {
        var master = await StartMasterAsync();
        var worker = await StartWorkerAsync("it-node-a", master);
        await master.WaitForNodeOnlineAsync("it-node-a");

        await worker.CrashAsync();
        await master.WaitForNodeStatusAsync("it-node-a", NodeStatus.Offline);

        // 容器重启或手动重开 Worker：NodeId 不变（MASTER_NODE_ID 或机器名）
        await StartWorkerAsync("it-node-a", master);
        await master.WaitForNodeOnlineAsync("it-node-a");
    }

    /// <summary>D4（M-01，T4 已修复）：认证拦截器已加入 AddGrpc 的拦截器列表</summary>
    [Fact]
    public async Task 令牌错误的节点注册被拒绝()
    {
        var master = await StartMasterAsync();
        using var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{master.GrpcPort}");
        var client = new CrawlerCoordinator.CrawlerCoordinatorClient(channel);
        var headers = new Metadata { { "authorization", "Bearer wrong-token" } };

        var registration = new NodeRegistration
        {
            NodeId = "it-intruder",
            MachineName = "intruder",
            CpuCores = 1,
            TotalMemoryGb = 1,
            IpAddress = "127.0.0.1"
        };
        var error = await Assert.ThrowsAsync<RpcException>(
            async () => await client.RegisterNodeAsync(registration, headers));
        Assert.Equal(StatusCode.Unauthenticated, error.StatusCode);
        Assert.Null(await master.GetNodeAsync("it-intruder"));
    }
}
