using System;
using System.Collections.Generic;
using System.Text;

namespace CrawlerCore.Events
{
    /// <summary>
    /// 爬取结果更新事件参数类，用于在爬取结果发生变化时传递相关信息。
    /// </summary>
    public class CrawlResultReceivedEventArgs : EventArgs
    {
        /// <summary>
        /// 关联的爬虫任务ID（与CrawlerTask.TaskId一致）
        /// </summary>
        public string TaskId { get; }

        /// <summary>
        /// 爬取结果总页数
        /// </summary>
        public int TotalPages { get; }

        /// <summary>
        /// 任务完成时间（未完成则为null）
        /// </summary>
        public DateTime? CompletedAt { get; }

        public CrawlResultReceivedEventArgs(string taskId, int totalPages, DateTime? completedAt)
        {
            TaskId = taskId;
            TotalPages = totalPages;
            CompletedAt = completedAt;
        }
    }
}
