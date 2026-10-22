using AngleSharp;
using CrawlerCore.Entities;
using CrawlerCore.Events;
using CrawlerCore.Infrastructure;
using CrawlerCore.Interfaces;
using CrawlerWorker.Services.Engine.Robots;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http;
using System.Text;
using TaskStatus = CrawlerCore.Entities.TaskStatus;

namespace CrawlerWorker.Services.Engine;

/// <summary>
/// Worker爬虫执行引擎：BFS 深度爬取。
/// <list type="bullet">
/// <item>取消：请求进行中被取消时直接抛出 <see cref="OperationCanceledException"/>，不把被取消的请求记成失败页面（W-01）。</item>
/// <item>合规：起始 URL、子链接与每一跳重定向目标在请求前都经过 robots.txt 检查（<see cref="IRobotsPolicy"/>，W-07）；
/// 每个请求都经过按主机限速（<see cref="IHostRateLimiter"/>），间隔取设置、插件 CrawlDelay、robots Crawl-delay 的最大值（W-08）。</item>
/// <item>重定向：手动跟随，每一跳先做循环检测、作用域检查（插件 ShouldFollowLink、同主机），再全局去重（W-19）。</item>
/// <item>失败重试：超时、429、5xx、网络错误按退避重试，429/503 的 Retry-After 推迟该主机的所有请求；
/// 没有 HTTP 响应的页面状态码记 0（W-20）。</item>
/// <item>响应：超时覆盖读取正文；只解析 HTML，正文超过上限不解析；默认只跟随与起始 URL 同主机的链接（W-21）。</item>
/// <item>插件解析有超时，插件出错时保留 HTTP 状态码（W-18、W-30）。</item>
/// </list>
/// 被 robots 禁止、重定向到已爬取或作用域之外的页面不计入 MaxPages，也不出现在结果中。
/// </summary>
public class CrawlerEngine : ICrawlerEngine
{
    /// <summary>引擎与 robots 策略使用的命名 HttpClient</summary>
    public const string HttpClientName = "CrawlerEngineClient";

    #region 常量与依赖注入
    private const int MaxUrlHashSetCapacity = 100000;
    private const int MaxRedirects = 10;
    /// <summary>重定向次数超限时记录的状态码（沿用原有约定）</summary>
    private const int TooManyRedirectsStatusCode = 310;
    /// <summary>没有收到 HTTP 响应（网络错误、robots.txt 暂时不可用）时记录的状态码</summary>
    public const int NoResponseStatusCode = 0;
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(5);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IPluginManager _pluginManager;
    private readonly CrawlerSettings _crawlerSettings;
    private readonly IRobotsPolicy _robotsPolicy;
    private readonly IHostRateLimiter _rateLimiter;
    #endregion

    #region 事件定义
    public event EventHandler<TaskProgressUpdatedEventArgs>? ProgressUpdated;
    #endregion

