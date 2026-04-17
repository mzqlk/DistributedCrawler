using CrawlerCore.Infrastructure;
using System;
using System.Collections.Generic;

namespace CrawlerCore.Entities
{
    /// <summary>
    /// 爬虫任务实体
    /// </summary>
    public class CrawlerTask
    {
        /// <summary>
        /// 任务唯一标识（GUID生成）
        /// </summary>
        public string TaskId { get; set; } = string.Empty;

        /// <summary>
        /// 任务名称（自定义，如“新闻网站采集”）
        /// </summary>
        public string TaskName { get; set; } = string.Empty;

        /// <summary>
        /// 任务起始URL列表（爬取入口）
        /// </summary>
        public List<string> StartUrls { get; set; } = null!; // 标注非空，构造函数初始化

        /// <summary>
        /// 任务使用的爬虫插件名称（与ICrawlerPlugin.Name一致）
        /// </summary>
        public string PluginName { get; set; } = string.Empty;

        /// <summary>
        /// 任务配置参数（键值对，如选择器、请求延迟）
        /// </summary>
        public Dictionary<string, string> Configuration { get; set; } = null!; // 标注非空

        /// <summary>
        /// 最大爬取深度（1=仅爬取起始URL，2=起始URL+子链接，以此类推）
        /// </summary>
        public int MaxDepth { get; set; } = 1;

        /// <summary>
        /// 最大爬取页面数（防止无限爬取，0=不限制）
        /// </summary>
        public int MaxPages { get; set; } = 0;

        /// <summary>
        /// 任务当前状态
        /// </summary>
        public TaskStatus Status { get; set; } = TaskStatus.Pending;

        /// <summary>
        /// 任务分配到的节点ID（未分配则为空）
        /// </summary>
        public string AssignedNode { get; set; } = string.Empty;

        /// <summary>
        /// 任务创建时间（UTC时间）
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// 任务优先级（1=最低，5=最高，默认1）
        /// </summary>
        public int Priority { get; set; } = 1;

        /// <summary>
        /// 构造函数：初始化集合属性+动态默认值
        /// </summary>
        public CrawlerTask()
        {
            TaskId = Guid.NewGuid().ToString();
            CreatedAt = DateTime.UtcNow;
            StartUrls = new List<string>();
            Configuration = new Dictionary<string, string>();
            Status = TaskStatus.Pending;
            AssignedNode = string.Empty;
            PluginName = string.Empty;
            Priority = 1;

            LogHelper.Info($"创建爬虫任务: TaskId={TaskId}, CreatedAt={CreatedAt:yyyy-MM-dd HH:mm:ss}");
        }
    }
}