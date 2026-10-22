using CrawlerCore.Entities;
using CrawlerCore.Infrastructure;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CrawlerMaster.WPF.Services
{
    /// <summary>
    /// 资源感知负载均衡调度器
    /// 1. 只考虑在线节点；任务数已达节点并发上限的不再分配；
    /// 2. 优先选择 CPU、内存使用率都低于阈值（<see cref="LoadBalancingSettings"/>，默认 90%）的节点；
    ///    没有节点满足阈值时退而在全部在线节点中选择，任务不会无限期等待；
    /// 3. 先比较已分配且尚未结束的任务数（少者优先，避免一次创建的多个任务都落到同一节点），
    ///    再比较资源得分：CPU(40%) + 内存(30%) + 网络(20) + 契合度(10)
    /// </summary>
    public class LoadBalancingScheduler : ILoadBalancingScheduler
    {
        private readonly LoadBalancingSettings _settings;

        public LoadBalancingScheduler(IOptions<LoadBalancingSettings>? options = null)
        {
            _settings = options?.Value ?? new LoadBalancingSettings();
        }

        public NodeInfo? SelectOptimalNode(IEnumerable<NodeInfo> availableNodes, CrawlerTask task)
        {
            var candidates = (availableNodes ?? Enumerable.Empty<NodeInfo>())
                .Select(node => new NodeCandidate(node))
                .ToList();
            return SelectOptimalNode(candidates, task);
        }

        public NodeInfo? SelectOptimalNode(IReadOnlyCollection<NodeCandidate> candidates, CrawlerTask task)
        {
            var online = (candidates ?? Array.Empty<NodeCandidate>())
                .Where(c => c.Node.Status == NodeStatus.Online)
                .ToList();
            if (online.Count == 0)
            {
                LogHelper.Warn("负载均衡：没有在线节点，分配失败");
                return null;
            }

            var notFull = online.Where(c => !c.IsFull).ToList();
            if (notFull.Count == 0)
            {
                LogHelper.Info("负载均衡：所有在线节点的任务数都已达到并发上限，任务留在队列中等待");
                return null;
            }

            var withinThreshold = notFull.Where(IsWithinResourceThreshold).ToList();
            var pool = withinThreshold;
            if (pool.Count == 0)
            {
                LogHelper.Warn($"负载均衡：没有节点满足资源阈值（CPU<{_settings.MaxCpuPercent}%、内存<{_settings.MaxMemoryPercent}%），退而选择负载最低的节点");
                pool = notFull;
            }

            var best = pool
                .Select(c => new { Candidate = c, Score = CalculateNodeScore(c.Node) })
                .OrderBy(x => x.Candidate.AssignedTasks)
                .ThenByDescending(x => x.Score)
                .First();

            LogHelper.Info($"负载均衡：选中节点 NodeId={best.Candidate.Node.NodeId}，已分配任务={best.Candidate.AssignedTasks}，得分={best.Score:F2}");
            return best.Candidate.Node;
        }

        private bool IsWithinResourceThreshold(NodeCandidate candidate)
            => candidate.Node.CpuUsage < _settings.MaxCpuPercent
               && CalculateMemoryUsageRate(candidate.Node) * 100 < _settings.MaxMemoryPercent;

        /// <summary>
        /// 计算内存使用率；总内存未知（≤0）时按 0% 处理
        /// </summary>
        private static double CalculateMemoryUsageRate(NodeInfo node)
        {
            if (node.TotalMemoryGb <= 0)
            {
                return 0.0;
            }

            // TotalMemoryGb 是 GB 单位，MemoryUsageMb 是 MB 单位
            double totalMemoryMb = node.TotalMemoryGb * 1024;
            return node.MemoryUsageMb / totalMemoryMb;
        }

        /// <summary>
        /// 计算节点综合评分
        /// </summary>
        private static double CalculateNodeScore(NodeInfo node)
        {
            // CPU得分：(100-使用率) * 40%
            double cpuScore = (100 - node.CpuUsage) * 0.4;

            // 内存得分：(100-使用率百分比) * 30%
            double memoryScore = (100 - CalculateMemoryUsageRate(node) * 100) * 0.3;

            // 预留固定得分
            const double networkScore = 20;    // 网络得分
            const double fitScore = 10;         // 任务契合度得分

            return cpuScore + memoryScore + networkScore + fitScore;
        }
    }
}
