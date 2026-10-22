using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CrawlerCore.Tests.Integration.Infrastructure;

/// <summary>
/// 本地 Kestrel 静态测试网站：以 <c>/site/</c> 提供 Integration/TestSite 下的 HTML 页面，
/// 记录每个请求的路径，并可以“扣住”请求（<see cref="HoldRequests"/>），让任务停在运行中，便于注入故障。
/// </summary>
public sealed class TestWebSite : IAsyncDisposable
{
    /// <summary>首页 + 深度 2 的三个页面（MaxDepth=2 时应爬取的全部页面）</summary>
    public static readonly string[] DepthTwoPages = ["index.html", "a.html", "b.html", "c.html"];

    private WebApplication _app = null!;
    private TaskCompletionSource _gate = CreateCompletedGate();
    private TaskCompletionSource _heldRequestArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gateLock = new();

    private TestWebSite()
    {
    }

    /// <summary>站点根地址，例如 http://127.0.0.1:12345</summary>
    public string BaseUrl { get; private set; } = string.Empty;

    /// <summary>按到达顺序记录的请求路径</summary>
    public ConcurrentQueue<string> RequestedPaths { get; } = new();

    /// <summary>
    /// /robots.txt 的内容；为 null（默认）时返回 404，Worker 视为没有限制
    /// </summary>
    public string? RobotsTxt { get; set; }

    /// <summary>测试页面的绝对地址</summary>
    public string PageUrl(string page) => $"{BaseUrl}/site/{page}";

    public static async Task<TestWebSite> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));

        var site = new TestWebSite();
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            site.RequestedPaths.Enqueue(context.Request.Path.Value ?? string.Empty);
            await site.PassGateAsync(context.RequestAborted);
            if (context.Request.Path == "/robots.txt" && site.RobotsTxt is { } robots)
            {
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync(robots);
                return;
            }
            await next(context);
        });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(Path.Combine(AppContext.BaseDirectory, "Integration", "TestSite")),
            RequestPath = "/site"
        });

        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
        site._app = app;
        site.BaseUrl = address.TrimEnd('/');
        return site;
    }

    /// <summary>请求经过闸门：未扣住时直接通过；扣住时通知“有请求被扣住”，并等待放行或请求中止</summary>
    private Task PassGateAsync(CancellationToken requestAborted)
    {
        Task gate;
        lock (_gateLock)
        {
            gate = _gate.Task;
            if (!gate.IsCompleted)
            {
                _heldRequestArrived.TrySetResult();
            }
        }
        return gate.WaitAsync(requestAborted);
    }

    /// <summary>此后到达的请求都挂起，直到 <see cref="ReleaseRequests"/></summary>
    public void HoldRequests()
    {
        lock (_gateLock)
        {
            _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _heldRequestArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>等待第一个被挂起的请求到达</summary>
    public Task WaitForHeldRequestAsync(TimeSpan timeout)
    {
        Task arrived;
        lock (_gateLock)
        {
            arrived = _heldRequestArrived.Task;
        }
        return arrived.WaitAsync(timeout);
    }

    /// <summary>放行所有被挂起的请求，恢复正常响应</summary>
    public void ReleaseRequests()
    {
        lock (_gateLock)
        {
            _gate.TrySetResult();
        }
    }

    public async ValueTask DisposeAsync()
    {
        ReleaseRequests();
        await _app.StopAsync(TimeSpan.FromSeconds(5));
        await _app.DisposeAsync();
    }

    private static TaskCompletionSource CreateCompletedGate()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.SetResult();
        return gate;
    }
}
