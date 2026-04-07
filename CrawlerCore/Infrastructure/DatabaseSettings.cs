namespace CrawlerCore.Infrastructure
{
    /// <summary>
    /// 数据库配置项（100%对齐原项目appsettings.json规范）
    /// </summary>
    public class DatabaseSettings
    {
        public const string SectionName = "Database";

        public string ConnectionString { get; set; } = "Data Source=crawler.db";
        public bool AutoMigrate { get; set; } = true;
    }
}