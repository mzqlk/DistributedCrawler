using CrawlerCore.Protos;

namespace CrawlerWorker.Communication;

/// <summary>
/// 线程安全的gRPC客户端管理器，生命周期与主机绑定
/// 严格对齐原项目重试规范、配置体系
/// </summary>
public interface IGrpcClientManager
{
    /// <summary>
    /// 获取CrawlerCoordinator服务客户端
    /// </summary>
    CrawlerCoordinator.CrawlerCoordinatorClient CoordinatorClient { get; }

    /// <summary>
    /// 检查Master端点连通性
    /// </summary>
    Task<bool> CheckMasterConnectivityAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 优雅关闭gRPC通道
    /// </summary>
    Task ShutdownAsync(CancellationToken cancellationToken = default);
}