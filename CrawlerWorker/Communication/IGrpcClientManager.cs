using CrawlerCore.Protos;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace CrawlerWorker.Communication
{
    /// <summary>
    /// gRPC 客户端管理器接口，提供线程安全的客户端获取、健康检查及生命周期管理能力。
    /// </summary>
    public interface IGrpcClientManager : IDisposable
    {
        /// <summary>
        /// 获取 gRPC 协调器客户端实例。线程安全，首次访问时自动建立连接。
        /// </summary>
        CrawlerCoordinator.CrawlerCoordinatorClient CoordinatorClient { get; }

        /// <summary>
        /// 检查当前 gRPC 通道是否处于就绪状态。
        /// </summary>
        /// <returns>如果通道存在且状态为 Ready，则返回 true；否则返回 false。</returns>
        bool IsChannelHealthy();

        /// <summary>
        /// 强制重建 gRPC 通道。通常在检测到连接断开后调用以恢复通信。
        /// </summary>
        void RecreateChannel();

        /// <summary>
        /// 通过 TCP 连接测试 Master 端点的网络连通性，可用于启动前的健康预检。
        /// </summary>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>连通性测试通过则返回 true；否则返回 false。</returns>
        Task<bool> CheckMasterConnectivityAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 优雅关闭 gRPC 通道，支持超时控制。关闭后管理器将不再可用。
        /// </summary>
        /// <param name="cancellationToken">取消令牌。</param>
        Task ShutdownAsync(CancellationToken cancellationToken = default);
    }
}