using CrawlerCore.Infrastructure;
using System.Collections.Generic;

namespace CrawlerCore.Entities
{
    /// <summary>
    /// 爬虫插件执行结果实体
    /// </summary>
    public class PluginResult
    {
        /// <summary>
        /// 插件解析后的数据（键值对）
        /// </summary>
        public Dictionary<string, object> Data { get; set; } = new Dictionary<string, object>();

        /// <summary>
        /// 插件执行是否成功
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// 执行失败时的错误信息（成功则为空）
        /// </summary>
        public string ErrorMessage { get; set; } = string.Empty;

        public PluginResult()
        {
            Data = new Dictionary<string, object>();
            LogHelper.Debug("PluginResult 实例创建");
        }
    }
}