using CrawlerCore.Infrastructure;
using System;

namespace CrawlerCore.Entities
{
    /// <summary>
    /// Worker工作节点信息实体
    /// </summary>
    public class NodeInfo
    {
        /// <summary>
        /// 节点唯一标识（全局唯一，如机器名+GUID）
        /// </summary>
        public string NodeId { get; set; } = string.Empty;

        /// <summary>
        /// 节点所在机器名
        /// </summary>
        public string MachineName { get; set; } = string.Empty;

        /// <summary>
        /// 节点IP地址
        /// </summary>
        public string IpAddress { get; set; } = string.Empty;

        /// <summary>
        /// 节点CPU核心数
        /// </summary>
        public int CpuCores { get; set; }

        /// <summary>
        /// 节点总内存（GB）
        /// </summary>
        public double TotalMemoryGb { get; set; }

        /// <summary>
        /// 节点当前状态
        /// </summary>
        public NodeStatus Status { get; set; } = NodeStatus.Pending;

        /// <summary>
        /// CPU使用率（%，0-100）
        /// </summary>
        public double CpuUsage { get; set; }

        /// <summary>
        /// 内存使用率（MB）
        /// </summary>
        public double MemoryUsageMb { get; set; }

        /// <summary>
        /// 磁盘使用率（%，0-100）
        /// </summary>
        public double DiskUsagePercent { get; set; }

        /// <summary>
        /// 节点当前运行的活跃任务数
        /// </summary>
        public int ActiveTasks { get; set; }

        /// <summary>
        /// 最后一次心跳时间（UTC时间，避免时区问题）
        /// </summary>
        public DateTime LastHeartbeat { get; set; }

        /// <summary>
        /// 构造函数：初始化默认值，确保实例化时动态赋值
        /// </summary>
        public NodeInfo()
        {
            NodeId = Guid.NewGuid().ToString();
            LastHeartbeat = DateTime.UtcNow;
            Status = NodeStatus.Pending;

            LogHelper.Info($"Worker节点信息创建: NodeId={NodeId}, LastHeartbeat={LastHeartbeat}");
        }
    }
}