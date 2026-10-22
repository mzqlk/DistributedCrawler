using System.Diagnostics;
using System.Net;
using CrawlerCore.Entities;
using CrawlerCore.Infrastructure;
using CrawlerWorker.Services.Engine;
using CrawlerWorker.Services.Engine.Robots;
using Microsoft.Extensions.Options;

namespace CrawlerCore.Tests.Worker;

/// <summary>
/// 爬虫引擎的取消、合规（robots、限速）、重定向、重试与响应限制（T5：W-01、W-07、W-08、W-18、W-19、W-20、W-21）
/// </summary>
public class CrawlerEngineComplianceTests
{
    private const string Site = "https://site.test";

    private static CrawlerSettings FastSettings(Action<CrawlerSettings>? configure = null)
    {
        var settings = new CrawlerSettings { DelayBetweenRequestsMs = 0, RequestTimeoutSeconds = 5, MaxRequestRetries = 2 };
        configure?.Invoke(settings);
        return settings;
    }

    private static (CrawlerEngine Engine, RoutingHandler Handler) CreateEngine(
        RoutingHandler handler,
        ScriptedPlugin plugin,
        CrawlerSettings? settings = null,
        IRobotsPolicy? robots = null,
        IHostRateLimiter? limiter = null)
    {
        settings ??= FastSettings();
        var factory = new SingleHandlerClientFactory(handler);
        limiter ??= new RecordingRateLimiter();
        robots ??= new RobotsPolicy(factory, limiter, Options.Create(settings), TimeProvider.System);
        var engine = new CrawlerEngine(factory, new SinglePluginManager(plugin), Options.Create(settings), robots, limiter);
        return (engine, handler);
    }

    private static CrawlerTask NewTask(ScriptedPlugin plugin, int maxDepth = 2, int maxPages = 20, params string[] startUrls)
        => new() { TaskId = Guid.NewGuid().ToString(), PluginName = plugin.Name, MaxDepth = maxDepth, MaxPages = maxPages, StartUrls = startUrls.ToList() };

