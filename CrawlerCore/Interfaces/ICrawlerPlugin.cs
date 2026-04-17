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
    }
}