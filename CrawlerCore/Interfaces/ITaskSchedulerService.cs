using CrawlerCore.Entities;
using System.Threading.Tasks;

namespace CrawlerCore.Interfaces
{
    /// <summary>
    /// 爬虫任务调度服务接口（Master端）
    /// </summary>
    public interface ITaskSchedulerService
    {
        /// <summary>
        /// 将任务加入调度队列（支持优先级）
        /// </summary>
        /// <param name="task">爬虫任务实体</param>
        /// <param name="priority">任务优先级（1=最低，5=最高，默认1）</param>
        /// <returns>是否入队成功</returns>
        Task<bool> ScheduleTaskAsync(CrawlerTask task, int priority = 1);

        /// <summary>
        /// 将任务分配到最优Worker节点（基于负载均衡）
        /// </summary>
        /// <param name="taskId">任务ID</param>
        /// <returns>分配的节点ID（分配失败则为空）</returns>
        Task<string?> AssignTaskToOptimalNodeAsync(string taskId);

        /// <summary>
        /// 重分配故障任务（节点离线导致的未完成任务）
        /// </summary>
        /// <param name="nodeId">离线的节点ID</param>
        /// <returns>成功重分配的任务数量</returns>
        Task<int> ReassignFailedTasksAsync(string nodeId);

        /// <summary>
        /// 非阻塞出队方法
        /// </summary>
        /// <param name="task">出队任务（空则为null）</param>
        /// <returns>是否出队成功</returns>
        bool TryDequeue(out CrawlerTask? task);
    }
}