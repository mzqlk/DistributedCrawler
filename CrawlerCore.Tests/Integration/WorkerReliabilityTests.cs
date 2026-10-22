using CrawlerCore.Entities;
using CrawlerCore.Tests.Integration.Infrastructure;
using Xunit.Abstractions;

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
}
