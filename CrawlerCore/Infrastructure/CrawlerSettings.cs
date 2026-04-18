namespace CrawlerCore.Infrastructure
{
    /// <summary>
    /// 爬虫全局配置项
    /// </summary>
    public class CrawlerSettings
    {
        public const string SectionName = "CrawlerSettings";

        public string DefaultUserAgent { get; set; } = "Mozilla/5.0 (compatible; DistributedCrawler/1.0)";
        public int RequestTimeoutSeconds { get; set; } = 30;
        public int MaxConcurrentRequests { get; set; } = 10;
        public int DelayBetweenRequestsMs { get; set; } = 1000;
        public bool EnableProgressSimulation { get; set; } = false;

        /// <summary>
        /// Worker节点最大并发执行任务数
        /// </summary>
        public int MaxConcurrentTasks { get; set; } = 5;
    }
}