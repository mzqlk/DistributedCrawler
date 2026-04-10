using CrawlerCore.Entities;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CrawlerCore.Interfaces
{
    /// <summary>
    /// 节点管理服务接口（Master端）
    /// </summary>
    public interface INodeManagementService
    {
        /// <summary>
        /// 节点注册
        /// </summary>
        /// <param name="nodeInfo">待注册的节点信息</param>
        /// <returns>注册后的节点信息（含NodeId）</returns>
        Task<NodeInfo> RegisterNodeAsync(NodeInfo nodeInfo);

        /// <summary>
        /// 获取所有活跃节点（状态为Online）
        /// </summary>
        /// <returns>活跃节点列表</returns>
        Task<List<NodeInfo>> GetActiveNodesAsync();

        /// <summary>
        /// 更新节点最后心跳时间
        /// </summary>
        /// <param name="nodeId">节点ID</param>
        /// <returns>是否更新成功</returns>
        Task<bool> UpdateNodeHeartbeatAsync(string nodeId);

        /// <summary>
        /// 移除离线节点（超过指定超时时间无心跳）
        /// </summary>
        /// <param name="timeout">超时时间</param>
        /// <returns>被移除的节点数量</returns>
        Task<int> RemoveInactiveNodesAsync(TimeSpan timeout);

        /// <summary>
        /// 将超时无心跳的节点标记为离线状态
        /// </summary>
        /// <param name="timeout">心跳超时时间</param>
        /// <returns>被标记为离线的节点数量</returns>
        Task<int> MarkInactiveNodesAsOfflineAsync(TimeSpan timeout);
    }
}