    #region 构造函数
    public CrawlerEngine(IHttpClientFactory httpClientFactory,
                         IPluginManager pluginManager,
                         IOptions<CrawlerSettings> crawlerSettings,
                         IRobotsPolicy robotsPolicy,
                         IHostRateLimiter rateLimiter)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _pluginManager = pluginManager ?? throw new ArgumentNullException(nameof(pluginManager));
        _crawlerSettings = crawlerSettings.Value ?? throw new ArgumentNullException(nameof(crawlerSettings));
        _robotsPolicy = robotsPolicy ?? throw new ArgumentNullException(nameof(robotsPolicy));
        _rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));
    }
    #endregion

    #region 核心执行入口
    public async Task<CrawlResult> ExecuteAsync(CrawlerTask task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentException.ThrowIfNullOrWhiteSpace(task.TaskId);

        var result = new CrawlResult { TaskId = task.TaskId };
        LogHelper.Info($"开始执行任务: {task.TaskId} | 插件: {task.PluginName} | 最大深度: {task.MaxDepth}", nameof(CrawlerEngine));

        try
        {
            var startUrls = new List<(string Url, Uri Uri)>();
            foreach (var url in task.StartUrls ?? [])
            {
                if (string.IsNullOrWhiteSpace(url)) continue;
                var trimmed = url.Trim();
                if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    startUrls.Add((trimmed, uri));
                }
                else
                {
                    LogHelper.Warn($"忽略无效的起始URL: {url}（任务 {task.TaskId}）", nameof(CrawlerEngine));
                }
            }

            if (startUrls.Count == 0)
            {
                LogHelper.Warn($"任务无有效URL: {task.TaskId}", nameof(CrawlerEngine));
                task.Status = TaskStatus.Completed;
                OnProgressUpdated(task, 0, 0);
                return result;
            }

            var plugin = _pluginManager.GetPlugin(task.PluginName)
                ?? throw new KeyNotFoundException($"未找到插件: {task.PluginName}");

            var context = new CrawlContext(task, plugin, ResolveBaseInterval(plugin, task), startUrls.Select(s => s.Uri));
            var maxPages = task.MaxPages <= 0 ? int.MaxValue : task.MaxPages;
            var crawledCount = 0;

            foreach (var (url, _) in startUrls)
            {
                if (context.UrlSet.Add(NormalizeUrl(url)))
                    context.Queue.Enqueue((url, 1));
            }

            OnProgressUpdated(task, 0, Math.Min(context.Queue.Count, maxPages));

            while (context.Queue.Count > 0 && crawledCount < maxPages)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var (currentUrl, currentDepth) = context.Queue.Dequeue();
                var pageResult = await CrawlPageAsync(context, currentUrl, currentDepth, cancellationToken).ConfigureAwait(false);
                if (pageResult == null)
                {
                    continue;
                }

                result.PageResults.Add(pageResult);
                crawledCount++;

                if (currentDepth < task.MaxDepth && pageResult.Links.Count > 0)
                {
                    EnqueueLinks(context, pageResult.Links, currentDepth);
                }

                OnProgressUpdated(task, crawledCount, Math.Min(crawledCount + context.Queue.Count, maxPages));
            }

            result.CompletedAt = DateTime.UtcNow;
            task.Status = TaskStatus.Completed;
            LogHelper.Info($"任务完成: {task.TaskId} | 总爬取页面: {crawledCount} | 去重URL数: {context.UrlSet.Count}", nameof(CrawlerEngine));
        }
        catch (OperationCanceledException)
        {
            task.Status = TaskStatus.Failed;
            LogHelper.Info($"任务被取消: {task.TaskId}", nameof(CrawlerEngine));
            throw;
        }
        catch (Exception ex)
        {
            task.Status = TaskStatus.Failed;
            LogHelper.Error(ex, $"任务执行失败: {task.TaskId}", nameof(CrawlerEngine));
            throw;
        }

        return result;
    }

    /// <summary>基础请求间隔：设置与插件 CrawlDelay 中较大的一个（任务配置中的 CrawlDelay 只能调大）</summary>
    private TimeSpan ResolveBaseInterval(ICrawlerPlugin plugin, CrawlerTask task)
    {
        var delayMs = Math.Max(0, _crawlerSettings.DelayBetweenRequestsMs);
        var pluginConfig = plugin.GetDefaultConfiguration() ?? new Dictionary<string, string>();
        if (pluginConfig.TryGetValue("CrawlDelay", out var pluginDelay) && int.TryParse(pluginDelay, out var pluginDelayMs))
        {
            delayMs = Math.Max(delayMs, pluginDelayMs);
        }
        if (task.Configuration != null && task.Configuration.TryGetValue("CrawlDelay", out var taskDelay) && int.TryParse(taskDelay, out var taskDelayMs))
        {
            delayMs = Math.Max(delayMs, taskDelayMs);
        }
        return TimeSpan.FromMilliseconds(delayMs);
    }

    private void EnqueueLinks(CrawlContext context, IEnumerable<string> links, int currentDepth)
    {
        var accepted = 0;
        foreach (var link in links)
        {
            if (!Uri.TryCreate(link, UriKind.Absolute, out var linkUri) || !IsInScope(context, linkUri, currentDepth))
            {
                continue;
            }

            if (context.UrlSet.Count >= MaxUrlHashSetCapacity)
            {
                if (!context.CapacityWarned)
                {
                    context.CapacityWarned = true;
                    LogHelper.Warn($"URL去重集合达到容量上限 {MaxUrlHashSetCapacity}，不再加入新的URL，已入队的继续爬取（任务 {context.Task.TaskId}）", nameof(CrawlerEngine));
                }
                return;
            }

            if (context.UrlSet.Add(NormalizeUrl(link)))
            {
                context.Queue.Enqueue((link, currentDepth + 1));
                accepted++;
            }
        }
        LogHelper.Debug($"深度 {currentDepth} 页面的链接入队 {accepted} 个，队列长度 {context.Queue.Count}", nameof(CrawlerEngine));
    }

    /// <summary>链接是否在任务的作用域内：同主机限制（W-21）与插件的 ShouldFollowLink</summary>
    private bool IsInScope(CrawlContext context, Uri url, int foundOnDepth)
    {
        if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps) return false;
        if (_crawlerSettings.SameHostOnly && !context.StartHosts.Contains(url.Host)) return false;
        return context.Plugin.ShouldFollowLink(url.AbsoluteUri, foundOnDepth);
    }
    #endregion

    #region 单页爬取
    /// <summary>
    /// 爬取一个页面（含重定向）。返回 null 表示这个页面不计入结果：被 robots 禁止，或重定向到已爬取、作用域之外的地址
    /// </summary>
    private async Task<PageResult?> CrawlPageAsync(CrawlContext context, string url, int depth, CancellationToken ct)
    {
        var pageResult = new PageResult { Url = url };
        var currentUri = new Uri(url);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { NormalizeUrl(url) };

        for (var redirectCount = 0; ; redirectCount++)
        {
            var robots = await _robotsPolicy.CheckAsync(currentUri, ct).ConfigureAwait(false);
            if (robots.Verdict == RobotsVerdict.Disallowed)
            {
                LogHelper.Warn($"robots.txt 禁止访问，跳过: {currentUri}{(redirectCount > 0 ? $"（由 {url} 重定向而来）" : string.Empty)}", nameof(CrawlerEngine));
                return null;
            }
            if (robots.Verdict == RobotsVerdict.Unavailable)
            {
                LogHelper.Warn($"robots.txt 暂时无法获取，不请求: {currentUri}", nameof(CrawlerEngine));
                pageResult.StatusCode = NoResponseStatusCode;
                pageResult.FinalUrl = redirectCount == 0 ? url : currentUri.AbsoluteUri;
                return pageResult;
            }

            var interval = robots.CrawlDelay > context.BaseInterval ? robots.CrawlDelay : context.BaseInterval;
            var fetch = await FetchAsync(currentUri, interval, ct).ConfigureAwait(false);
            pageResult.StatusCode = fetch.StatusCode;
            // 失败页面也记录最后请求的地址（W-19 ③）
            pageResult.FinalUrl = redirectCount == 0 ? url : currentUri.AbsoluteUri;

            if (fetch.IsRedirect)
            {
                var target = fetch.RedirectTarget;
                if (target == null)
                {
                    LogHelper.Warn($"重定向响应缺少有效的 Location 头 | URL: {currentUri}", nameof(CrawlerEngine));
                    return pageResult;
                }
                if (redirectCount >= MaxRedirects)
                {
                    LogHelper.Warn($"超出最大重定向次数 {MaxRedirects} | 最终 URL: {currentUri}", nameof(CrawlerEngine));
                    pageResult.StatusCode = TooManyRedirectsStatusCode;
                    return pageResult;
                }

                var normalizedTarget = NormalizeUrl(target.AbsoluteUri);
                if (!visited.Add(normalizedTarget))
                {
                    LogHelper.Warn($"检测到重定向循环，停止跟随 | URL: {target}", nameof(CrawlerEngine));
                    return pageResult;
                }
                if (!IsRedirectInScope(context, currentUri, target, depth))
                {
                    LogHelper.Debug($"重定向目标不在任务作用域内，放弃本页: {url} -> {target}", nameof(CrawlerEngine));
                    return null;
                }
                if (!context.UrlSet.Add(normalizedTarget))
                {
                    LogHelper.Debug($"重定向目标已爬取或已在队列中，放弃本页: {url} -> {target}", nameof(CrawlerEngine));
                    return null;
                }

                LogHelper.Debug($"跟随重定向 ({fetch.StatusCode}) | {currentUri} -> {target}", nameof(CrawlerEngine));
                currentUri = target;
                continue;
            }

            if (fetch.Html == null)
            {
                return pageResult;
            }

            pageResult.Links = await ExtractLinksAsync(fetch.Html, currentUri.AbsoluteUri).ConfigureAwait(false);
            pageResult.ParsedData = await RunPluginAsync(context.Plugin, fetch.Html, pageResult.FinalUrl, ct).ConfigureAwait(false);
            return pageResult;
        }
    }

    /// <summary>
    /// 重定向目标的作用域：起始页只要求同主机（SameHostOnly 时）；跟随而来的页面同样要经过插件的 ShouldFollowLink
    /// </summary>
    private bool IsRedirectInScope(CrawlContext context, Uri from, Uri target, int depth)
    {
        if (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps) return false;
        if (depth <= 1)
        {
            return !_crawlerSettings.SameHostOnly
                   || string.Equals(target.Host, from.Host, StringComparison.OrdinalIgnoreCase)
                   || context.StartHosts.Contains(target.Host);
        }
        return IsInScope(context, target, depth - 1);
    }

    /// <summary>
    /// 发出一个请求（含重试）。每次尝试前都经过按主机限速；超时覆盖读取正文的全过程
    /// </summary>
    private async Task<FetchOutcome> FetchAsync(Uri url, TimeSpan interval, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        var host = HostRateLimiter.HostKey(url);
        var maxRetries = Math.Max(0, _crawlerSettings.MaxRequestRetries);
        var timeout = TimeSpan.FromSeconds(Math.Max(1, _crawlerSettings.RequestTimeoutSeconds));

        for (var attempt = 0; ; attempt++)
        {
            var canRetry = attempt < maxRetries;
            await _rateLimiter.WaitAsync(host, interval, ct).ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml;q=0.9,*/*;q=0.1");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
                var status = (int)response.StatusCode;

                if (status is >= 300 and < 400)
                {
                    var location = response.Headers.Location;
                    Uri? target = null;
                    if (location != null && Uri.TryCreate(url, location, out var resolved))
                    {
                        target = resolved;
                    }
                    return new FetchOutcome(status, IsRedirect: true, RedirectTarget: target);
                }

                if (IsRetryableStatus(status) && canRetry)
                {
                    var delay = RetryDelay(response, attempt);
                    _rateLimiter.Defer(host, delay);
                    LogHelper.Warn($"页面请求返回 {status}，{delay.TotalSeconds:0.#} 秒后重试（第 {attempt + 1} 次）| URL: {url}", nameof(CrawlerEngine));
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                    {
                        // 不再重试，但仍让该主机的后续请求遵守 Retry-After
                        _rateLimiter.Defer(host, RetryDelay(response, attempt));
                    }
                    LogHelper.Warn($"页面请求失败 | URL: {url} | 状态码: {status}", nameof(CrawlerEngine));
                    return new FetchOutcome(status);
                }

                if (!IsHtml(response))
                {
                    LogHelper.Debug($"不是 HTML 页面（{response.Content.Headers.ContentType?.MediaType}），不读取正文 | URL: {url}", nameof(CrawlerEngine));
                    return new FetchOutcome(status);
                }

                var html = await ReadBodyAsync(response, timeoutCts.Token).ConfigureAwait(false);
                if (html == null)
                {
                    LogHelper.Warn($"响应正文超过上限 {_crawlerSettings.MaxResponseBytes} 字节，不解析 | URL: {url}", nameof(CrawlerEngine));
                    return new FetchOutcome(status);
                }

                LogHelper.Debug($"页面加载成功 | URL: {url} | 内容长度: {html.Length}", nameof(CrawlerEngine));
                return new FetchOutcome(status, Html: html);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                if (canRetry)
                {
                    _rateLimiter.Defer(host, BackoffDelay(attempt));
                    LogHelper.Warn($"页面请求超时（{timeout.TotalSeconds:0} 秒），重试（第 {attempt + 1} 次）| URL: {url}", nameof(CrawlerEngine));
                    continue;
                }
                LogHelper.Warn($"页面请求超时 | URL: {url} | 超时时间: {timeout.TotalSeconds:0}s", nameof(CrawlerEngine));
                return new FetchOutcome((int)HttpStatusCode.RequestTimeout);
            }
            catch (HttpRequestException ex)
            {
                if (canRetry)
                {
                    _rateLimiter.Defer(host, BackoffDelay(attempt));
                    LogHelper.Warn($"HTTP请求异常，重试（第 {attempt + 1} 次）| URL: {url} | {ex.Message}", nameof(CrawlerEngine));
                    continue;
                }
                LogHelper.Error(ex, $"HTTP请求异常 | URL: {url}", nameof(CrawlerEngine));
                return new FetchOutcome(NoResponseStatusCode);
            }
        }
    }

    private static bool IsRetryableStatus(int status) => status is 408 or 429 or >= 500 and <= 599;

    private static TimeSpan BackoffDelay(int attempt) => TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt)));

    /// <summary>Retry-After（秒数或日期）优先，上限 5 分钟；没有时按指数退避</summary>
    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        TimeSpan? delay = retryAfter?.Delta;
        if (delay == null && retryAfter?.Date is { } date)
        {
            delay = date - DateTimeOffset.UtcNow;
        }
        if (delay is { } value && value > TimeSpan.Zero)
        {
            return value < MaxRetryAfter ? value : MaxRetryAfter;
        }
        return BackoffDelay(attempt);
    }

    /// <summary>没有 Content-Type 时按 HTML 处理（兼容不规范的服务器）</summary>
    private static bool IsHtml(HttpResponseMessage response)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        return mediaType == null
               || mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase)
               || mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>读取正文，超过 <see cref="CrawlerSettings.MaxResponseBytes"/> 时返回 null</summary>
    private async Task<string?> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var maxBytes = Math.Max(1, _crawlerSettings.MaxResponseBytes);
        if (response.Content.Headers.ContentLength is { } length && length > maxBytes)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }
            buffer.Write(chunk, 0, read);
        }

        var encoding = Encoding.UTF8;
        var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"', ' ');
        if (!string.IsNullOrEmpty(charset))
        {
            try { encoding = Encoding.GetEncoding(charset); } catch (ArgumentException) { /* 未知字符集按 UTF-8 */ }
        }
        buffer.Position = 0;
        using var reader = new StreamReader(buffer, encoding, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    /// <summary>调用插件解析页面：有超时；失败或出错时返回空字典，页面保留 HTTP 状态码</summary>
    private async Task<Dictionary<string, object>> RunPluginAsync(ICrawlerPlugin plugin, string html, string url, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(1, _crawlerSettings.PluginTimeoutSeconds));
        try
        {
            var pluginResult = await plugin.ExecuteAsync(new PluginContext { Html = html, Url = url }).WaitAsync(timeout, ct).ConfigureAwait(false);
            if (pluginResult is { Success: true })
            {
                return pluginResult.Data ?? [];
            }
            LogHelper.Warn($"插件 [{plugin.Name}] 解析失败 | URL: {url} | 错误: {pluginResult?.ErrorMessage ?? "插件执行返回未知错误"}", nameof(CrawlerEngine));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            LogHelper.Error($"插件 [{plugin.Name}] 解析超时（{timeout.TotalSeconds:0} 秒）| URL: {url}", nameof(CrawlerEngine));
        }
        catch (Exception ex)
        {
            LogHelper.Error(ex, $"插件 [{plugin.Name}] 解析异常 | URL: {url}", nameof(CrawlerEngine));
        }
        return [];
    }
    #endregion

    #region 链接提取
    private static async Task<List<string>> ExtractLinksAsync(string html, string baseUrl)
    {
        var links = new List<string>();
        if (string.IsNullOrWhiteSpace(html) || string.IsNullOrWhiteSpace(baseUrl))
            return links;

        try
        {
            using var context = BrowsingContext.New(Configuration.Default);
            using var document = await context.OpenAsync(req => req.Content(html).Address(baseUrl)).ConfigureAwait(false);

            var baseUri = new Uri(baseUrl);
            var uniqueLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var element in document.QuerySelectorAll("a[href]"))
            {
                string rawHref = element.GetAttribute("href")?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(rawHref)) continue;

                if (rawHref.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
                    rawHref.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) ||
                    rawHref.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (Uri.TryCreate(baseUri, rawHref, out var absoluteUri) &&
                    (absoluteUri.Scheme == Uri.UriSchemeHttp || absoluteUri.Scheme == Uri.UriSchemeHttps))
                {
                    uniqueLinks.Add(absoluteUri.AbsoluteUri);
                }
            }

            links = uniqueLinks.ToList();
        }
        catch (Exception ex)
        {
            LogHelper.Error(ex, $"链接提取失败 | 基础URL: {baseUrl}", nameof(CrawlerEngine));
        }

        return links;
    }
    #endregion

    #region URL 规范化（引擎通用）
    /// <summary>
    /// 对 URL 进行规范化处理：移除 Fragment (#之后内容)，统一小写，移除末尾斜杠。
    /// </summary>
    private static string NormalizeUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var builder = new UriBuilder(uri) { Fragment = "" };
            string normalized = builder.Uri.ToString().TrimEnd('/');
            return normalized.ToLowerInvariant();
        }
        return url;
    }
    #endregion

    #region 进度上报
    private void OnProgressUpdated(CrawlerTask task, int crawled, int total)
    {
        ProgressUpdated?.Invoke(this, new TaskProgressUpdatedEventArgs(
            task.TaskId,
            task.TaskName,
            task.Status,
            crawled,
            total,
            task.AssignedNode));
    }
    #endregion

    #region 内部类型
    /// <summary>一次任务执行的状态</summary>
    private sealed class CrawlContext
    {
        public CrawlContext(CrawlerTask task, ICrawlerPlugin plugin, TimeSpan baseInterval, IEnumerable<Uri> startUris)
        {
            Task = task;
            Plugin = plugin;
            BaseInterval = baseInterval;
            StartHosts = new HashSet<string>(startUris.Select(u => u.Host), StringComparer.OrdinalIgnoreCase);
        }

        public CrawlerTask Task { get; }
        public ICrawlerPlugin Plugin { get; }
        public TimeSpan BaseInterval { get; }
        public HashSet<string> StartHosts { get; }
        public HashSet<string> UrlSet { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Queue<(string Url, int Depth)> Queue { get; } = new();
        public bool CapacityWarned { get; set; }
    }

    /// <param name="StatusCode">HTTP 状态码；没有响应时为 0，超时为 408</param>
    /// <param name="IsRedirect">3xx 响应</param>
    /// <param name="RedirectTarget">解析后的重定向目标（Location 缺失或无效时为 null）</param>
    /// <param name="Html">成功读取的 HTML 正文；不是 HTML、正文超限或失败时为 null</param>
    private readonly record struct FetchOutcome(int StatusCode, bool IsRedirect = false, Uri? RedirectTarget = null, string? Html = null);
    #endregion
}
