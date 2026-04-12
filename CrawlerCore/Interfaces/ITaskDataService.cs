using CrawlerCore.Entities;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CrawlerCore.Interfaces
{
    /// <summary>
    /// 爬虫任务数据操作接口（Master端）
    /// </summary>
    public interface ITaskDataService
    {
        /// <summary>
        /// 创建新的爬虫任务
        /// </summary>
        /// <param name="task">爬虫任务实体</param>
        /// <returns>创建后的任务实体（含TaskId）</returns>
        Task<CrawlerTask> AddTaskAsync(CrawlerTask task);

        /// <summary>
        /// 根据任务ID获取任务信息
        /// </summary>
        /// <param name="taskId">任务ID</param>
        /// <returns>对应的任务实体（不存在则为null）</returns>
        /// <remarks>需确保项目启用可空引用类型（Nullable），否则会有编译警告</remarks>
        Task<CrawlerTask?> GetTaskByIdAsync(string taskId);

        /// <summary>
        /// 获取所有爬虫任务（按创建时间倒序）
        /// </summary>
        /// <returns>任务列表</returns>
        Task<List<CrawlerTask>> GetAllTasksAsync();

        /// <summary>
        /// 更新任务状态
        /// </summary>
        /// <param name="taskId">任务ID</param>
        /// <param name="newStatus">新的任务状态</param>
        /// <returns>是否更新成功</returns>
        Task<bool> UpdateTaskStatusAsync(string taskId, Entities.TaskStatus newStatus);

        /// <summary>
        /// 为任务分配节点
        /// </summary>
        /// <param name="taskId">任务ID</param>
        /// <param name="nodeId">节点ID</param>
        /// <returns>是否分配成功</returns>
        Task<bool> AssignNodeToTaskAsync(string taskId, string nodeId);

        /// <summary>
        /// 根据任务ID删除任务
        /// </summary>
        /// <param name="taskId">任务ID</param>
        /// <returns>是否删除成功</returns>
        Task<bool> DeleteTaskAsync(string taskId);

        /// <summary>
        /// 获取指定节点上所有运行中的任务
        /// </summary>
        /// <param name="nodeId">节点唯一标识</param>
        /// <returns>运行中任务完整实体列表</returns>
        Task<List<CrawlerTask>> GetRunningTasksByNodeIdAsync(string nodeId);

        /// <summary>
        /// 事务化批量重置故障任务：仅将当前仍为Running的任务重置为Pending+清空分配节点
        /// </summary>
        /// <param name="taskIds">待重置的任务ID列表</param>
        /// <returns>成功重置的任务完整实体列表（确保数据最新）</returns>
        Task<List<CrawlerTask>> BatchResetFailedTasksAsync(List<string> taskIds);
    }
}