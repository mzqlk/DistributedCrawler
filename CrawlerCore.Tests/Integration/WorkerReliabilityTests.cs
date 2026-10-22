using CrawlerCore.Entities;
using CrawlerCore.Tests.Integration.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit.Abstractions;
using TaskStatus = CrawlerCore.Entities.TaskStatus;

namespace CrawlerCore.Tests.Integration;

/// <summary>
/// Worker 侧回归测试（路线图 T5，对应 docs/review/worker.md 的 W 编号）：合规、连接管理、结果可靠上报、停止流程。
/// D1、D2 在 <see cref="MasterWorkerKnownDefectTests"/>。
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait(IntegrationTestCollection.Category, IntegrationTestCollection.Integration)]
public sealed class WorkerReliabilityTests : IntegrationTestBase
{
    public WorkerReliabilityTests(ITestOutputHelper output) : base(output)
    {
    }

    #region 合规（W-07）
    [Fact]
    public async Task robots禁止的页面不被请求_也不计入结果()
    {
        Site.RobotsTxt = "User-agent: *\nDisallow: /site/b.html\n";
        var master = await StartMasterAsync();
        await StartWorkerAsync("it-node-a", master);
        await master.WaitForNodeOnlineAsync("it-node-a");

        var task = await master.ScheduleTaskAsync(NewSiteTask("robots-禁止页面"));
        var pages = await master.WaitForTaskCompletedAsync(task.TaskId);

        Assert.Equal(new[] { "a.html", "c.html", "index.html" }.Select(Site.PageUrl), pages.Select(p => p.Url).OrderBy(u => u));
        Assert.DoesNotContain("/site/b.html", Site.RequestedPaths);
        Assert.Equal("/robots.txt", Site.RequestedPaths.First());
        Assert.Single(Site.RequestedPaths, p => p == "/robots.txt");
    }
    #endregion

    #region 结果可靠上报与半开连接（W-03、W-12）
    [Fact]
    public async Task 失联期间完成的任务_Worker发现半开连接并在重连后重发结果_Master只有一份结果()
    {
        var master = await StartMasterAsync();
        var worker = await StartWorkerAsync("it-node-a", master);
        await master.WaitForNodeOnlineAsync("it-node-a");

        Site.HoldRequests();
        var task = await master.ScheduleTaskAsync(NewSiteTask("失联期间完成"));
        await master.WaitForTaskStatusAsync(task.TaskId, TaskStatus.Running);
        await Site.WaitForHeldRequestAsync(Poll.DefaultTimeout);

        // 失联：连接还在但不再有数据。Master 由健康检查判离线并重置任务；Worker 由 keepalive 发现半开连接
        var connectionsBefore = worker.Proxy.TotalConnectionCount;
        worker.Hang();
        await master.WaitForNodeStatusAsync("it-node-a", NodeStatus.Offline);

        // 任务在失联期间完成：结果只能写进已经失联的连接，或留在发件箱里
        Site.ReleaseRequests();
        await Poll.UntilAsync(() => TestWebSite.DepthTwoPages.All(p => Site.RequestedPaths.Contains("/site/" + p)), "失联期间任务的页面都已爬取");
        await Poll.UntilAsync(() => worker.Proxy.TotalConnectionCount > connectionsBefore, "W-12：Worker 发现半开连接并尝试重连");

        worker.Proxy.Restore();
        var pages = await master.WaitForTaskCompletedAsync(task.TaskId, TimeSpan.FromSeconds(40));

        Assert.Equal(TestWebSite.DepthTwoPages.Length, pages.Count);
        Assert.Equal(1, await master.CountCrawlResultsAsync(task.TaskId));
        Assert.Equal("it-node-a", (await master.GetTaskAsync(task.TaskId))!.AssignedNode);
        // 结果来自重发，而不是重新执行：每个页面只被请求过一次
        foreach (var page in TestWebSite.DepthTwoPages)
        {
            Assert.Single(Site.RequestedPaths, p => p == "/site/" + page);
        }
    }
    #endregion

    #region 启动与配置错误（W-11、W-22）
    [Fact]
    public async Task Master不可达时启动的Worker持续重试_Master可达后注册上线并上报并发上限()
    {
        var master = await StartMasterAsync();
        var worker = await StartWorkerAsync("it-node-late", master, maxConcurrentTasks: 3, startDisconnected: true);

        // 原来启动时 TCP 预检失败就退出（退出码 0）；现在按退避一直重试
        await Poll.UntilAsync(() => worker.Proxy.TotalConnectionCount >= 3, "Worker 多次尝试连接 Master");
        var lifetime = worker.Services.GetRequiredService<IHostApplicationLifetime>();
        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested, "Master 暂时不可达时 Worker 不应退出");

        worker.Proxy.Restore();
        await master.WaitForNodeOnlineAsync("it-node-late");
        Assert.Equal(3, master.Coordinator.GetMaxConcurrentTasks("it-node-late"));
    }

    [Fact]
    public async Task 令牌错误的Worker不再重试_以非0退出码退出()
    {
        var master = await StartMasterAsync();
        try
        {
            var worker = await StartWorkerAsync("it-bad-token", master, authToken: "wrong-token");
            var lifetime = worker.Services.GetRequiredService<IHostApplicationLifetime>();

            await Poll.UntilAsync(() => lifetime.ApplicationStopping.IsCancellationRequested, "令牌被拒绝后 Worker 请求退出");
            Assert.Equal(1, Environment.ExitCode);
            Assert.Null(await master.GetNodeAsync("it-bad-token"));
        }
        finally
        {
            // Worker 把进程退出码设为 1；测试进程不能带着它退出
            Environment.ExitCode = 0;
        }
    }
    #endregion
}
