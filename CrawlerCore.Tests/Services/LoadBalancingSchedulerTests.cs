using CrawlerCore.Entities;
using CrawlerMaster.WPF.Services;
using Xunit;

namespace CrawlerCore.Tests.Services
{
    /// <summary>
    /// 负载均衡调度器单元测试
    /// 覆盖：过滤规则、评分算法、边界条件
    /// </summary>
    public class LoadBalancingSchedulerTests
    {
        private readonly LoadBalancingScheduler _scheduler = new();

        #region 测试数据构造
        // 合格节点：在线 + CPU30% + 内存50%
        private readonly NodeInfo _goodNode = new()
        {
            NodeId = "node-001",
            Status = NodeStatus.Online,
            CpuUsage = 30,
            TotalMemoryGb = 8,
            MemoryUsageMb = 4096 // 50%使用率
        };

        // CPU超限节点：CPU60%
        private readonly NodeInfo _cpuOverNode = new()
        {
            NodeId = "node-002",
            Status = NodeStatus.Online,
            CpuUsage = 60,
            TotalMemoryGb = 8,
            MemoryUsageMb = 2048
        };

        // 内存超限节点：内存70%
        private readonly NodeInfo _memoryOverNode = new()
        {
            NodeId = "node-003",
            Status = NodeStatus.Online,
            CpuUsage = 30,
            TotalMemoryGb = 8,
            MemoryUsageMb = 5734 // 70%使用率
        };

        // 离线节点
        private readonly NodeInfo _offlineNode = new()
        {
            NodeId = "node-004",
            Status = NodeStatus.Offline,
            CpuUsage = 20,
            TotalMemoryGb = 8,
            MemoryUsageMb = 2048
        };

        // 总内存异常节点
        private readonly NodeInfo _badMemoryNode = new()
        {
            NodeId = "node-005",
            Status = NodeStatus.Online,
            CpuUsage = 30,
            TotalMemoryGb = 0,
            MemoryUsageMb = 0
        };
        #endregion

        [Fact(DisplayName = "过滤规则：仅保留合格节点，超限/离线节点被过滤")]
        public void SelectOptimalNode_FilterRule_OnlyQualified()
        {
            // Arrange
            var nodes = new[] { _goodNode, _cpuOverNode, _memoryOverNode, _offlineNode, _badMemoryNode };
            var task = new CrawlerTask();

            // Act
            var result = _scheduler.SelectOptimalNode(nodes, task);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(_goodNode.NodeId, result.NodeId);
        }

        [Fact(DisplayName = "评分算法：资源更空闲的节点得分更高，优先被选中")]
        public void SelectOptimalNode_ScoreAlgorithm_BestNodeSelected()
        {
            // Arrange
            var lowLoadNode = new NodeInfo
            {
                NodeId = "node-best",
                Status = NodeStatus.Online,
                CpuUsage = 10,
                TotalMemoryGb = 16,
                MemoryUsageMb = 2048
            };
            var nodes = new[] { _goodNode, lowLoadNode };
            var task = new CrawlerTask();

            // Act
            var result = _scheduler.SelectOptimalNode(nodes, task);

            // Assert
            Assert.Equal(lowLoadNode.NodeId, result.NodeId);
        }

        [Fact(DisplayName = "边界条件：无合格节点时返回null")]
        public void SelectOptimalNode_NoQualifiedNode_ReturnNull()
        {
            // Arrange
            var nodes = new[] { _cpuOverNode, _memoryOverNode, _offlineNode };
            var task = new CrawlerTask();

            // Act
            var result = _scheduler.SelectOptimalNode(nodes, task);

            // Assert
            Assert.Null(result);
        }

        [Fact(DisplayName = "边界条件：空节点集合返回null")]
        public void SelectOptimalNode_EmptyNodes_ReturnNull()
        {
            // Act
            var result = _scheduler.SelectOptimalNode(Array.Empty<NodeInfo>(), new CrawlerTask());

            // Assert
            Assert.Null(result);
        }
    }
}