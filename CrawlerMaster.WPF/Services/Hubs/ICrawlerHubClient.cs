using CrawlerMaster.WPF.Models;
using System.Threading.Tasks;

namespace CrawlerMaster.WPF.Services.Hubs
{
    /// <summary>
    /// 爬虫Hub客户端接口，用于接收服务器推送的消息
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