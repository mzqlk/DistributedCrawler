using AngleSharp;
using AngleSharp.Dom;
using CrawlerCore.Entities;
using CrawlerCore.Events;
using CrawlerCore.Infrastructure;
using CrawlerCore.Interfaces;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using TaskStatus = CrawlerCore.Entities.TaskStatus;

namespace CrawlerWorker.Services.Engine;

/// <summary>
/// Worker爬虫执行引擎
/// </summary>
public sealed class CrawlerEngine
{
    #region 常量与依赖注入
    private const int MaxUrlHashSetCapacity = 100000;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IPluginManager _pluginManager;
    private readonly CrawlerSettings _crawlerSettings;
    #endregion

    #region 事件定义
    public event EventHandler<TaskProgressUpdatedEventArgs>? ProgressUpdated;
    #endregion

    #region 构造函数
    public CrawlerEngine(IHttpClientFactory httpClientFactory,
                         IPluginManager pluginManager,
                         IOptions<CrawlerSettings> crawlerSettings)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _pluginManager = pluginManager ?? throw new ArgumentNullException(nameof(pluginManager));
        _crawlerSettings = crawlerSettings.Value ?? throw new ArgumentNullException(nameof(crawlerSettings));
    }
    #endregion

    static CrawlerEngine() => LogHelper.Info("爬虫引擎类型初始化完成", nameof(CrawlerEngine));

    #region 核心执行入口
    public async Task<CrawlResult> ExecuteAsync(CrawlerTask task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentException.ThrowIfNullOrWhiteSpace(task.TaskId);

        var result = new CrawlResult { TaskId = task.TaskId };
        var crawledUrlSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var crawlQueue = new Queue<(string Url, int Depth)>();
        LogHelper.Info($"开始执行任务: {task.TaskId} | 插件: {task.PluginName} | 最大深度: {task.MaxDepth}", nameof(CrawlerEngine));

        try
        {
            var validUrls = task.StartUrls?.Where(u => !string.IsNullOrWhiteSpace(u)).ToList() ?? [];
            if (validUrls.Count == 0)
            {
                LogHelper.Warn($"任务无有效URL: {task.TaskId}", nameof(CrawlerEngine));
                task.Status = TaskStatus.Completed;
                OnProgressUpdated(task, 0, 0);
                return result;
            }

            var plugin = _pluginManager.GetPlugin(task.PluginName)
                ?? throw new KeyNotFoundException($"未找到插件: {task.PluginName}");

            var pluginConfig = plugin.GetDefaultConfiguration() ?? new Dictionary<string, string>();
            int crawlDelayMs = _crawlerSettings.DelayBetweenRequestsMs;
            if (pluginConfig.TryGetValue("CrawlDelay", out string? crawlDelayStr) && int.TryParse(crawlDelayStr, out int pluginDelayMs))
            {
                crawlDelayMs = pluginDelayMs;
                LogHelper.Info($"使用插件特定爬取间隔: {crawlDelayMs}ms (插件: {task.PluginName})", nameof(CrawlerEngine));
            }

            int maxPages = task.MaxPages <= 0 ? int.MaxValue : task.MaxPages;
            int crawledCount = 0;

            foreach (var url in validUrls)
            {
                string normalized = NormalizeUrl(url);
                if (crawledUrlSet.Add(normalized))
                    crawlQueue.Enqueue((url, 1));
            }

            int estimatedTotalPages = Math.Min(crawlQueue.Count, maxPages);
            OnProgressUpdated(task, 0, estimatedTotalPages);

            while (crawlQueue.Count > 0 && crawledCount < maxPages)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (crawledUrlSet.Count >= MaxUrlHashSetCapacity)
                {
                    LogHelper.Warn($"URL去重集合达到容量上限 {MaxUrlHashSetCapacity}，停止新增URL爬取", nameof(CrawlerEngine));
                    break;
                }

                var (currentUrl, currentDepth) = crawlQueue.Dequeue();
                LogHelper.Debug($"处理队列URL: {currentUrl} | 当前深度: {currentDepth}", nameof(CrawlerEngine));

                var pageResult = await CrawlSinglePageAsync(currentUrl, task, plugin, currentDepth, crawlDelayMs, cancellationToken);
                result.PageResults.Add(pageResult);
                crawledCount++;

                if (!string.IsNullOrEmpty(pageResult.FinalUrl) &&
                    !string.Equals(pageResult.Url, pageResult.FinalUrl, StringComparison.OrdinalIgnoreCase))
                {
                    crawledUrlSet.Add(NormalizeUrl(pageResult.FinalUrl));
                }

                int currentTotalPages = Math.Max(crawledCount + crawlQueue.Count, maxPages);
                OnProgressUpdated(task, crawledCount, Math.Min(currentTotalPages, maxPages));

                // 深度限制内，提取并过滤子链接
                if (currentDepth < task.MaxDepth && pageResult.Links.Count > 0)
                {
                    int nextDepth = currentDepth + 1;
                    foreach (var subLink in pageResult.Links)
                    {
                        // 【关键】由插件决定是否跟随此链接
                        if (!plugin.ShouldFollowLink(subLink, currentDepth))
                        {
                            LogHelper.Debug($"插件 [{plugin.Name}] 拒绝了链接: {subLink}");
                            continue;
                        }

                        string normalizedLink = NormalizeUrl(subLink);
                        if (crawledUrlSet.Add(normalizedLink))
                        {
                            crawlQueue.Enqueue((subLink, nextDepth));
                        }
                    }
                    LogHelper.Debug($"提取并过滤子链接 {pageResult.Links.Count} 个，有效入队 {crawlQueue.Count} 个", nameof(CrawlerEngine));
                }
            }

            result.CompletedAt = DateTime.UtcNow;
            task.Status = TaskStatus.Completed;
            LogHelper.Info($"任务完成: {task.TaskId} | 总爬取页面: {crawledCount} | 去重URL数: {crawledUrlSet.Count}", nameof(CrawlerEngine));
        }
        catch (OperationCanceledException)
        {
            task.Status = TaskStatus.Failed;
            LogHelper.Warn($"任务被主动取消: {task.TaskId}", nameof(CrawlerEngine));
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
    #endregion

    #region 单页爬取
    private async Task<PageResult> CrawlSinglePageAsync(
        string url,
        CrawlerTask task,
        ICrawlerPlugin plugin,
        int depth,
        int crawlDelayMs,
        CancellationToken ct)
    {
        var pageResult = new PageResult { Url = url };
        LogHelper.Debug($"开始爬取页面 | URL: {url} | 深度: {depth}", nameof(CrawlerEngine));

        const int maxRedirects = 10;
        var redirectCount = 0;
        var currentUrl = url;
        var visitedRedirectUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { url };

        var client = _httpClientFactory.CreateClient("CrawlerEngineClient");
        if (!client.DefaultRequestHeaders.UserAgent.Any())
            client.DefaultRequestHeaders.UserAgent.ParseAdd(_crawlerSettings.DefaultUserAgent);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        client.Timeout = TimeSpan.FromSeconds(_crawlerSettings.RequestTimeoutSeconds);

        try
        {
            while (redirectCount <= maxRedirects)
            {
                ct.ThrowIfCancellationRequested();

                await Task.Delay(crawlDelayMs, ct);

                using var response = await client.GetAsync(currentUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                pageResult.StatusCode = (int)response.StatusCode;

                if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400)
                {
                    var location = response.Headers.Location;
                    if (location == null)
                    {
                        LogHelper.Warn($"重定向响应缺少 Location 头 | URL: {currentUrl}", nameof(CrawlerEngine));
                        break;
                    }

                    if (!Uri.TryCreate(new Uri(currentUrl), location, out var redirectUri))
                    {
                        LogHelper.Warn($"无法解析重定向 Location: {location} | 基础 URL: {currentUrl}", nameof(CrawlerEngine));
                        break;
                    }

                    var redirectUrl = redirectUri.AbsoluteUri;
                    LogHelper.Debug($"跟随重定向 ({response.StatusCode}) | {currentUrl} -> {redirectUrl}", nameof(CrawlerEngine));

                    if (!visitedRedirectUrls.Add(redirectUrl))
                    {
                        LogHelper.Warn($"检测到重定向循环，停止跟随 | URL: {redirectUrl}", nameof(CrawlerEngine));
                        break;
                    }

                    currentUrl = redirectUrl;
                    redirectCount++;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    LogHelper.Warn($"页面请求失败 | URL: {currentUrl} | 状态码: {pageResult.StatusCode}", nameof(CrawlerEngine));
                    break;
                }

                string html = await response.Content.ReadAsStringAsync(ct);
                LogHelper.Debug($"页面加载成功 | URL: {currentUrl} | 内容长度: {html.Length}", nameof(CrawlerEngine));

                pageResult.FinalUrl = currentUrl;
                pageResult.Links = await ExtractLinksAsync(html, currentUrl);

                var pluginContext = new PluginContext { Html = html, Url = currentUrl };
                var pluginResult = await plugin.ExecuteAsync(pluginContext);

                if (pluginResult is { Success: true })
                {
                    pageResult.ParsedData = pluginResult.Data ?? [];
                    LogHelper.Debug($"插件解析成功 | URL: {currentUrl} | 解析字段数: {pageResult.ParsedData.Count}", nameof(CrawlerEngine));
                }
                else
                {
                    string errorMsg = pluginResult?.ErrorMessage ?? "插件执行返回未知错误";
                    LogHelper.Error($"插件解析失败 | URL: {currentUrl} | 错误: {errorMsg}", nameof(CrawlerEngine));
                }

                break;
            }

            if (redirectCount > maxRedirects)
            {
                LogHelper.Warn($"超出最大重定向次数 {maxRedirects} | 最终 URL: {currentUrl}", nameof(CrawlerEngine));
                pageResult.StatusCode = 310;
            }
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            LogHelper.Warn($"页面请求超时 | URL: {currentUrl} | 超时时间: {_crawlerSettings.RequestTimeoutSeconds}s", nameof(CrawlerEngine));
            pageResult.StatusCode = 408;
        }
        catch (TaskCanceledException)
        {
            LogHelper.Warn($"页面爬取被取消 | URL: {currentUrl}", nameof(CrawlerEngine));
            pageResult.StatusCode = 499;
        }
        catch (HttpRequestException ex)
        {
            LogHelper.Error(ex, $"HTTP请求异常 | URL: {currentUrl}", nameof(CrawlerEngine));
            pageResult.StatusCode = ex.StatusCode.HasValue ? (int)ex.StatusCode : 500;
        }
        catch (Exception ex)
        {
            LogHelper.Error(ex, $"页面爬取未知异常 | URL: {currentUrl}", nameof(CrawlerEngine));
            pageResult.StatusCode = 500;
        }

        return pageResult;
    }
    #endregion

    #region 链接提取
    private async Task<List<string>> ExtractLinksAsync(string html, string baseUrl)
    {
        var links = new List<string>();
        if (string.IsNullOrWhiteSpace(html) || string.IsNullOrWhiteSpace(baseUrl))
            return links;

        try
        {
            var config = Configuration.Default;
            var context = BrowsingContext.New(config);
            using var document = await context.OpenAsync(req => req.Content(html).Address(baseUrl));

            var uniqueLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var anchorElements = document.QuerySelectorAll("a[href]");

            foreach (var element in anchorElements)
            {
                string rawHref = element.GetAttribute("href")?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(rawHref)) continue;

                if (rawHref.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
                    rawHref.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) ||
                    rawHref.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (Uri.TryCreate(new Uri(baseUrl), rawHref, out var absoluteUri) &&
                    (absoluteUri.Scheme == Uri.UriSchemeHttp || absoluteUri.Scheme == Uri.UriSchemeHttps))
                {
                    uniqueLinks.Add(absoluteUri.AbsoluteUri);
                }
            }

            links = uniqueLinks.ToList();
            LogHelper.Debug($"链接提取完成 | 基础URL: {baseUrl} | 有效链接数: {links.Count}", nameof(CrawlerEngine));
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
    private string NormalizeUrl(string url)
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
}