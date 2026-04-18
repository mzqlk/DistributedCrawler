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
public sealed class CrawlerEngine(IHttpClientFactory httpClientFactory,
                                  IPluginManager pluginManager,
                                  IOptions<CrawlerSettings> crawlerSettings)
{
    #region 常量与依赖注入
    // URL去重最大容量（风险防控：内存超限告警）
    private const int MaxUrlHashSetCapacity = 100000;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    private readonly IPluginManager _pluginManager = pluginManager ?? throw new ArgumentNullException(nameof(pluginManager));
    private readonly CrawlerSettings _crawlerSettings = crawlerSettings.Value ?? throw new ArgumentNullException(nameof(crawlerSettings));
    #endregion

    #region 事件定义
    /// <summary>
    /// 进度更新事件（实时上报Master）
    /// </summary>
    public event EventHandler<TaskProgressUpdatedEventArgs>? ProgressUpdated;
    #endregion

    #region 静态构造函数
    static CrawlerEngine() => LogHelper.Info("爬虫引擎类型初始化完成", nameof(CrawlerEngine));
    #endregion

    #region 核心执行入口（队列式深度爬取）
    /// <summary>
    /// 执行爬虫任务
    /// </summary>
    public async Task<CrawlResult> ExecuteAsync(CrawlerTask task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentException.ThrowIfNullOrWhiteSpace(task.TaskId);

        var result = new CrawlResult { TaskId = task.TaskId };
        // URL去重集合（全局去重，防止重复爬取）
        var crawledUrlSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 待爬队列：(URL, 当前深度)
        var crawlQueue = new Queue<(string Url, int Depth)>();
        LogHelper.Info($"开始执行任务: {task.TaskId} | 插件: {task.PluginName} | 最大深度: {task.MaxDepth}", nameof(CrawlerEngine));

        try
        {
            // 1. 初始化有效起始URL
            var validUrls = task.StartUrls?.Where(u => !string.IsNullOrWhiteSpace(u)).ToList() ?? [];
            if (validUrls.Count is 0)
            {
                LogHelper.Warn($"任务无有效URL: {task.TaskId}", nameof(CrawlerEngine));
                task.Status = TaskStatus.Completed;
                OnProgressUpdated(task, 0, 0);
                return result;
            }

            // 2. 插件校验
            var plugin = _pluginManager.GetPlugin(task.PluginName)
                ?? throw new KeyNotFoundException($"未找到插件: {task.PluginName}");

            // 3. 初始化爬取参数
            int maxPages = task.MaxPages <= 0 ? int.MaxValue : task.MaxPages;
            int crawledCount = 0;
            // 起始URL入队+去重
            foreach (var url in validUrls)
            {
                if (crawledUrlSet.Add(url))
                    crawlQueue.Enqueue((url, 1));
            }

            int estimatedTotalPages = Math.Min(crawlQueue.Count, maxPages);
            OnProgressUpdated(task, 0, estimatedTotalPages);

            // 4. 队列循环爬取（核心深度逻辑，避免递归栈溢出）
            while (crawlQueue.Count > 0 && crawledCount < maxPages)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 内存超限告警（风险防控）
                if (crawledUrlSet.Count >= MaxUrlHashSetCapacity)
                {
                    LogHelper.Warn($"URL去重集合达到容量上限 {MaxUrlHashSetCapacity}，停止新增URL爬取", nameof(CrawlerEngine));
                    break;
                }

                // 出队待爬项
                var (currentUrl, currentDepth) = crawlQueue.Dequeue();
                LogHelper.Debug($"处理队列URL: {currentUrl} | 当前深度: {currentDepth}", nameof(CrawlerEngine));

                // 执行单页爬取
                var pageResult = await CrawlSinglePageAsync(currentUrl, task, plugin, currentDepth, string.Empty, cancellationToken);
                result.PageResults.Add(pageResult);
                crawledCount++;

                // 如果发生重定向，将最终爬取的 URL 也加入全局去重集合，避免后续重复爬取
                if (!string.IsNullOrEmpty(pageResult.FinalUrl) &&
                    !string.Equals(pageResult.Url, pageResult.FinalUrl, StringComparison.OrdinalIgnoreCase))
                {
                    crawledUrlSet.Add(pageResult.FinalUrl);
                }

                // 实时上报进度
                int currentTotalPages = Math.Max(crawledCount + crawlQueue.Count, maxPages);
                OnProgressUpdated(task, crawledCount, Math.Min(currentTotalPages, maxPages));

                // 5. 深度限制判断：未达最大深度则提取子链接入队
                if (currentDepth < task.MaxDepth && pageResult.Links.Count > 0)
                {
                    int nextDepth = currentDepth + 1;
                    foreach (var subLink in pageResult.Links)
                    {
                        // 去重后入队
                        if (crawledUrlSet.Add(subLink))
                            crawlQueue.Enqueue((subLink, nextDepth));
                    }
                    LogHelper.Debug($"提取子链接 {pageResult.Links.Count} 个 | 下一层深度: {nextDepth}", nameof(CrawlerEngine));
                }
            }

            // 任务完成收尾
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

    #region 单页爬取核心逻辑（含链接提取）
    /// <summary>
    /// 单页爬取完整实现（支持手动跟随跨协议重定向）
    /// </summary>
    private async Task<PageResult> CrawlSinglePageAsync(
        string url,
        CrawlerTask task,
        ICrawlerPlugin plugin,
        int depth,
        string parentUrl,
        CancellationToken ct)
    {
        var pageResult = new PageResult { Url = url };
        LogHelper.Debug($"开始爬取页面 | URL: {url} | 深度: {depth}", nameof(CrawlerEngine));

        const int maxRedirects = 10;          // 与配置中的 MaxAutomaticRedirections 保持一致
        var redirectCount = 0;
        var currentUrl = url;
        var visitedRedirectUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { url };

        // 在循环外创建 HttpClient，避免重复创建和请求头丢失
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

                // 请求延迟（防封禁）
                await Task.Delay(_crawlerSettings.DelayBetweenRequestsMs, ct);

                using var response = await client.GetAsync(currentUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                pageResult.StatusCode = (int)response.StatusCode;

                // 处理 3xx 重定向
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

                    // 防止重定向循环
                    if (!visitedRedirectUrls.Add(redirectUrl))
                    {
                        LogHelper.Warn($"检测到重定向循环，停止跟随 | URL: {redirectUrl}", nameof(CrawlerEngine));
                        break;
                    }

                    currentUrl = redirectUrl;
                    redirectCount++;
                    continue;   // 继续下一次请求
                }

                // 非重定向状态码，检查是否成功
                if (!response.IsSuccessStatusCode)
                {
                    LogHelper.Warn($"页面请求失败 | URL: {currentUrl} | 状态码: {pageResult.StatusCode}", nameof(CrawlerEngine));
                    break;
                }

                // 成功响应：读取内容、提取链接、调用插件
                string html = await response.Content.ReadAsStringAsync(ct);
                LogHelper.Debug($"页面加载成功 | URL: {currentUrl} | 内容长度: {html.Length}", nameof(CrawlerEngine));

                pageResult.FinalUrl = currentUrl;          // 记录重定向后的最终 URL
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

                break;   // 成功处理后退出循环
            }

            if (redirectCount > maxRedirects)
            {
                LogHelper.Warn($"超出最大重定向次数 {maxRedirects} | 最终 URL: {currentUrl}", nameof(CrawlerEngine));
                pageResult.StatusCode = 310;   // 自定义状态码表示重定向过多
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

    #region 【核心新增】HTML链接提取方法（AngleSharp实现）
    /// <summary>
    /// 引擎内置链接提取：解析HTML所有a标签，规范化为绝对URL
    /// </summary>
    /// <param name="html">页面HTML源码</param>
    /// <param name="baseUrl">当前页面基础URL（用于相对路径转绝对路径）</param>
    /// <returns>规范化后的绝对URL列表</returns>
    private async Task<List<string>> ExtractLinksAsync(string html, string baseUrl)
    {
        var links = new List<string>();
        if (string.IsNullOrWhiteSpace(html) || string.IsNullOrWhiteSpace(baseUrl))
            return links;

        try
        {
            // AngleSharp轻量级解析配置
            var config = Configuration.Default;
            var context = BrowsingContext.New(config);

            using var document = await context.OpenAsync(req => req.Content(html).Address(baseUrl));

            var uniqueLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 提取所有a标签href属性
            var anchorElements = document.QuerySelectorAll("a[href]");
            foreach (var element in anchorElements)
            {
                string rawHref = element.GetAttribute("href")?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(rawHref)) continue;

                // 过滤无效协议（mailto/tel/javascript等）
                if (rawHref.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
                    rawHref.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) ||
                    rawHref.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
                    continue;

                // 相对路径转绝对路径
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

    #region 进度事件触发
    /// <summary>
    /// 触发进度更新事件（对外上报Master）
    /// </summary>
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