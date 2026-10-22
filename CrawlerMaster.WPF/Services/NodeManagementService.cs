using CrawlerCore.Entities;
using CrawlerCore.Events;
using CrawlerCore.Interfaces;
using CrawlerMaster.WPF.Data;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CrawlerMaster.WPF.Services
{
    /// <summary>
    /// 节点管理服务，负责节点的注册、心跳更新和状态管理。
    /// 节点状态事件只在这里、且只在库中状态保存之后发布；发布时不持有写锁，订阅者不会在锁内执行
    /// </summary>
    public class NodeManagementService : INodeManagementService
    {
        private readonly CrawlerDbContext _dbContext;
        private readonly IEventAggregator _eventAggregator;
        // 全局并发锁
        private static readonly SemaphoreSlim _writeLock = new(1, 1);

        public NodeManagementService(CrawlerDbContext dbContext, IEventAggregator eventAggregator)
        {
            _dbContext = dbContext;
            _eventAggregator = eventAggregator;
        }

        /// <summary>
        /// 注册节点（已存在时更新机器名、IP 与硬件信息，并置为 Online）
        /// </summary>
        public async Task<NodeInfo> RegisterNodeAsync(NodeInfo nodeInfo)
        {
            NodeStatusChangedEventArgs? statusChanged = null;
            NodeInfo resultNode;

            await _writeLock.WaitAsync();
            try
            {
                var existNode = await _dbContext.NodeInfos
                    .FirstOrDefaultAsync(n => n.NodeId == nodeInfo.NodeId);

                NodeStatus oldStatus;
                if (existNode != null)
                {
                    oldStatus = existNode.Status;
                    existNode.Status = NodeStatus.Online;
                    existNode.LastHeartbeat = DateTime.UtcNow;
                    existNode.MachineName = nodeInfo.MachineName;
                    existNode.IpAddress = nodeInfo.IpAddress;
                    // 硬件可能变化（例如容器调整了内存限制），负载均衡按最新值计算
                    existNode.CpuCores = nodeInfo.CpuCores;
                    existNode.TotalMemoryGb = nodeInfo.TotalMemoryGb;
                    resultNode = existNode;
                }
                else
                {
                    oldStatus = NodeStatus.Pending;
                    nodeInfo.Status = NodeStatus.Online;
                    nodeInfo.LastHeartbeat = DateTime.UtcNow;
                    await _dbContext.NodeInfos.AddAsync(nodeInfo);
                    resultNode = nodeInfo;
                }

                await _dbContext.SaveChangesAsync();

                if (oldStatus != NodeStatus.Online)
                {
                    statusChanged = new NodeStatusChangedEventArgs(resultNode.NodeId, oldStatus, NodeStatus.Online, resultNode);
                }
            }
            finally
            {
                _writeLock.Release();
            }

            if (statusChanged != null)
            {
                _eventAggregator.Publish(this, statusChanged);
            }
            return resultNode;
        }

        public async Task<List<NodeInfo>> GetActiveNodesAsync()
        {
            // 查询不加锁，提升性能
            return await _dbContext.NodeInfos
                .AsNoTracking()
                .Where(n => n.Status == NodeStatus.Online)
                .ToListAsync();
        }

        public async Task<bool> UpdateNodeHeartbeatAsync(NodeInfo nodeInfo)
        {
            NodeStatusChangedEventArgs statusEvent;
            bool saved;

            await _writeLock.WaitAsync();
            try
            {
                var node = await _dbContext.NodeInfos.FirstOrDefaultAsync(n => n.NodeId == nodeInfo.NodeId);
                if (node == null) return false;

                node.CpuUsage = nodeInfo.CpuUsage;
                node.MemoryUsageMb = nodeInfo.MemoryUsageMb;
                node.DiskUsagePercent = nodeInfo.DiskUsagePercent;
                node.ActiveTasks = nodeInfo.ActiveTasks;
                node.LastHeartbeat = nodeInfo.LastHeartbeat;

                // 心跳只经由双向流到达：节点当前是 Offline（例如健康检查误判后又恢复），收到心跳即恢复为 Online
                var oldStatus = node.Status;
                if (oldStatus == NodeStatus.Offline)
                {
                    node.Status = NodeStatus.Online;
                }

                saved = await _dbContext.SaveChangesAsync() > 0;
                statusEvent = new NodeStatusChangedEventArgs(node.NodeId, oldStatus, node.Status, node);
            }
            finally
            {
                _writeLock.Release();
            }

            // 保存之后再发布（界面据此刷新资源曲线；Offline → Online 时也是状态变化通知）
            _eventAggregator.Publish(this, statusEvent);
            return saved;
        }

        public async Task<int> RemoveInactiveNodesAsync(TimeSpan timeout)
        {
            await _writeLock.WaitAsync();
            try
            {
                var cutoffTime = DateTime.UtcNow - timeout;
                var inactiveNodes = await _dbContext.NodeInfos
                    .Where(n => n.LastHeartbeat < cutoffTime)
                    .ToListAsync();

                _dbContext.NodeInfos.RemoveRange(inactiveNodes);
                return await _dbContext.SaveChangesAsync();
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public async Task<IReadOnlyList<string>> MarkInactiveNodesAsOfflineAsync(TimeSpan timeout)
        {
            var events = new List<NodeStatusChangedEventArgs>();

            await _writeLock.WaitAsync();
            try
            {
                var cutoffTime = DateTime.UtcNow - timeout;
                var inactiveNodes = await _dbContext.NodeInfos
                    .Where(n => n.Status == NodeStatus.Online && n.LastHeartbeat < cutoffTime)
                    .ToListAsync();

                if (inactiveNodes.Count == 0) return Array.Empty<string>();

                foreach (var node in inactiveNodes)
                {
                    events.Add(new NodeStatusChangedEventArgs(node.NodeId, node.Status, NodeStatus.Offline, node));
                    node.Status = NodeStatus.Offline;
                }

                await _dbContext.SaveChangesAsync();
            }
            finally
            {
                _writeLock.Release();
            }

            foreach (var statusEvent in events)
            {
                _eventAggregator.Publish(this, statusEvent);
            }
            return events.Select(e => e.NodeId).ToList();
        }

        /// <summary>
        /// 标记单个节点为离线（已是离线时不重复发布事件）
        /// </summary>
        public async Task MarkSingleNodeAsOfflineAsync(string nodeId)
        {
            NodeStatusChangedEventArgs? statusChanged = null;

            await _writeLock.WaitAsync();
            try
            {
                var node = await _dbContext.NodeInfos.FirstOrDefaultAsync(n => n.NodeId == nodeId);
                if (node == null || node.Status == NodeStatus.Offline) return;

                var oldStatus = node.Status;
                node.Status = NodeStatus.Offline;
                await _dbContext.SaveChangesAsync();
                statusChanged = new NodeStatusChangedEventArgs(nodeId, oldStatus, NodeStatus.Offline, node);
            }
            finally
            {
                _writeLock.Release();
            }

            if (statusChanged != null)
            {
                _eventAggregator.Publish(this, statusChanged);
            }
        }
    }
}
