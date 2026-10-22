using System.Diagnostics;
using CrawlerCore.Infrastructure;
using CrawlerMaster.WPF.ViewModels;
using CrawlerWorker.Hosting;
using CrawlerWorker.Services.Engine;

namespace CrawlerCore.Tests.Worker;

/// <summary>按主机限速（W-08）、爬虫 HTTP 处理器（W-15）、Master 新建任务的默认起始 URL（W-07 ③）</summary>
public class HostRateLimiterTests
{
    [Fact(DisplayName = "W-08：并发等待同一主机时，相邻许可的间隔不小于要求的间隔")]
    public async Task ConcurrentWaiters_AreSpaced()
    {
        var limiter = new HostRateLimiter(TimeProvider.System);
        var interval = TimeSpan.FromMilliseconds(200);
        var stamps = new System.Collections.Concurrent.ConcurrentBag<long>();

        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                await limiter.WaitAsync("site.test", interval, CancellationToken.None);
                stamps.Add(Stopwatch.GetTimestamp());
            }
        })));

        var ordered = stamps.OrderBy(s => s).ToList();
        Assert.Equal(6, ordered.Count);
        for (var i = 1; i < ordered.Count; i++)
        {
            var gap = Stopwatch.GetElapsedTime(ordered[i - 1], ordered[i]);
            Assert.True(gap >= interval - TimeSpan.FromMilliseconds(2), $"第 {i} 个间隔只有 {gap.TotalMilliseconds:0} 毫秒");
        }
    }

    [Fact(DisplayName = "W-08：间隔取前后两次请求要求的较大值；不同主机互不影响")]
    public async Task UsesLargerInterval_AndHostsAreIndependent()
    {
        var limiter = new HostRateLimiter(TimeProvider.System);
        await limiter.WaitAsync("a.test", TimeSpan.FromMilliseconds(300), CancellationToken.None);
        var start = Stopwatch.GetTimestamp();

        await limiter.WaitAsync("b.test", TimeSpan.FromMilliseconds(300), CancellationToken.None);
        Assert.True(Stopwatch.GetElapsedTime(start) < TimeSpan.FromMilliseconds(150), "另一个主机不应等待");

        // 上一次要求 300 毫秒，这一次只要求 10 毫秒：仍要等满 300 毫秒
        await limiter.WaitAsync("a.test", TimeSpan.FromMilliseconds(10), CancellationToken.None);
        Assert.True(Stopwatch.GetElapsedTime(start) >= TimeSpan.FromMilliseconds(250));
    }

    [Fact(DisplayName = "W-20：Defer（Retry-After）推迟该主机的下一次请求")]
    public async Task Defer_PostponesNextRequest()
    {
        var limiter = new HostRateLimiter(TimeProvider.System);
        await limiter.WaitAsync("site.test", TimeSpan.Zero, CancellationToken.None);
        limiter.Defer("site.test", TimeSpan.FromMilliseconds(400));
        var start = Stopwatch.GetTimestamp();

        await limiter.WaitAsync("site.test", TimeSpan.Zero, CancellationToken.None);

        Assert.True(Stopwatch.GetElapsedTime(start) >= TimeSpan.FromMilliseconds(380));
    }

    [Fact(DisplayName = "等待可以被取消")]
    public async Task Wait_IsCancellable()
    {
        var limiter = new HostRateLimiter(TimeProvider.System);
        await limiter.WaitAsync("site.test", TimeSpan.FromMinutes(5), CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => limiter.WaitAsync("site.test", TimeSpan.FromMinutes(5), cts.Token));
    }

    [Fact(DisplayName = "W-15：爬虫请求默认校验证书、不自动跟随重定向；只有显式打开配置项才忽略证书错误")]
    public void CrawlerHandler_ValidatesCertificatesByDefault()
    {
        var handler = Assert.IsType<SocketsHttpHandler>(WorkerServiceCollectionExtensions.CreateCrawlerHttpHandler(new CrawlerSettings()));
        Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
        Assert.False(handler.AllowAutoRedirect);

        var insecure = Assert.IsType<SocketsHttpHandler>(WorkerServiceCollectionExtensions.CreateCrawlerHttpHandler(new CrawlerSettings { IgnoreCertificateErrors = true }));
        Assert.NotNull(insecure.SslOptions.RemoteCertificateValidationCallback);
    }

    [Fact(DisplayName = "W-07 ③：Master 新建任务的默认起始 URL 是 robots.txt 允许的列表页")]
    public void CreateTaskDefaultStartUrl_IsAllowedListPage()
    {
        var viewModel = new CreateTaskViewModel(new Moq.Mock<IServiceProvider>().Object);
        Assert.Equal("https://arxiv.org/list/cs.LG/recent", viewModel.StartUrlsText);
        Assert.DoesNotContain("/search", viewModel.StartUrlsText);
    }
}
