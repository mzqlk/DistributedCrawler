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

        /// <summary>
        /// 更新任务的完整信息（除TaskId外的所有字段）
        /// </summary>
        /// <param name="task">任务实体</param>
        /// <returns>是否更新成功</returns>
        Task<bool> UpdateTaskAsync(CrawlerTask task);

        #region 爬取结果数据访问扩展
        /// <summary>
        /// 获取所有爬取总结果（含关联的任务信息，按需加载PageResult）
        /// </summary>
        /// <returns>爬取总结果列表</returns>
        Task<List<CrawlResult>> GetAllCrawlResultsAsync();

        /// <summary>
        /// 根据任务ID获取爬取总结果
        /// </summary>
        /// <param name="taskId">爬虫任务ID</param>
        /// <returns>对应爬取总结果（不存在则为null）</returns>
        Task<CrawlResult?> GetCrawlResultByTaskIdAsync(string taskId);

        /// <summary>
        /// 根据爬取结果ID获取关联的所有单页爬取结果
        /// </summary>
        /// <param name="resultId">CrawlResult主键ID</param>
        /// <returns>单页爬取结果列表</returns>
        Task<List<PageResult>> GetPageResultsByResultIdAsync(long resultId);

        /// <summary>
        /// 批量获取爬取结果（含分页，大数据量优化）
        /// </summary>
        /// <param name="pageIndex">页码</param>
        /// <param name="pageSize">每页条数</param>
        /// <returns>分页后的爬取结果列表+总条数</returns>
        Task<(List<CrawlResult> Items, int TotalCount)> GetPagedCrawlResultsAsync(int pageIndex, int pageSize);

        /// <summary>
        /// 分页投影查询：一次性获取CrawlResult+关联TaskName+PageResultCount，支持筛选
        /// </summary>
        /// <param name="pageIndex">0-based页码</param>
        /// <param name="pageSize">每页条数</param>
        /// <param name="searchKeyword">搜索关键词（TaskId/TaskName模糊匹配）</param>
        /// <returns>投影结果列表+总条数</returns>
        Task<(List<CrawlResultProjection> Items, int TotalCount)> GetPagedCrawlResultProjectionsAsync(int pageIndex, int pageSize, string searchKeyword = "");
        #endregion
    }

    public class CrawlResultProjection
    {
        public long Id { get; set; }
        public string TaskId { get; set; } = string.Empty;
        public string TaskName { get; set; } = string.Empty;
        public int PageResultCount { get; set; }
        public DateTime? CompletedAt { get; set; }
    }
}