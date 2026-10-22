using CrawlerCore.Protos;
using CrawlerWorker.Services;

namespace CrawlerCore.Tests.Integration.Infrastructure;

/// <summary>
/// 测试用节点信息采集器：委托真实的 <see cref="NodeInfoCollector"/> 采集机器名、CPU 核数、内存、IP 等，
/// 但做两处改写：
/// <list type="number">
/// <item>NodeId 改为测试指定值。真实实现从进程级环境变量 MASTER_NODE_ID 或机器名生成，同一进程内的两个 Worker 会撞号。</item>
/// <item>心跳上报固定的低 CPU / 内存占用。负载均衡会过滤 CPU≥50% 或内存≥60% 的节点，测试不能受测试机实际负载影响。</item>
/// </list>
/// </summary>
public sealed class TestNodeInfoCollector : INodeInfoCollector
{
    /// <summary>心跳上报的固定 CPU 使用率（%），测试用它判断“心跳已被 Master 处理”</summary>
    public const double FixedCpuUsage = 12.5;

    /// <summary>心跳上报的固定内存使用量（MB）</summary>
    public const double FixedMemoryUsageMb = 256;

    private readonly INodeInfoCollector _inner;
    private readonly string _nodeId;
    private readonly int? _reportedMaxConcurrentTasks;

    /// <param name="reportedMaxConcurrentTasks">注册时上报的并发上限；null 表示用真实值，0 模拟不上报上限的旧版 Worker</param>
    public TestNodeInfoCollector(INodeInfoCollector inner, string nodeId, int? reportedMaxConcurrentTasks = null)
    {
        _inner = inner;
        _nodeId = nodeId;
        _reportedMaxConcurrentTasks = reportedMaxConcurrentTasks;
    }

    public string GetNodeId() => _nodeId;

    public async Task<NodeRegistration> CollectNodeRegistrationAsync(CancellationToken cancellationToken = default)
    {
        var registration = await _inner.CollectNodeRegistrationAsync(cancellationToken).ConfigureAwait(false);
        registration.NodeId = _nodeId;
        if (_reportedMaxConcurrentTasks.HasValue)
        {
            registration.MaxConcurrentTasks = _reportedMaxConcurrentTasks.Value;
        }
        // 内存使用率 = FixedMemoryUsageMb / (TotalMemoryGb*1024)，保证总内存足够大以通过负载均衡过滤
        if (registration.TotalMemoryGb < 4)
        {
            registration.TotalMemoryGb = 4;
        }
        return registration;
    }

    public async Task<Heartbeat> CollectHeartbeatAsync(CancellationToken cancellationToken = default)
    {
        var heartbeat = await _inner.CollectHeartbeatAsync(cancellationToken).ConfigureAwait(false);
        heartbeat.NodeId = _nodeId;
        heartbeat.CpuUsage = FixedCpuUsage;
        heartbeat.MemoryUsageMb = FixedMemoryUsageMb;
        return heartbeat;
    }
}
