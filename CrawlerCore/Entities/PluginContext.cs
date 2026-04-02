using CrawlerCore.Infrastructure;

namespace CrawlerCore.Entities
{
    /// <summary>
    /// 爬虫插件执行上下文（传递插件所需的基础参数）
    /// </summary>
    public class PluginContext
    {
        /// <summary>
        /// 待解析的页面HTML内容
        /// </summary>
        public string Html { get; set; } = string.Empty;

        /// <summary>
        /// 页面的目标URL
        /// </summary>
        public string Url { get; set; } = string.Empty;

        public PluginContext()
        {
            LogHelper.Debug("PluginContext 实例创建");
        }
    }
}