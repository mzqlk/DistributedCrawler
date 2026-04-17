using CrawlerCore.Entities;
using System;
using TaskStatus = CrawlerCore.Entities.TaskStatus;

namespace CrawlerCore.Events
{
    /// <summary>
    /// 任务进度更新事件参数
    /// </summary>
    public class TaskProgressUpdatedEventArgs : EventArgs
    {
        /// <summary>
        /// 任务唯一标识
        /// </summary>
        public string TaskId { get; }

        /// <summary>
        /// 任务名称（兼容空值）
        /// </summary>
        public string TaskName { get; }

        /// <summary>
        /// 任务当前状态
        /// </summary>
        public TaskStatus CurrentStatus { get; }

        /// <summary>
        /// 已爬取页面数
        /// </summary>
        public int CrawledPages { get; }

        /// <summary>
        /// 总页面数
        /// </summary>
        public int TotalPages { get; }

        /// <summary>
        /// 任务分配的节点ID
        /// </summary>
        public string AssignedNodeId { get; }

        /// <summary>
        /// 事件触发时间（UTC，避免时区问题）
        /// </summary>
        public DateTime ChangedTime { get; } = DateTime.UtcNow;

        public TaskProgressUpdatedEventArgs(
            string taskId,
            string taskName,
            TaskStatus currentStatus,
            int crawledPages,
            int totalPages,
            string assignedNodeId)
        {
            TaskId = string.IsNullOrWhiteSpace(taskId) ? string.Empty : taskId;
            TaskName = string.IsNullOrWhiteSpace(taskName) ? string.Empty : taskName;
            CurrentStatus = currentStatus;
            CrawledPages = Math.Max(0, crawledPages);
            TotalPages = Math.Max(0, totalPages);
            AssignedNodeId = string.IsNullOrWhiteSpace(assignedNodeId) ? string.Empty : assignedNodeId;
        }
    }
}