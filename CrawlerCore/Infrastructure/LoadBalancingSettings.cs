namespace CrawlerCore.Infrastructure;

/// <summary>
/// Master 负载均衡配置项
/// </summary>
public class LoadBalancingSettings
{
    public const string SectionName = "LoadBalancingSettings";

    /// <summary>
    /// CPU 使用率（%）低于该值的节点优先分配任务，默认 90。
    /// 没有节点满足阈值时，退而选择负载最低的在线节点，任务不会无限期等待
    /// </summary>
    public double MaxCpuPercent { get; set; } = 90;

    /// <summary>
    /// 内存使用率（%）低于该值的节点优先分配任务，默认 90。
    /// Windows 上 Worker 上报的是整机已用内存，开发机上常见 60% 以上，阈值不宜过严
    /// </summary>
    public double MaxMemoryPercent { get; set; } = 90;
}
