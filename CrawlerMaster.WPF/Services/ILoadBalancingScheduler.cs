using CrawlerCore.Entities;
using System.Collections.Generic;

namespace CrawlerMaster.WPF.Services
{
    /// <summary>
    /// 负载均衡的候选节点
    /// </summary>
    /// <param name="Node">节点信息（来自数据库，资源数据来自最近一次心跳）</param>
    /// <param name="AssignedTasks">已分配给该节点、尚未结束（Pending/Running）的任务数</param>
    /// <param name="MaxConcurrentTasks">节点注册时声明的并发任务上限，0 表示不限</param>
    public sealed record NodeCandidate(NodeInfo Node, int AssignedTasks = 0, int MaxConcurrentTasks = 0)
    {
        /// <summary>任务数已达上限，不再分配</summary>
        public bool IsFull => MaxConcurrentTasks > 0 && AssignedTasks >= MaxConcurrentTasks;
    }

    public interface ILoadBalancingScheduler
    {
        /// <summary>
        /// 只按资源选择节点（不考虑已分配任务数）
        /// </summary>
        /// <returns>最优节点；没有在线节点时返回 null</returns>
        NodeInfo? SelectOptimalNode(IEnumerable<NodeInfo> availableNodes, CrawlerTask task);

        /// <summary>
        /// 选择最优节点：排除任务数已满的节点，优先满足资源阈值的节点，再按已分配任务数从少到多、资源得分从高到低选择
        /// </summary>
        /// <returns>最优节点；没有可用节点（无在线节点，或都已满）时返回 null</returns>
        NodeInfo? SelectOptimalNode(IReadOnlyCollection<NodeCandidate> candidates, CrawlerTask task);
    }
}