    #region W-01 取消
    [Fact(DisplayName = "W-01：请求进行中被取消时抛出 OperationCanceledException，不产生 499/500 页面")]
    public async Task CancelledWhileRequestInFlight_Throws()
    {
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new RoutingHandler(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath == "/robots.txt") return RoutingHandler.Status(HttpStatusCode.NotFound);
            requestStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return RoutingHandler.Html("<html></html>");
        });
        var plugin = new ScriptedPlugin();
        var (engine, _) = CreateEngine(handler, plugin);
        using var cts = new CancellationTokenSource();

        var execution = engine.ExecuteAsync(NewTask(plugin, startUrls: [$"{Site}/a", $"{Site}/b"]), cts.Token);
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        Assert.Empty(plugin.ParsedUrls);
    }
    #endregion

    #region W-07 robots
    [Fact(DisplayName = "W-07：robots.txt 禁止的起始 URL 不被请求，不计入结果")]
    public async Task DisallowedStartUrl_IsNotRequested()
    {
        var handler = new RoutingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/robots.txt" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("User-agent: *\nDisallow: /search\nAllow: /list\n") },
            _ => RoutingHandler.Html("<html><a href='/list/x'>x</a></html>")
        });
        var plugin = new ScriptedPlugin();
        var (engine, _) = CreateEngine(handler, plugin);

        var result = await engine.ExecuteAsync(NewTask(plugin, startUrls: [$"{Site}/search/?query=graphs"]), CancellationToken.None);

        Assert.Empty(result.PageResults);
        Assert.Equal(new[] { $"{Site}/robots.txt" }, handler.RequestedUrls);
    }

    [Fact(DisplayName = "W-07：被禁止的子链接不请求；robots.txt 只获取一次；Crawl-delay 参与请求间隔")]
    public async Task DisallowedLinks_SkippedAndCrawlDelayApplied()
    {
        var handler = new RoutingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/robots.txt" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("User-agent: *\nDisallow: /private\nCrawl-delay: 7\n") },
            "/" => RoutingHandler.Html("<html><a href='/public'>p</a><a href='/private/1'>x</a></html>"),
            _ => RoutingHandler.Html("<html></html>")
        });
        var plugin = new ScriptedPlugin();
        var limiter = new RecordingRateLimiter();
        var (engine, _) = CreateEngine(handler, plugin, FastSettings(s => s.DelayBetweenRequestsMs = 1000), limiter: limiter);

        var result = await engine.ExecuteAsync(NewTask(plugin, startUrls: [$"{Site}/"]), CancellationToken.None);

        Assert.Equal(new[] { $"{Site}/", $"{Site}/public" }, result.PageResults.Select(p => p.Url));
        Assert.Equal(new[] { $"{Site}/robots.txt", $"{Site}/", $"{Site}/public" }, handler.RequestedUrls);
        // robots.txt 本身按设置的间隔，页面按 Crawl-delay（7 秒，大于设置的 1 秒）
        Assert.Equal(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(7) }, limiter.Waits.Select(w => w.Interval));
    }

    [Fact(DisplayName = "W-07：robots.txt 返回 404 时视为没有限制")]
    public async Task RobotsNotFound_AllowsEverything()
    {
        var handler = new RoutingHandler(request => request.RequestUri!.AbsolutePath == "/robots.txt"
            ? RoutingHandler.Status(HttpStatusCode.NotFound)
            : RoutingHandler.Html("<html></html>"));
        var plugin = new ScriptedPlugin();
        var (engine, _) = CreateEngine(handler, plugin);

        var result = await engine.ExecuteAsync(NewTask(plugin, startUrls: [$"{Site}/search"]), CancellationToken.None);

        Assert.Equal(200, Assert.Single(result.PageResults).StatusCode);
    }

    [Fact(DisplayName = "W-07：robots.txt 返回 5xx 时暂不请求该主机，页面记为没有响应（状态码 0）")]
    public async Task RobotsServerError_PageNotRequested()
    {
        var handler = new RoutingHandler(request => request.RequestUri!.AbsolutePath == "/robots.txt"
            ? RoutingHandler.Status(HttpStatusCode.ServiceUnavailable)
            : RoutingHandler.Html("<html></html>"));
        var plugin = new ScriptedPlugin();
        var (engine, _) = CreateEngine(handler, plugin);

        var result = await engine.ExecuteAsync(NewTask(plugin, startUrls: [$"{Site}/a"]), CancellationToken.None);

        var page = Assert.Single(result.PageResults);
        Assert.Equal(CrawlerEngine.NoResponseStatusCode, page.StatusCode);
        Assert.Equal(new[] { $"{Site}/robots.txt" }, handler.RequestedUrls);
    }

    [Fact(DisplayName = "W-07：重定向到 robots.txt 禁止的地址时不请求它，本页不计入结果")]
    public async Task RedirectToDisallowed_NotRequested()
    {
        var handler = new RoutingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/robots.txt" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("User-agent: *\nDisallow: /search\n") },
            "/go" => RoutingHandler.Redirect("/search?q=1"),
            _ => RoutingHandler.Html("<html></html>")
        });
        var plugin = new ScriptedPlugin();
        var (engine, _) = CreateEngine(handler, plugin);

        var result = await engine.ExecuteAsync(NewTask(plugin, startUrls: [$"{Site}/go"]), CancellationToken.None);

        Assert.Empty(result.PageResults);
        Assert.DoesNotContain(handler.RequestedUrls, u => u.Contains("/search"));
    }
    #endregion

    #region W-08 按主机限速
    [Fact(DisplayName = "W-08：两个任务并发请求同一主机，相邻请求间隔不小于配置的间隔；不同主机互不影响")]
    public async Task ConcurrentTasks_ShareHostRateLimit()
    {
        var handler = new RoutingHandler(request => request.RequestUri!.AbsolutePath == "/robots.txt"
            ? RoutingHandler.Status(HttpStatusCode.NotFound)
            : RoutingHandler.Html("<html></html>"));
        var plugin = new ScriptedPlugin();
        var settings = FastSettings(s => s.DelayBetweenRequestsMs = 200);
        var (engine, _) = CreateEngine(handler, plugin, settings, limiter: new HostRateLimiter(TimeProvider.System));

        var stopwatch = Stopwatch.StartNew();
        await Task.WhenAll(
            engine.ExecuteAsync(NewTask(plugin, startUrls: [$"{Site}/a1", $"{Site}/a2"]), CancellationToken.None),
            engine.ExecuteAsync(NewTask(plugin, startUrls: [$"{Site}/b1", $"{Site}/b2"]), CancellationToken.None),
            engine.ExecuteAsync(NewTask(plugin, startUrls: ["https://other.test/c1"]), CancellationToken.None));

        var sameHost = handler.Requests.Where(r => r.Url.StartsWith(Site)).Select(r => r.Timestamp).OrderBy(t => t).ToList();
        Assert.Equal(5, sameHost.Count); // robots.txt + 4 个页面
        for (var i = 1; i < sameHost.Count; i++)
        {
            var gap = Stopwatch.GetElapsedTime(sameHost[i - 1], sameHost[i]);
            Assert.True(gap >= TimeSpan.FromMilliseconds(190), $"第 {i} 个间隔只有 {gap.TotalMilliseconds:0} 毫秒");
        }

        // 另一个主机的请求不必排在 site.test 之后
        var other = handler.Requests.Where(r => r.Url.StartsWith("https://other.test")).Select(r => r.Timestamp).Min();
        Assert.True(Stopwatch.GetElapsedTime(sameHost[0], other) < TimeSpan.FromMilliseconds(600), "不同主机不应互相等待");
    }
    #endregion

    #region W-19 重定向
    [Fact(DisplayName = "W-19：/a 和 /b 都重定向到 /c 时，/c 只爬一次")]
    public async Task RedirectsToSameTarget_CrawledOnce()
    {
        var handler = new RoutingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/robots.txt" => RoutingHandler.Status(HttpStatusCode.NotFound),
            "/a" or "/b" => RoutingHandler.Redirect("/c"),
            _ => RoutingHandler.Html("<html></html>")
        });
        var plugin = new ScriptedPlugin();
        var (engine, _) = CreateEngine(handler, plugin);

        var result = await engine.ExecuteAsync(NewTask(plugin, startUrls: [$"{Site}/a", $"{Site}/b"]), CancellationToken.None);

        var page = Assert.Single(result.PageResults);
        Assert.Equal($"{Site}/a", page.Url);
        Assert.Equal($"{Site}/c", page.FinalUrl);
        Assert.Single(handler.RequestedUrls, u => u == $"{Site}/c");
        Assert.Single(plugin.ParsedUrls);
    }

    [Fact(DisplayName = "W-19：子链接重定向到插件拒绝的地址时不请求它")]
    public async Task LinkRedirectToRejectedUrl_NotRequested()
    {
        var handler = new RoutingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/robots.txt" => RoutingHandler.Status(HttpStatusCode.NotFound),
            "/" => RoutingHandler.Html("<html><a href='/item/1'>1</a></html>"),
            "/item/1" => RoutingHandler.Redirect("https://elsewhere.test/item/1"),
            _ => RoutingHandler.Html("<html></html>")
        });
        var plugin = new ScriptedPlugin { FollowLink = url => url.Contains("/item/") && url.StartsWith(Site) };
        var (engine, _) = CreateEngine(handler, plugin, FastSettings(s => s.SameHostOnly = false));

        var result = await engine.ExecuteAsync(NewTask(plugin, startUrls: [$"{Site}/"]), CancellationToken.None);

        Assert.Equal(new[] { $"{Site}/" }, result.PageResults.Select(p => p.Url));
        Assert.DoesNotContain(handler.RequestedUrls, u => u.Contains("elsewhere.test"));
    }

    [Fact(DisplayName = "W-19：失败页面也记录最终请求的地址")]
    public async Task FailedPage_HasFinalUrl()
    {
        var handler = new RoutingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/robots.txt" => RoutingHandler.Status(HttpStatusCode.NotFound),
            "/old" => RoutingHandler.Redirect("/gone"),
            _ => RoutingHandler.Status(HttpStatusCode.NotFound)
        });
        var plugin = new ScriptedPlugin();
        var (engine, _) = CreateEngine(handler, plugin);

        var page = Assert.Single((await engine.ExecuteAsync(NewTask(plugin, startUrls: [$"{Site}/old"]), CancellationToken.None)).PageResults);

        Assert.Equal(404, page.StatusCode);
        Assert.Equal($"{Site}/gone", page.FinalUrl);
    }
    #endregion

    #region W-20 重试
    [Fact(DisplayName = "W-20：503 带 Retry-After 时推迟该主机的请求后重试，最终成功")]
    public async Task ServiceUnavailableWithRetryAfter_Retried()
    {
        var attempts = 0;
        var handler = new RoutingHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/robots.txt") return RoutingHandler.Status(HttpStatusCode.NotFound);
            if (Interlocked.Increment(ref attempts) == 1)
            {
                var busy = RoutingHandler.Status(HttpStatusCode.ServiceUnavailable);
                busy.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
                return busy;
            }
            return RoutingHandler.Html("<html></html>");
        });
        var plugin = new ScriptedPlugin();
        var limiter = new HostRateLimiter(TimeProvider.System);
        var (engine, _) = CreateEngine(handler, plugin, limiter: limiter);

        var stopwatch = Stopwatch.StartNew();
        var page = Assert.Single((await engine.ExecuteAsync(NewTask(plugin, startUrls: [$"{Site}/a"]), CancellationToken.None)).PageResults);

        Assert.Equal(200, page.StatusCode);
        Assert.Equal(2, attempts);
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(950), $"应按 Retry-After 等待约 1 秒，实际 {stopwatch.Elapsed.TotalMilliseconds:0} 毫秒");
    }

    [Fact(DisplayName = "W-20：一直 429 时重试次数用完后记 429，并按 Retry-After 推迟该主机")]
    public async Task TooManyRequests_RetriesExhausted()
    {
        var attempts = 0;
        var handler = new RoutingHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/robots.txt") return RoutingHandler.Status(HttpStatusCode.NotFound);
            Interlocked.Increment(ref attempts);
            var limited = RoutingHandler.Status(HttpStatusCode.TooManyRequests);
            limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return limited;
        });
        var plugin = new ScriptedPlugin();
        var limiter = new RecordingRateLimiter();
        var (engine, _) = CreateEngine(handler, plugin, limiter: limiter);

        var page = Assert.Single((await engine.ExecuteAsync(NewTask(plugin, startUrls: [$"{Site}/a"]), CancellationToken.None)).PageResults);

        Assert.Equal(429, page.StatusCode);
        Assert.Equal(3, attempts); // 首次 + 重试 2 次
        Assert.All(limiter.Deferrals, d => Assert.Equal(TimeSpan.FromSeconds(30), d.Delay));
        Assert.Equal(3, limiter.Deferrals.Count);
    }

    [Fact(DisplayName = "W-20：网络错误重试后仍失败时状态码记 0（没有 HTTP 响应），不再记 500")]
    public async Task NetworkError_RecordedAsNoResponse()
    {
        var attempts = 0;
        var handler = new RoutingHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/robots.txt") return RoutingHandler.Status(HttpStatusCode.NotFound);
            Interlocked.Increment(ref attempts);
            throw new HttpRequestException("连接被重置");
        });
        var plugin = new ScriptedPlugin();
        var (engine, _) = CreateEngine(handler, plugin);

        var page = Assert.Single((await engine.ExecuteAsync(NewTask(plugin, startUrls: [$"{Site}/a"]), CancellationToken.None)).PageResults);

        Assert.Equal(CrawlerEngine.NoResponseStatusCode, page.StatusCode);
        Assert.Equal(3, attempts);
    }
    #endregion

    #region W-21 响应限制与作用域
    [Fact(DisplayName = "W-21：不是 HTML 的响应不读取正文、不调用插件")]
    public async Task NonHtml_NotParsed()
    {
        var handler = new RoutingHandler(request => request.RequestUri!.AbsolutePath == "/robots.txt"
            ? RoutingHandler.Status(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 0x25, 0x50, 0x44, 0x46 }) { Headers = { ContentType = new("application/pdf") } } });
        var plugin = new ScriptedPlugin();
        var (engine, _) = CreateEngine(handler, plugin);

        var page = Assert.Single((await engine.ExecuteAsync(NewTask(plugin, startUrls: [$"{Site}/paper.pdf"]), CancellationToken.None)).PageResults);

        Assert.Equal(200, page.StatusCode);
        Assert.Empty(page.ParsedData);
        Assert.Empty(plugin.ParsedUrls);
    }

    [Fact(DisplayName = "W-21：正文超过上限时不解析")]
    public async Task OversizedBody_NotParsed()
    {
        var handler = new RoutingHandler(request => request.RequestUri!.AbsolutePath == "/robots.txt"
            ? RoutingHandler.Status(HttpStatusCode.NotFound)
            : RoutingHandler.Html("<html>" + new string('x', 5000) + "</html>"));
        var plugin = new ScriptedPlugin();
        var (engine, _) = CreateEngine(handler, plugin, FastSettings(s => s.MaxResponseBytes = 1000));

        var page = Assert.Single((await engine.ExecuteAsync(NewTask(plugin, startUrls: [$"{Site}/big"]), CancellationToken.None)).PageResults);

        Assert.Empty(page.ParsedData);
        Assert.Empty(plugin.ParsedUrls);
    }

    [Fact(DisplayName = "W-21：读取正文超时时该页记 408，任务继续")]
    public async Task BodyReadTimeout_RecordedAs408AndTaskContinues()
    {
        var handler = new RoutingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/robots.txt" => RoutingHandler.Status(HttpStatusCode.NotFound),
            "/slow" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new HangingContent() },
            _ => RoutingHandler.Html("<html></html>")
        });
        var plugin = new ScriptedPlugin();
        var (engine, _) = CreateEngine(handler, plugin, FastSettings(s => { s.RequestTimeoutSeconds = 1; s.MaxRequestRetries = 0; }));

        var result = await engine.ExecuteAsync(NewTask(plugin, startUrls: [$"{Site}/slow", $"{Site}/fast"]), CancellationToken.None);

        Assert.Equal(new[] { 408, 200 }, result.PageResults.Select(p => p.StatusCode));
    }

    [Fact(DisplayName = "W-21：默认只跟随与起始 URL 同主机的链接")]
    public async Task SameHostOnly_ByDefault()
    {
        var handler = new RoutingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/robots.txt" => RoutingHandler.Status(HttpStatusCode.NotFound),
            "/" => RoutingHandler.Html($"<html><a href='{Site}/in'>in</a><a href='https://external.test/out'>out</a></html>"),
            _ => RoutingHandler.Html("<html></html>")
        });
        var plugin = new ScriptedPlugin();
        var (engine, _) = CreateEngine(handler, plugin);

        var result = await engine.ExecuteAsync(NewTask(plugin, startUrls: [$"{Site}/"]), CancellationToken.None);

        Assert.Equal(new[] { $"{Site}/", $"{Site}/in" }, result.PageResults.Select(p => p.Url));
        Assert.DoesNotContain(handler.RequestedUrls, u => u.Contains("external.test"));
    }
    #endregion

    #region W-18 插件超时与异常
    [Fact(DisplayName = "W-18/W-30：插件超时或抛异常时该页没有解析数据、保留 HTTP 状态码，任务继续")]
    public async Task PluginTimeoutOrException_PageKeptAndTaskContinues()
    {
        var handler = new RoutingHandler(request => request.RequestUri!.AbsolutePath == "/robots.txt"
            ? RoutingHandler.Status(HttpStatusCode.NotFound)
            : RoutingHandler.Html("<html></html>"));
        var plugin = new ScriptedPlugin
        {
            Execute = async context =>
            {
                if (context.Url.EndsWith("/hang")) await Task.Delay(Timeout.Infinite);
                if (context.Url.EndsWith("/boom")) throw new InvalidOperationException("插件内部错误");
                return new PluginResult { Success = true, Data = new Dictionary<string, object> { ["ok"] = "1" } };
            }
        };
        var (engine, _) = CreateEngine(handler, plugin, FastSettings(s => s.PluginTimeoutSeconds = 1));

        var result = await engine.ExecuteAsync(NewTask(plugin, startUrls: [$"{Site}/hang", $"{Site}/boom", $"{Site}/fine"]), CancellationToken.None);

        Assert.Equal(new[] { 200, 200, 200 }, result.PageResults.Select(p => p.StatusCode));
        Assert.Empty(result.PageResults[0].ParsedData);
        Assert.Empty(result.PageResults[1].ParsedData);
        Assert.Equal("1", result.PageResults[2].ParsedData["ok"]);
    }
    #endregion

    #region 进度
    [Fact(DisplayName = "W-26 ②：进度的总页数是已爬加队列中（不超过 MaxPages），而不是恒为 MaxPages")]
    public async Task ProgressTotal_ReflectsQueue()
    {
        var handler = new RoutingHandler(request => request.RequestUri!.AbsolutePath == "/robots.txt"
            ? RoutingHandler.Status(HttpStatusCode.NotFound)
            : RoutingHandler.Html("<html></html>"));
        var plugin = new ScriptedPlugin();
        var (engine, _) = CreateEngine(handler, plugin);
        var totals = new List<int>();
        engine.ProgressUpdated += (_, e) => totals.Add(e.TotalPages);

        await engine.ExecuteAsync(NewTask(plugin, maxPages: 50, startUrls: [$"{Site}/1", $"{Site}/2"]), CancellationToken.None);

        Assert.Equal(new[] { 2, 2, 2 }, totals);
    }
    #endregion
}
