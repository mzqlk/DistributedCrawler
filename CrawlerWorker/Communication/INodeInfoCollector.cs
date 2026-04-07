using CrawlerCore.Protos;

namespace CrawlerWorker.Services;

/// <summary>
/// 节点信息采集接口，严格对齐原项目NodeRegistration消息体
/// </summary>
public interface INodeInfoCollector
{
    /// <summary>
    /// 采集节点注册所需的完整信息
    /// </summary>
    Task<NodeRegistration> CollectNodeRegistrationAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取节点唯一ID，对齐原项目规则
    /// </summary>
    string GetNodeId();
}