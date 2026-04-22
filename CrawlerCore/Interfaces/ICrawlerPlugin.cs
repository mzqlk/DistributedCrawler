using CrawlerCore.Entities;
using System.Threading.Tasks;

namespace CrawlerCore.Interfaces
{
    /// <summary>
    /// 爬虫插件核心接口
    /// </summary>
    public interface ICrawlerPlugin
    {
        /// <summary>
        /// 插件名称（全局唯一，与CrawlerTask.PluginName一致）
        /// </summary>
        string Name { get; }

        /// <summary>
        /// 插件版本
        /// </summary>
        string Version { get; }

        /// <summary>
        /// 执行插件解析逻辑
        /// </summary>
        /// <param name="context">插件执行上下文</param>
        /// <returns>插件执行结果</returns>
        Task<PluginResult> ExecuteAsync(PluginContext context);

        /// <summary>
        /// 获取插件默认配置参数
        /// </summary>
        /// <returns>配置参数键值对</returns>
        Dictionary<string, string> GetDefaultConfiguration();

        /// <summary>
        /// 判断在深度爬取过程中，是否应该跟随并爬取给定的链接。
        /// 插件可在此实现自己的作用域限制规则（如：只允许匹配特定路径模式的URL）。
        /// 返回 true 表示允许引擎将此链接加入爬取队列，false 表示拒绝。
        /// 默认实现：允许所有链接（保持向后兼容）。
        /// </summary>
        /// <param name="url">待判断的绝对URL</param>
        /// <param name="currentDepth">当前页面的深度</param>
        /// <returns>true 允许入队；false 拒绝入队</returns>
        bool ShouldFollowLink(string url, int currentDepth)
        {
            return true; // 默认允许所有
        }
    }
}