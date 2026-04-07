namespace CrawlerCore.Infrastructure
{
    /// <summary>
    /// 爬虫全局配置项（100%对齐原项目appsettings.json规范）
    /// </summary>
    public class CrawlerSettings
    {
        public const string SectionName = "CrawlerSettings";

        public string DefaultUserAgent { get; set; } = "Mozilla/5.0 (compatible; DistributedCrawler/1.0)";
        public int RequestTimeoutSeconds { get; set; } = 30;
        public int MaxConcurrentRequests { get; set; } = 10;
        public int DelayBetweenRequestsMs { get; set; } = 1000;
    }
}