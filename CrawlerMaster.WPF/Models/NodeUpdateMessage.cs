using CrawlerCore.Entities;

namespace CrawlerMaster.WPF.Models;

/// <summary>
/// 节点状态实时推送消息
/// </summary>
public class NodeUpdateMessage
{
    public string NodeId { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public NodeStatus Status { get; set; }
    public double CpuUsage { get; set; }
    public double MemoryUsageMb { get; set; }
    public int ActiveTasks { get; set; }
    public DateTime LastHeartbeat { get; set; }
}