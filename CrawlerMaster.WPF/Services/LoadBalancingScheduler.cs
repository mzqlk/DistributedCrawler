using CrawlerCore.Entities;
using CrawlerCore.Infrastructure;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CrawlerMaster.WPF.Services
{
    /// <summary>
    /// 资源感知负载均衡调度器
    /// 过滤规则：在线节点 + CPU<50% + 内存使用率<60%
    /// 评分算法：CPU(40%) + 内存(30%) + 网络(20) + 契合度(10)
    /// </summary>
    public class LoadBalancingScheduler
    {
        /// <summary>
        /// 选择最优执行节点
        /// </summary>
        /// <param name="availableNodes">所有可用节点集合</param>
        /// <param name="task">待分配爬虫任务</param>
        /// <returns>最优节点；无符合条件节点返回null</returns>
        public NodeInfo? SelectOptimalNode(IEnumerable<NodeInfo> availableNodes, CrawlerTask task)
        {
            if (availableNodes == null || !availableNodes.Any())
            {
                LogHelper.Warn("负载均衡：无传入节点，分配失败");
                return null;
            }

            // 1. 执行节点过滤规则
            var qualifiedNodes = availableNodes
                .Where(node =>
                    // 规则1：节点必须在线
                    node.Status == NodeStatus.Online &&
                    // 规则2：CPU使用率 < 50%
                    node.CpuUsage < 50 &&
                    // 规则3：内存使用率 < 60%（规避除零异常）
                    node.TotalMemoryGb > 0 &&
                    CalculateMemoryUsageRate(node) < 0.6)
                .ToList();

            if (!qualifiedNodes.Any())
            {
                LogHelper.Warn("负载均衡：无符合资源阈值的节点，分配失败");
                return null;
            }

            // 2. 节点评分计算 + 降序排序
            var scoredNodes = qualifiedNodes
                .Select(node => new
                {
                    Node = node,
                    Score = CalculateNodeScore(node)
                })
                .OrderByDescending(x => x.Score)
                .ToList();

            var bestNode = scoredNodes.First().Node;
            LogHelper.Info($"负载均衡：选中最优节点 NodeId={bestNode.NodeId}，得分={scoredNodes.First().Score:F2}");
            return bestNode;
        }

        /// <summary>
        /// 计算内存使用率（统一单位处理 + 边界值优化）
        /// </summary>
        private double CalculateMemoryUsageRate(NodeInfo node)
        {
            if (node.TotalMemoryGb <= 0)
            {
                LogHelper.Warn($"节点 NodeId={node.NodeId} 总内存配置异常（≤0GB），内存使用率按0%处理");
                return 0.0;
            }

            // TotalMemoryGb 是 GB 单位，MemoryUsageMb 是 MB 单位
            double totalMemoryMb = node.TotalMemoryGb * 1024;
            return node.MemoryUsageMb / totalMemoryMb;
        }

        /// <summary>
        /// 计算节点综合评分（核心算法）
        /// </summary>
        private double CalculateNodeScore(NodeInfo node)
        {
            // CPU得分：(100-使用率) * 40%
            double cpuScore = (100 - node.CpuUsage) * 0.4;

            // 内存使用率
            double memoryUsageRate = CalculateMemoryUsageRate(node);
            // 内存得分：(100-使用率百分比) * 30%
            double memoryScore = (100 - memoryUsageRate * 100) * 0.3;

            // 预留固定得分
            const double networkScore = 20;    // 网络得分
            const double fitScore = 10;         // 任务契合度得分

            // 总得分
            double totalScore = cpuScore + memoryScore + networkScore + fitScore;

            LogHelper.Debug($"节点评分 NodeId={node.NodeId}：CPU={cpuScore:F2} 内存={memoryScore:F2} 总分={totalScore:F2}");
            return totalScore;
        }
    }
}