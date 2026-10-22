using System.Threading.Tasks;
using CrawlerCore.Entities;

namespace CrawlerMaster.WPF.Services
{
    /// <summary>
    /// gRPC 协调服务对调度、健康检查等后台服务暴露的操作
    /// </summary>
    public interface ICrawlerCoordinatorService
    {
        /// <summary>
        /// 通过节点的双向流下发任务（写入该流的发送通道）
        /// </summary>
        /// <returns>是否已放入发送通道；节点没有活跃流时返回 false</returns>
        Task<bool> SendTaskToNodeAsync(string nodeId, CrawlerTask task);

        /// <summary>
        /// 节点当前是否有活跃的双向流（库中的 Online 可能是 Master 重启前留下的旧状态，下发前以此为准）
        /// </summary>
        bool IsNodeConnected(string nodeId);

        /// <summary>
        /// 节点注册时声明的并发任务上限，0 表示不限（旧版 Worker 不上报）
        /// </summary>
        int GetMaxConcurrentTasks(string nodeId);

        /// <summary>
        /// 主动断开节点的双向流（健康检查判定离线时调用），之后该节点需要重新建流
        /// </summary>
        void DisconnectNode(string nodeId);
    }
}
