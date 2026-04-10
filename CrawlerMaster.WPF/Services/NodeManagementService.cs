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
    public class NodeManagementService : INodeManagementService
    {
        private readonly CrawlerDbContext _dbContext;
        private readonly IEventAggregator _eventAggregator;
        // 全局并发锁（SQLite 单文件数据库，必须串行写操作）
        private static readonly SemaphoreSlim _writeLock = new(1, 1);

        public NodeManagementService(CrawlerDbContext dbContext, IEventAggregator eventAggregator)
        {
            _dbContext = dbContext;
            _eventAggregator = eventAggregator;
        }

        /// <summary>
        /// 修复：正确的并发注册，无EF跟踪错误
        /// </summary>
        public async Task<NodeInfo> RegisterNodeAsync(NodeInfo nodeInfo)
        {
            await _writeLock.WaitAsync();
            try
            {
                // 1. 查询跟踪实体（修复AsNoTracking导致的更新失败）
                var existNode = await _dbContext.NodeInfos
                    .FirstOrDefaultAsync(n => n.NodeId == nodeInfo.NodeId);

                NodeStatus oldStatus;
                NodeInfo resultNode;

                if (existNode != null)
                {
                    // 2. 直接更新跟踪的实体（正确写法）
                    oldStatus = existNode.Status;
                    existNode.Status = NodeStatus.Online;
                    existNode.LastHeartbeat = DateTime.UtcNow;
                    // 补充同步客户端上报的信息
                    existNode.MachineName = nodeInfo.MachineName;
                    existNode.IpAddress = nodeInfo.IpAddress;
                    resultNode = existNode;
                }
                else
                {
                    // 3. 新增实体
                    oldStatus = NodeStatus.Pending;
                    nodeInfo.Status = NodeStatus.Online;
                    nodeInfo.LastHeartbeat = DateTime.UtcNow;
                    await _dbContext.NodeInfos.AddAsync(nodeInfo);
                    resultNode = nodeInfo;
                }

                // 4. 保存（SQLite无需显式事务，单写操作原子性）
                await _dbContext.SaveChangesAsync();

                // 发布状态变更事件
                if (oldStatus != NodeStatus.Online)
                {
                    _eventAggregator.Publish(this,
                        new NodeStatusChangedEventArgs(resultNode.NodeId, oldStatus, NodeStatus.Online, resultNode));
                }

                return resultNode;
            }
            finally
            {
                _writeLock.Release();
            }
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
                return await _dbContext.SaveChangesAsync() > 0;
            }
            finally
            {
                _writeLock.Release();
            }
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

        public async Task<int> MarkInactiveNodesAsOfflineAsync(TimeSpan timeout)
        {
            await _writeLock.WaitAsync();
            try
            {
                var cutoffTime = DateTime.UtcNow - timeout;
                var inactiveNodes = await _dbContext.NodeInfos
                    .Where(n => n.Status == NodeStatus.Online && n.LastHeartbeat < cutoffTime)
                    .ToListAsync();

                if (inactiveNodes.Count == 0) return 0;

                var nodeStatusPairs = inactiveNodes.Select(n => new { n, OldStatus = n.Status }).ToList();
                foreach (var pair in nodeStatusPairs)
                {
                    pair.n.Status = NodeStatus.Offline;
                }

                await _dbContext.SaveChangesAsync();

                foreach (var pair in nodeStatusPairs)
                {
                    _eventAggregator.Publish(this,
                        new NodeStatusChangedEventArgs(pair.n.NodeId, pair.OldStatus, NodeStatus.Offline, pair.n));
                }

                return inactiveNodes.Count;
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>
        /// 修复：单个节点标记离线（供断流调用，线程安全）
        /// </summary>
        public async Task MarkSingleNodeAsOfflineAsync(string nodeId)
        {
            await _writeLock.WaitAsync();
            try
            {
                var node = await _dbContext.NodeInfos.FirstOrDefaultAsync(n => n.NodeId == nodeId);
                if (node == null || node.Status == NodeStatus.Offline) return;

                var oldStatus = node.Status;
                node.Status = NodeStatus.Offline;
                await _dbContext.SaveChangesAsync();

                _eventAggregator.Publish(this,
                    new NodeStatusChangedEventArgs(nodeId, oldStatus, NodeStatus.Offline, node));
            }
            finally
            {
                _writeLock.Release();
            }
        }
    }
}