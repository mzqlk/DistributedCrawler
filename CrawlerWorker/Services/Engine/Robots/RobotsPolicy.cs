using CrawlerCore.Infrastructure;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CrawlerWorker.Services.Engine.Robots
{
    /// <summary>robots 检查的结论</summary>
    public enum RobotsVerdict
    {
        /// <summary>允许请求</summary>
        Allowed,

        /// <summary>robots.txt 禁止，不请求</summary>
        Disallowed,

        /// <summary>robots.txt 暂时无法获取（5xx、429、网络错误、超时），按 RFC 9309 暂不请求该主机</summary>
        Unavailable
    }

    /// <param name="Verdict">结论</param>
    /// <param name="CrawlDelay">该主机 robots.txt 中的 Crawl-delay（没有时为 0）</param>
    public readonly record struct RobotsDecision(RobotsVerdict Verdict, TimeSpan CrawlDelay)
    {
        public bool IsAllowed => Verdict == RobotsVerdict.Allowed;
    }

    /// <summary>
    /// robots.txt 策略（W-07）：引擎在请求起始 URL、子链接和每一跳重定向目标之前都要经过它
    /// </summary>
    public interface IRobotsPolicy
    {
        Task<RobotsDecision> CheckAsync(Uri url, CancellationToken cancellationToken);
    }

    /// <summary>
    /// 每个站点（协议 + 主机 + 端口）首次访问时抓取 /robots.txt 并缓存（<see cref="CrawlerSettings.RobotsCacheHours"/>）。
    /// <list type="bullet">
    /// <item>2xx：按内容解析；4xx（429 除外）：视为没有限制；</item>
    /// <item>5xx、429、网络错误、超时：视为暂时不可用，暂不请求该主机，1 分钟后再试；</item>
    /// <item>robots.txt 的重定向最多跟随 5 次，超过视为没有限制（RFC 9309 §2.3.1.2）。</item>
    /// </list>
    /// 抓取 robots.txt 本身也经过按主机限速。
    /// </summary>
    public sealed class RobotsPolicy : IRobotsPolicy
    {
        private const int MaxRedirects = 5;
        private static readonly TimeSpan UnavailableRetryInterval = TimeSpan.FromMinutes(1);

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IHostRateLimiter _rateLimiter;
        private readonly CrawlerSettings _settings;
        private readonly TimeProvider _timeProvider;
        private readonly string _productToken;
        private readonly ConcurrentDictionary<string, SiteEntry> _sites = new(StringComparer.OrdinalIgnoreCase);

        public RobotsPolicy(IHttpClientFactory httpClientFactory, IHostRateLimiter rateLimiter, IOptions<CrawlerSettings> settings, TimeProvider timeProvider)
        {
            _httpClientFactory = httpClientFactory;
            _rateLimiter = rateLimiter;
            _settings = settings.Value;
            _timeProvider = timeProvider;
            _productToken = GetProductToken(_settings.DefaultUserAgent);
        }

        /// <summary>User-Agent 中的产品名（第一个 “/” 或空格之前的部分），robots.txt 用它匹配规则组</summary>
        public static string GetProductToken(string? userAgent)
        {
            if (string.IsNullOrWhiteSpace(userAgent)) return "*";
            var end = userAgent.IndexOfAny(new[] { '/', ' ' });
            return end > 0 ? userAgent[..end] : userAgent.Trim();
        }

        public async Task<RobotsDecision> CheckAsync(Uri url, CancellationToken cancellationToken)
        {
            var siteKey = url.GetLeftPart(UriPartial.Authority);
            var site = _sites.GetOrAdd(siteKey, _ => new SiteEntry());

            await site.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // 首次访问（ExpiresAt 为最小值）、缓存过期、或上次不可用已过了重试间隔时重新获取
                if (_timeProvider.GetUtcNow() >= site.ExpiresAt)
                {
                    await RefreshAsync(siteKey, site, cancellationToken).ConfigureAwait(false);
                }

                if (site.Rules == null)
                {
                    return new RobotsDecision(RobotsVerdict.Unavailable, TimeSpan.Zero);
                }
                return new RobotsDecision(site.Rules.IsAllowed(url) ? RobotsVerdict.Allowed : RobotsVerdict.Disallowed, site.Rules.CrawlDelay);
            }
            finally
            {
                site.Gate.Release();
            }
        }

        private async Task RefreshAsync(string siteKey, SiteEntry site, CancellationToken cancellationToken)
        {
            var now = _timeProvider.GetUtcNow();
            try
            {
                site.Rules = await FetchAsync(new Uri(siteKey + "/robots.txt"), cancellationToken).ConfigureAwait(false);
                site.ExpiresAt = now.AddHours(Math.Max(1, _settings.RobotsCacheHours));
                LogHelper.Info($"robots.txt 已更新: {siteKey}，规则 {site.Rules.RuleCount} 条，Crawl-delay {site.Rules.CrawlDelay.TotalSeconds:0.##} 秒");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 暂时不可用：不沿用可能过期的旧规则，按 RFC 9309 视为全部禁止，稍后再试
                site.Rules = null;
                site.ExpiresAt = now + UnavailableRetryInterval;
                LogHelper.Warn($"robots.txt 暂时无法获取，{UnavailableRetryInterval.TotalSeconds:0} 秒内不请求该站点: {siteKey}（{ex.Message}）");
            }
        }

        private async Task<RobotsTxt> FetchAsync(Uri robotsUrl, CancellationToken cancellationToken)
        {
            var client = _httpClientFactory.CreateClient(CrawlerEngine.HttpClientName);
            var current = robotsUrl;
            for (var redirects = 0; ; redirects++)
            {
                await _rateLimiter.WaitAsync(HostRateLimiter.HostKey(current), TimeSpan.FromMilliseconds(Math.Max(0, _settings.DelayBetweenRequestsMs)), cancellationToken)
                    .ConfigureAwait(false);

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _settings.RequestTimeoutSeconds)));
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));

                HttpResponseMessage response;
                try
                {
                    response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException($"获取 {current} 超时");
                }

                using (response)
                {
                    var status = (int)response.StatusCode;
                    if (status is >= 300 and < 400)
                    {
                        if (redirects >= MaxRedirects || response.Headers.Location == null)
                        {
                            return RobotsTxt.AllowAll;
                        }
                        current = new Uri(current, response.Headers.Location);
                        continue;
                    }
                    if (response.StatusCode == HttpStatusCode.TooManyRequests || status >= 500)
                    {
                        throw new HttpRequestException($"robots.txt 返回 {status}", null, response.StatusCode);
                    }
                    if (status >= 400)
                    {
                        return RobotsTxt.AllowAll;
                    }

                    var content = await ReadLimitedAsync(response.Content, timeoutCts.Token).ConfigureAwait(false);
                    return RobotsTxt.Parse(content, _productToken);
                }
            }
        }

        private static async Task<string> ReadLimitedAsync(HttpContent content, CancellationToken cancellationToken)
        {
            await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[RobotsTxt.MaxParsedBytes];
            var total = 0;
            int read;
            while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
            }
            using var reader = new StreamReader(new MemoryStream(buffer, 0, total), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        private sealed class SiteEntry
        {
            public SemaphoreSlim Gate { get; } = new(1, 1);
            /// <summary>当前规则；null 表示 robots.txt 暂时不可用</summary>
            public RobotsTxt? Rules { get; set; }
            public DateTimeOffset ExpiresAt { get; set; }
        }
    }
}
