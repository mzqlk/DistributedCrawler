using System.Threading;
using System.Threading.Tasks;
using CrawlerCore.Protos;

namespace CrawlerWorker.Services
{
    /// <summary>
    /// 节点信息采集器接口，定义节点标识获取、注册信息采集和心跳数据采集的标准契约。
    /// </summary>
    public interface INodeInfoCollector
    {
        /// <summary>
        /// 获取当前节点的唯一标识符。
        /// </summary>
        /// <returns>经过规范化的节点ID。</returns>
        string GetNodeId();

        /// <summary>
        /// 异步采集节点注册信息，包含节点ID、机器名、CPU核心数、总内存及IP地址。
        /// </summary>
        /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
        /// <returns>包含节点注册信息的 <see cref="NodeRegistration"/> 对象。</returns>
        Task<NodeRegistration> CollectNodeRegistrationAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 异步采集节点心跳数据，包含CPU使用率、内存使用量、磁盘使用率及活跃任务数。
        /// </summary>
        /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
        /// <returns>包含节点实时负载信息的 <see cref="Heartbeat"/> 对象。</returns>
        Task<Heartbeat> CollectHeartbeatAsync(CancellationToken cancellationToken = default);
    }
}