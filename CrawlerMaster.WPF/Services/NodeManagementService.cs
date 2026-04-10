using CrawlerCore.Entities;
using CrawlerCore.Interfaces;
using CrawlerMaster.WPF.Data;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CrawlerMaster.WPF.Services
{
    /// <summary>
    /// 节点管理数据服务实现
    /// </summary>
    public class NodeManagementService : INodeManagementService
    {
        private readonly CrawlerDbContext _dbContext;

        public NodeManagementService(CrawlerDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        /// <summary>
        /// 节点注册
        /// </summary>
        public async Task<NodeInfo> RegisterNodeAsync(NodeInfo nodeInfo)
        {
            var existNode = await _dbContext.NodeInfos
                .FirstOrDefaultAsync(n => n.NodeId == nodeInfo.NodeId);

            if (existNode != null)
            {
                existNode.Status = NodeStatus.Online;
                existNode.LastHeartbeat = DateTime.UtcNow;
                _dbContext.NodeInfos.Update(existNode);
            }
            else
            {
                await _dbContext.NodeInfos.AddAsync(nodeInfo);
            }

            await _dbContext.SaveChangesAsync();
            return nodeInfo;
        }

        /// <summary>
        /// 获取所有在线节点
        /// </summary>
        public async Task<List<NodeInfo>> GetActiveNodesAsync()
        {
            return await _dbContext.NodeInfos
                .Where(n => n.Status == NodeStatus.Online)
                .ToListAsync();
        }

        /// <summary>
        /// 更新节点心跳
        /// </summary>
        public async Task<bool> UpdateNodeHeartbeatAsync(string nodeId)
        {
            var node = await _dbContext.NodeInfos
                .FirstOrDefaultAsync(n => n.NodeId == nodeId);

            if (node == null) return false;

            node.LastHeartbeat = DateTime.UtcNow;
            return await _dbContext.SaveChangesAsync() > 0;
        }

        /// <summary>
        /// 移除超时离线节点
        /// </summary>
        public async Task<int> RemoveInactiveNodesAsync(TimeSpan timeout)
        {
            var cutoffTime = DateTime.UtcNow - timeout;
            var inactiveNodes = await _dbContext.NodeInfos
                .Where(n => n.LastHeartbeat < cutoffTime)
                .ToListAsync();

            _dbContext.NodeInfos.RemoveRange(inactiveNodes);
            return await _dbContext.SaveChangesAsync();
        }

        /// <summary>
        /// 将超时无心跳的节点标记为离线
        /// </summary>
        public async Task<int> MarkInactiveNodesAsOfflineAsync(TimeSpan timeout)
        {
            var cutoffTime = DateTime.UtcNow - timeout;
            var inactiveNodes = await _dbContext.NodeInfos
                .Where(n => n.Status == NodeStatus.Online && n.LastHeartbeat < cutoffTime)
                .ToListAsync();

            if (inactiveNodes.Count == 0)
                return 0;

            foreach (var node in inactiveNodes)
            {
                node.Status = NodeStatus.Offline;
            }

            return await _dbContext.SaveChangesAsync();
        }
    }
}