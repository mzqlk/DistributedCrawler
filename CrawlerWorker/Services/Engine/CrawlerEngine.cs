using CrawlerCore.Entities;
using CrawlerCore.Events;
using CrawlerCore.Infrastructure;
using CrawlerCore.Interfaces;
using Microsoft.Extensions.Options;
using System.Net.Http;
using System.Net.Http.Headers;
using TaskStatus = CrawlerCore.Entities.TaskStatus;

namespace CrawlerWorker.Services.Engine;

/// <summary>
/// Worker爬虫执行引擎
/// </summary>
public sealed class CrawlerEngine(IHttpClientFactory httpClientFactory,
                                  IPluginManager pluginManager,
                                  IOptions<CrawlerSettings> crawlerSettings)
{
    #region 依赖注入（使用主构造函数）
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    private readonly IPluginManager _pluginManager = pluginManager ?? throw new ArgumentNullException(nameof(pluginManager));
    private readonly CrawlerSettings _crawlerSettings = crawlerSettings.Value ?? throw new ArgumentNullException(nameof(crawlerSettings));
    #endregion

    /// <summary>
    /// 进度更新事件
    /// </summary>
    public event EventHandler<TaskProgressUpdatedEventArgs>? ProgressUpdated;

    static CrawlerEngine() => LogHelper.Info("爬虫引擎类型初始化完成", nameof(CrawlerEngine));

    /// <summary>
    /// 执行爬虫任务（核心入口）
    /// </summary>
    public async Task<CrawlResult> ExecuteAsync(CrawlerTask task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentException.ThrowIfNullOrWhiteSpace(task.TaskId);

        var result = new CrawlResult { TaskId = task.TaskId };
        LogHelper.Info($"开始执行任务: {task.TaskId} | 插件: {task.PluginName}", nameof(CrawlerEngine));

        try
        {
            // 空集合防护
            var validUrls = task.StartUrls?.Where(u => !string.IsNullOrWhiteSpace(u)).ToList() ?? [];
            if (validUrls.Count is 0)
            {
                LogHelper.Warn($"任务无有效URL: {task.TaskId}", nameof(CrawlerEngine));
                task.Status = TaskStatus.Completed;
                return result;
            }

            // 插件校验
            var plugin = _pluginManager.GetPlugin(task.PluginName)
                ?? throw new KeyNotFoundException($"未找到插件: {task.PluginName}");

            // MaxPages 上限校验
            int maxPages = task.MaxPages <= 0 ? int.MaxValue : task.MaxPages;
            int crawledCount = 0;

            // 执行爬取
            foreach (var url in validUrls)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 达到页面上限终止爬取
                if (crawledCount >= maxPages)
                {
                    LogHelper.Warn($"达到最大爬取页面数上限: {maxPages}", nameof(CrawlerEngine));
                    break;
                }

                // 传递插件实例，避免重复获取
                var pageResult = await CrawlSinglePageAsync(url, task, plugin, 1, string.Empty, cancellationToken);
                result.PageResults.Add(pageResult);
                crawledCount++;

                // 触发原生进度事件
                OnProgressUpdated(task, crawledCount, maxPages);
            }

            result.CompletedAt = DateTime.UtcNow;
            task.Status = TaskStatus.Completed;
            LogHelper.Info($"任务完成: {task.TaskId} | 页面数: {result.PageResults.Count}", nameof(CrawlerEngine));
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

    /// <summary>
    /// 单页爬取完整实现（接收插件实例，避免重复获取）
    /// </summary>
    private async Task<PageResult> CrawlSinglePageAsync(
        string url,
        CrawlerTask task,
        ICrawlerPlugin plugin,  // 新增参数：接收插件实例
        int depth,
        string parentUrl,
        CancellationToken ct)
    {
        var pageResult = new PageResult { Url = url };
        LogHelper.Debug($"开始爬取页面 | URL: {url} | 深度: {depth}", nameof(CrawlerEngine));

        try
        {
            #region 1. 初始化HTTP客户端
            var client = _httpClientFactory.CreateClient("CrawlerEngineClient");

            // 仅在未设置时添加 User-Agent
            if (!client.DefaultRequestHeaders.UserAgent.Any())
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd(_crawlerSettings.DefaultUserAgent);
            }

            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
            client.Timeout = TimeSpan.FromSeconds(_crawlerSettings.RequestTimeoutSeconds);

            // 请求延迟（防封禁）
            await Task.Delay(_crawlerSettings.DelayBetweenRequestsMs, ct);
            #endregion

            #region 2. 发送HTTP GET请求
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            pageResult.StatusCode = (int)response.StatusCode;

            // 非200状态码直接返回
            if (!response.IsSuccessStatusCode)
            {
                LogHelper.Warn($"页面请求失败 | URL: {url} | 状态码: {pageResult.StatusCode}", nameof(CrawlerEngine));
                return pageResult;
            }

            // 读取HTML内容
            string html = await response.Content.ReadAsStringAsync(ct);
            LogHelper.Debug($"页面加载成功 | URL: {url} | 内容长度: {html.Length}", nameof(CrawlerEngine));
            #endregion

            #region 3. 插件调用（直接使用传入的插件实例）
            // 构建插件执行上下文
            var baseContext = new PluginContext { Html = html, Url = url };
            var pluginResult = await plugin.ExecuteAsync(baseContext);
            #endregion

            #region 4. 结果封装
            if (pluginResult is { Success: true })
            {
                pageResult.ParsedData = pluginResult.Data ?? [];
                LogHelper.Debug($"插件解析成功 | URL: {url} | 解析字段数: {pageResult.ParsedData.Count}", nameof(CrawlerEngine));
            }
            else
            {
                string errorMsg = pluginResult?.ErrorMessage ?? "插件执行返回未知错误";
                LogHelper.Error($"插件解析失败 | URL: {url} | 错误: {errorMsg}", nameof(CrawlerEngine));
            }
            #endregion
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            LogHelper.Warn($"页面请求超时 | URL: {url} | 超时时间: {_crawlerSettings.RequestTimeoutSeconds}s", nameof(CrawlerEngine));
            pageResult.StatusCode = 408;
        }
        catch (TaskCanceledException)
        {
            LogHelper.Warn($"页面爬取被取消 | URL: {url}", nameof(CrawlerEngine));
            pageResult.StatusCode = 499;
        }
        catch (HttpRequestException ex)
        {
            LogHelper.Error(ex, $"HTTP请求异常 | URL: {url}", nameof(CrawlerEngine));
            pageResult.StatusCode = ex.StatusCode.HasValue ? (int)ex.StatusCode : 500;
        }
        catch (Exception ex)
        {
            LogHelper.Error(ex, $"页面爬取未知异常 | URL: {url}", nameof(CrawlerEngine));
            pageResult.StatusCode = 500;
        }

        return pageResult;
    }

    /// <summary>
    /// 触发进度更新事件
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
}