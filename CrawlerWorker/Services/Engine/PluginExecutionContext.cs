using CrawlerCore.Entities;
using System.Collections.Generic;

namespace CrawlerWorker.Services.Engine
{
    /// <summary>
    /// 爬虫插件执行扩展上下文（Worker引擎专用，包含任务全量配置）
    /// 区别于原生PluginContext，补充爬取深度、任务配置、父链路信息
    /// </summary>
    public class PluginExecutionContext
    {
        /// <summary>
        /// 原生页面上下文（HTML/URL）
        /// </summary>
        public PluginContext BaseContext { get; set; } = null!;

        /// <summary>
        /// 当前爬取深度（1=起始页）
        /// </summary>
        public int CurrentDepth { get; set; }

        /// <summary>
        /// 父页面URL（起始页为空）
        /// </summary>
        public string ParentUrl { get; set; } = string.Empty;

        /// <summary>
        /// 爬虫任务全局配置（键值对）
        /// </summary>
        public Dictionary<string, string> TaskConfiguration { get; set; } = null!;

        /// <summary>
        /// 关联任务ID
        /// </summary>
        public string TaskId { get; set; } = string.Empty;

        /// <summary>
        /// 构造函数：初始化非空属性
        /// </summary>
        public PluginExecutionContext()
        {
            BaseContext = new PluginContext();
            TaskConfiguration = new Dictionary<string, string>();
        }
    }
}