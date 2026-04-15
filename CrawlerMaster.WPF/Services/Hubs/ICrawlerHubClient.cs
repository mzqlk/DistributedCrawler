using CrawlerMaster.WPF.Models;
using System.Threading.Tasks;

namespace CrawlerMaster.WPF.Services.Hubs
{
    /// <summary>
    /// SignalR客户端强类型契约，编译时校验方法名，彻底避免拼写错误
    /// </summary>
    public interface ICrawlerHubClient
    {
        /// <summary>
        /// 节点状态更新推送
        /// </summary>
        Task OnNodeUpdated(NodeUpdateMessage message);

        /// <summary>
        /// 任务进度更新推送
        /// </summary>
        Task OnTaskProgressUpdated(TaskProgressMessage message);
    }
}