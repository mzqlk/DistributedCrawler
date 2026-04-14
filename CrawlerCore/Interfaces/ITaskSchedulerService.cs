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
        /// <returns>是否入队成功</returns>
        Task<bool> ScheduleTaskAsync(CrawlerTask task);

        /// <summary>
        /// 将任务分配到最优Worker节点（基于负载均衡）
        /// </summary>
        /// <param name="taskId">任务ID</param>
        /// <returns>分配的节点ID（分配失败则为空）</returns>
        Task<string?> AssignTaskToOptimalNodeAsync(string taskId);

        /// <summary>
        /// 非阻塞出队方法
        /// </summary>
        /// <param name="task">出队任务（空则为null）</param>
        /// <returns>是否出队成功</returns>
        bool TryDequeue(out CrawlerTask? task);

        /// <summary>
        /// 从数据库恢复未调度的Pending任务（重启自动入队）
        /// </summary>
        Task RecoverPendingTasksFromDatabaseAsync();
    }
}