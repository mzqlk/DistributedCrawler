using CrawlerCore.Infrastructure;
using System;
using System.Collections.Generic;

namespace CrawlerCore.Entities
{
    /// <summary>
    /// 爬虫任务整体爬取结果实体
    /// </summary>
    public class CrawlResult
    {
        public long Id { get; set; }

        /// <summary>
        /// 关联的爬虫任务ID（与CrawlerTask.TaskId一致）
        /// </summary>
        public string TaskId { get; set; } = string.Empty;

        /// <summary>
        /// 任务下所有单页的爬取结果
        /// </summary>
        public List<PageResult> PageResults { get; set; }

        /// <summary>
        /// 任务完成时间（未完成则为null）
        /// </summary>
        public DateTime? CompletedAt { get; set; }

        /// <summary>
        /// 构造函数：初始化集合属性
        /// </summary>
        public CrawlResult()
        {
            PageResults = new List<PageResult>();
            LogHelper.Debug("CrawlResult 实例创建");
        }
    }
}