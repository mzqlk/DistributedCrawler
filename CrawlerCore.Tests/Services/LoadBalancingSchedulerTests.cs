using CrawlerCore.Entities;
using CrawlerCore.Infrastructure;
using CrawlerMaster.WPF.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace CrawlerCore.Tests.Services
{
    /// <summary>
    /// 负载均衡调度器单元测试
    /// 覆盖：资源阈值、阈值不满足时的退路、按已分配任务数均衡、并发上限、评分算法、边界条件
    /// </summary>
    public class LoadBalancingSchedulerTests
    {
        // 与原实现相同的严格阈值，用于验证“满足阈值的节点优先”
        private readonly LoadBalancingScheduler _strictScheduler = new(Options.Create(new LoadBalancingSettings
        {
            MaxCpuPercent = 50,
            MaxMemoryPercent = 60
        }));

        // 默认阈值（90/90）
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

        // 总内存未知节点
        private readonly NodeInfo _unknownMemoryNode = new()
        {
            NodeId = "node-005",
            Status = NodeStatus.Online,
            CpuUsage = 30,
            TotalMemoryGb = 0,
            MemoryUsageMb = 0
        };
        #endregion

        [Fact(DisplayName = "资源阈值：满足阈值的节点优先，超限/离线节点不被选中")]
        public void SelectOptimalNode_FilterRule_OnlyQualified()
        {
            var nodes = new[] { _goodNode, _cpuOverNode, _memoryOverNode, _offlineNode };

            var result = _strictScheduler.SelectOptimalNode(nodes, new CrawlerTask());

            Assert.NotNull(result);
            Assert.Equal(_goodNode.NodeId, result.NodeId);
        }

        [Fact(DisplayName = "评分算法：资源更空闲的节点得分更高，优先被选中")]
        public void SelectOptimalNode_ScoreAlgorithm_BestNodeSelected()
        {
            var lowLoadNode = new NodeInfo
            {
                NodeId = "node-best",
                Status = NodeStatus.Online,
                CpuUsage = 10,
                TotalMemoryGb = 16,
                MemoryUsageMb = 2048
            };

            var result = _scheduler.SelectOptimalNode(new[] { _goodNode, lowLoadNode }, new CrawlerTask());

            Assert.Equal(lowLoadNode.NodeId, result!.NodeId);
        }

        [Fact(DisplayName = "没有节点满足资源阈值：退而选择负载最低的在线节点，不让任务无限期等待（M-07）")]
        public void SelectOptimalNode_NoNodeWithinThreshold_FallsBackToLowestLoad()
        {
            var nodes = new[] { _cpuOverNode, _memoryOverNode, _offlineNode };

            var result = _strictScheduler.SelectOptimalNode(nodes, new CrawlerTask());

            Assert.NotNull(result);
            // CPU60%/内存25% 的得分高于 CPU30%/内存70%
            Assert.Equal(_cpuOverNode.NodeId, result.NodeId);
        }

        [Fact(DisplayName = "默认阈值 90%：内存占用 70% 的 Windows 开发机仍能被选中（M-07）")]
        public void SelectOptimalNode_DefaultThreshold_AcceptsTypicalDevMachine()
        {
            var result = _scheduler.SelectOptimalNode(new[] { _memoryOverNode }, new CrawlerTask());

            Assert.Equal(_memoryOverNode.NodeId, result!.NodeId);
        }

        [Fact(DisplayName = "阈值可配置：从 LoadBalancingSettings 读取（M-07）")]
        public void SelectOptimalNode_ThresholdComesFromSettings()
        {
            // 阈值 80%：内存 70% 的节点满足，CPU 85% 的节点不满足
            var scheduler = new LoadBalancingScheduler(Options.Create(new LoadBalancingSettings { MaxCpuPercent = 80, MaxMemoryPercent = 80 }));
            var busyCpu = new NodeInfo { NodeId = "busy", Status = NodeStatus.Online, CpuUsage = 85, TotalMemoryGb = 8, MemoryUsageMb = 0 };

            var result = scheduler.SelectOptimalNode(new[] { busyCpu, _memoryOverNode }, new CrawlerTask());

            Assert.Equal(_memoryOverNode.NodeId, result!.NodeId);
        }

        [Fact(DisplayName = "总内存未知的节点按 0% 处理，不被过滤")]
        public void SelectOptimalNode_UnknownTotalMemory_TreatedAsZeroPercent()
        {
            var result = _strictScheduler.SelectOptimalNode(new[] { _unknownMemoryNode }, new CrawlerTask());

            Assert.Equal(_unknownMemoryNode.NodeId, result!.NodeId);
        }

        [Fact(DisplayName = "已分配任务数少的节点优先，即使资源得分更低（M-06）")]
        public void SelectOptimalNode_FewerAssignedTasksWins()
        {
            var idle = new NodeInfo { NodeId = "idle", Status = NodeStatus.Online, CpuUsage = 5, TotalMemoryGb = 16, MemoryUsageMb = 1024 };
            var candidates = new[]
            {
                new NodeCandidate(idle, AssignedTasks: 2),
                new NodeCandidate(_goodNode, AssignedTasks: 1)
            };

            var result = _scheduler.SelectOptimalNode(candidates, new CrawlerTask());

            Assert.Equal(_goodNode.NodeId, result!.NodeId);
        }

        [Fact(DisplayName = "连续分配：按已分配任务数轮流落到各节点（M-06）")]
        public void SelectOptimalNode_SequentialAssignments_SpreadAcrossNodes()
        {
            var nodeA = new NodeInfo { NodeId = "a", Status = NodeStatus.Online, CpuUsage = 1, TotalMemoryGb = 8, MemoryUsageMb = 100 };
            var nodeB = new NodeInfo { NodeId = "b", Status = NodeStatus.Online, CpuUsage = 1, TotalMemoryGb = 8, MemoryUsageMb = 100 };
            var counts = new Dictionary<string, int> { ["a"] = 0, ["b"] = 0 };

            for (var i = 0; i < 4; i++)
            {
                var candidates = new[] { new NodeCandidate(nodeA, counts["a"]), new NodeCandidate(nodeB, counts["b"]) };
                var selected = _scheduler.SelectOptimalNode(candidates, new CrawlerTask())!;
                counts[selected.NodeId]++;
            }

            Assert.Equal(2, counts["a"]);
            Assert.Equal(2, counts["b"]);
        }

        [Fact(DisplayName = "任务数已达并发上限的节点不再分配（M-06）")]
        public void SelectOptimalNode_FullNodeSkipped()
        {
            var candidates = new[]
            {
                new NodeCandidate(_goodNode, AssignedTasks: 1, MaxConcurrentTasks: 1),
                new NodeCandidate(_memoryOverNode, AssignedTasks: 3, MaxConcurrentTasks: 0)
            };

            var result = _scheduler.SelectOptimalNode(candidates, new CrawlerTask());

            Assert.Equal(_memoryOverNode.NodeId, result!.NodeId);
        }

        [Fact(DisplayName = "所有节点都已满：返回 null，任务留在队列中（M-06）")]
        public void SelectOptimalNode_AllNodesFull_ReturnNull()
        {
            var candidates = new[] { new NodeCandidate(_goodNode, AssignedTasks: 2, MaxConcurrentTasks: 2) };

            Assert.Null(_scheduler.SelectOptimalNode(candidates, new CrawlerTask()));
        }

        [Fact(DisplayName = "边界条件：只有离线节点时返回null")]
        public void SelectOptimalNode_OnlyOfflineNodes_ReturnNull()
        {
            Assert.Null(_scheduler.SelectOptimalNode(new[] { _offlineNode }, new CrawlerTask()));
        }

        [Fact(DisplayName = "边界条件：空节点集合返回null")]
        public void SelectOptimalNode_EmptyNodes_ReturnNull()
        {
            var result = _scheduler.SelectOptimalNode(Array.Empty<NodeInfo>(), new CrawlerTask());

            Assert.Null(result);
        }
    }
}
