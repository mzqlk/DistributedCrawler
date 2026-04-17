namespace CrawlerCore.Infrastructure
{
    /// <summary>
    /// 数据库配置项
    /// </summary>
    public class DatabaseSettings
    {
        public const string SectionName = "Database";

        public string ConnectionString { get; set; } = "Data Source=crawler.db";
        public bool AutoMigrate { get; set; } = true;
    }
}