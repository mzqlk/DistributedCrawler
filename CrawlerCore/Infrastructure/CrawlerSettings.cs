namespace CrawlerCore.Infrastructure
{
    /// <summary>
    /// 爬虫全局配置项
    /// </summary>
    public class CrawlerSettings
    {
        public const string SectionName = "CrawlerSettings";

        /// <summary>
        /// 爬虫请求使用的 User-Agent。robots.txt 按其中的产品名（DistributedCrawler）匹配规则组，
        /// 并带上项目地址，方便网站管理员找到我们（W-07、W-30）
        /// </summary>
        public string DefaultUserAgent { get; set; } = "DistributedCrawler/1.0 (+https://github.com/mzqlk/DistributedCrawler)";

        /// <summary>单个请求（含读取正文）的超时，秒</summary>
        public int RequestTimeoutSeconds { get; set; } = 30;

        public int MaxConcurrentRequests { get; set; } = 10;

        /// <summary>
        /// 同一主机相邻两次请求的最小间隔（毫秒）。实际间隔取它、插件的 CrawlDelay 与 robots.txt 的 Crawl-delay 三者的最大值，
        /// 并且对本进程内所有任务共同生效（W-08）
        /// </summary>
        public int DelayBetweenRequestsMs { get; set; } = 1000;

        public bool EnableProgressSimulation { get; set; } = false;

        /// <summary>
        /// Worker节点最大并发执行任务数（注册时上报给 Master，Master 不会给节点分配超过这个数的任务）
        /// </summary>
        public int MaxConcurrentTasks { get; set; } = 5;

        /// <summary>robots.txt 的缓存时间，小时（W-07）</summary>
        public int RobotsCacheHours { get; set; } = 24;

        /// <summary>
        /// 单个页面遇到超时、429、5xx 或网络错误时的最大重试次数（W-20）。0 表示不重试
        /// </summary>
        public int MaxRequestRetries { get; set; } = 2;

        /// <summary>单个响应正文的最大字节数，超过的页面不解析（W-21）。默认 5 MB</summary>
        public long MaxResponseBytes { get; set; } = 5 * 1024 * 1024;

        /// <summary>只跟随与起始 URL 同主机的链接（W-21）</summary>
        public bool SameHostOnly { get; set; } = true;

        /// <summary>插件解析单个页面的超时，秒（W-18）</summary>
        public int PluginTimeoutSeconds { get; set; } = 30;

        /// <summary>插件目录；相对路径相对于程序所在目录（W-18）</summary>
        public string PluginDirectory { get; set; } = "Plugins";

        /// <summary>
        /// 是否忽略被爬网站的证书错误（W-15）。默认 false；只在确实需要抓取证书有问题的站点时打开，启动时会记警告
        /// </summary>
        public bool IgnoreCertificateErrors { get; set; } = false;
    }
}
