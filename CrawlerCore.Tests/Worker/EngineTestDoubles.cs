using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using CrawlerCore.Entities;
using CrawlerCore.Interfaces;
using CrawlerWorker.Services.Engine;
using CrawlerWorker.Services.Engine.Robots;

namespace CrawlerCore.Tests.Worker;

/// <summary>允许一切、没有 Crawl-delay 的 robots 策略（只测爬取逻辑时使用）</summary>
public sealed class AllowAllRobotsPolicy : IRobotsPolicy
{
    public static readonly AllowAllRobotsPolicy Instance = new();

    public Task<RobotsDecision> CheckAsync(Uri url, CancellationToken cancellationToken)
        => Task.FromResult(new RobotsDecision(RobotsVerdict.Allowed, TimeSpan.Zero));
}

/// <summary>
/// 按路由函数返回响应的 HTTP 处理器，记录每个请求的地址与到达时间（单调时钟）
/// </summary>
public sealed class RoutingHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _route;

    public RoutingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> route)
    {
        _route = route;
    }

    public RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> route)
        : this((request, _) => Task.FromResult(route(request)))
    {
    }

    /// <summary>按到达顺序记录的请求（绝对地址、到达时的 Stopwatch 时间戳）</summary>
    public ConcurrentQueue<(string Url, long Timestamp)> Requests { get; } = new();

    public IReadOnlyList<string> RequestedUrls => Requests.Select(r => r.Url).ToList();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Enqueue((request.RequestUri!.AbsoluteUri, Stopwatch.GetTimestamp()));
        return _route(request, cancellationToken);
    }

    public static HttpResponseMessage Html(string html, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = CrawlerEngineCompleteTests.Html(html) };

    public static HttpResponseMessage Status(HttpStatusCode status) => new(status) { Content = new StringContent(string.Empty) };

    public static HttpResponseMessage Redirect(string location, HttpStatusCode status = HttpStatusCode.MovedPermanently)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }
}

/// <summary>每次 CreateClient 都返回使用同一处理器的新 HttpClient（处理器不随 HttpClient 释放）</summary>
public sealed class SingleHandlerClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

/// <summary>可配置行为的测试插件：记录调用，按需失败、超时或抛异常</summary>
public sealed class ScriptedPlugin : ICrawlerPlugin
{
    public string Name { get; init; } = "ScriptedPlugin";
    public string Version => "1.0.0";
    public Func<string, bool> FollowLink { get; init; } = _ => true;
    public Func<PluginContext, Task<PluginResult>>? Execute { get; init; }
    public Dictionary<string, string> Configuration { get; init; } = new();
    public ConcurrentQueue<string> ParsedUrls { get; } = new();

    public Task<PluginResult> ExecuteAsync(PluginContext context)
    {
        ParsedUrls.Enqueue(context.Url);
        return Execute?.Invoke(context)
               ?? Task.FromResult(new PluginResult { Success = true, Data = new Dictionary<string, object> { ["Url"] = context.Url } });
    }

    public Dictionary<string, string> GetDefaultConfiguration() => Configuration;

    public bool ShouldFollowLink(string url, int currentDepth) => FollowLink(url);
}

/// <summary>只提供一个插件的插件管理器</summary>
public sealed class SinglePluginManager(ICrawlerPlugin plugin) : IPluginManager
{
    public ICrawlerPlugin? GetPlugin(string pluginName)
        => string.Equals(pluginName, plugin.Name, StringComparison.OrdinalIgnoreCase) ? plugin : null;

    public IReadOnlyList<ICrawlerPlugin> GetAllPlugins() => [plugin];

    public void LoadAllPlugins()
    {
    }
}

/// <summary>记录每次限速请求的主机与间隔，不实际等待（Defer 也只记录）</summary>
public sealed class RecordingRateLimiter : IHostRateLimiter
{
    public ConcurrentQueue<(string Host, TimeSpan Interval)> Waits { get; } = new();
    public ConcurrentQueue<(string Host, TimeSpan Delay)> Deferrals { get; } = new();

    public Task WaitAsync(string host, TimeSpan interval, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Waits.Enqueue((host, interval));
        return Task.CompletedTask;
    }

    public void Defer(string host, TimeSpan delay) => Deferrals.Enqueue((host, delay));
}

/// <summary>读取正文时一直挂起（直到取消）的响应内容，用于验证超时覆盖读取正文的全过程</summary>
public sealed class HangingContent : HttpContent
{
    public HangingContent()
    {
        Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html");
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        => await Task.Delay(Timeout.Infinite, cancellationToken);

    protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
        => Task.FromResult<Stream>(new HangingStream());

    protected override Task<Stream> CreateContentReadStreamAsync()
        => Task.FromResult<Stream>(new HangingStream());

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }

    private sealed class HangingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
