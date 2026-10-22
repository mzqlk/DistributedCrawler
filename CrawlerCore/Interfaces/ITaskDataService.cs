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
        /// 按状态机更新任务状态：只允许 Pending/Running → Running/Completed/Failed，终态不回退；
        /// 重置为 Pending 只能通过故障恢复与重启恢复的专用方法完成
        /// </summary>
        /// <param name="taskId">任务ID</param>
        /// <param name="newStatus">新的任务状态</param>
        /// <returns>更新是否生效（任务不存在或转换不允许时为 false）</returns>
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
        /// 调度器下发任务前调用：任务仍为 Pending 时原子地改为 Running 并记录分配节点。
        /// 先写库再下发，Worker 之后上报的进度一定晚于这次写入
        /// </summary>
        /// <returns>是否生效（任务不存在或已不是 Pending 时为 false，此时不应下发）</returns>
        Task<bool> TryMarkTaskDispatchedAsync(string taskId, string nodeId);

        /// <summary>
        /// 下发失败时撤销 <see cref="TryMarkTaskDispatchedAsync"/>：仍为“Running + 该节点”时改回 Pending 并清空分配节点
        /// </summary>
        Task<bool> RevertTaskDispatchAsync(string taskId, string nodeId);

        /// <summary>
        /// 重新入队前确认任务仍为 Pending，并清空分配节点
        /// </summary>
        /// <returns>任务存在且为 Pending 时返回 true</returns>
        Task<bool> ClearAssignmentIfPendingAsync(string taskId);

        /// <summary>
        /// 按状态机处理 Worker 上报的任务进度（同一节点的消息按到达顺序调用）：
        /// <list type="bullet">
        /// <item>任务不存在，或已分配给其他节点：丢弃；</item>
        /// <item>终态任务的迟到进度、Worker 排队时上报的 Pending：忽略；</item>
        /// <item>Running/Completed：任务保持 Running（Completed 要等结果落库，见 <see cref="SaveCrawlResultAsync"/>）。
        /// 任务为 Pending 且未分配时（节点被判离线、任务被重置后又重连），由该节点直接认领为 Running；</item>
        /// <item>Failed：任务为“Running + 该节点”时改为 Failed。</item>
        /// </list>
        /// </summary>
        Task<WorkerReportResult> ApplyWorkerProgressAsync(string nodeId, string taskId, Entities.TaskStatus reportedStatus);

        /// <summary>
        /// 在同一事务中写入任务结果并把任务标为 Completed（“已完成”一定有数据）。
        /// 同一任务只保留一份结果：已有结果时返回 <see cref="WorkerReportOutcome.Duplicate"/>；
        /// 任务已分配给其他节点时丢弃。Failed 的任务收到所分配节点的结果时也改为 Completed。
        /// 数据库异常向上抛出，由调用方决定如何处理（见 <see cref="MarkTaskFailedAsync"/>）
        /// </summary>
        Task<WorkerReportResult> SaveCrawlResultAsync(string nodeId, CrawlResult result);

        /// <summary>
        /// 把尚未结束（Pending/Running）且未分配给其他节点的任务标为 Failed，例如结果落库失败时
        /// </summary>
        /// <returns>更新是否生效</returns>
        Task<bool> MarkTaskFailedAsync(string taskId, string nodeId);

        /// <summary>
        /// 获取分配给指定节点、尚未结束（Pending 或 Running）的任务
        /// </summary>
        Task<List<CrawlerTask>> GetUnfinishedTasksByNodeIdAsync(string nodeId);

        /// <summary>
        /// 故障恢复：在一个事务中把分配给该节点、尚未结束（Pending 或 Running）的任务重置为 Pending 并清空分配节点。
        /// 只处理仍分配给该节点的任务，可重复调用（幂等）
        /// </summary>
        /// <returns>被重置的任务（数据库中的最新值）</returns>
        Task<List<CrawlerTask>> ResetUnfinishedTasksOfNodeAsync(string nodeId);

        /// <summary>
        /// Master 重启恢复：在一个事务中把所有 Pending 任务（不论是否已分配），以及分配节点已不在线（离线、不存在或未记录）的
        /// Running 任务重置为 Pending 并清空分配节点，按创建时间升序返回
        /// </summary>
        Task<List<CrawlerTask>> ResetTasksForStartupRecoveryAsync();

        /// <summary>
        /// 统计各节点已分配且尚未结束（Pending 或 Running）的任务数，供负载均衡使用
        /// </summary>
        Task<Dictionary<string, int>> GetUnfinishedTaskCountsByNodeAsync();

        /// <summary>
        /// 更新任务的完整信息（除TaskId外的所有字段）
        /// </summary>
        /// <param name="task">任务实体</param>
        /// <returns>是否更新成功</returns>
        Task<bool> UpdateTaskAsync(CrawlerTask task);

        #region 爬取结果数据访问
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

    /// <summary>
    /// Worker 上报（进度或结果）的处理结论
    /// </summary>
    public enum WorkerReportOutcome
    {
        /// <summary>已接受（状态或结果已按状态机生效）</summary>
        Accepted,
        /// <summary>任务不存在</summary>
        TaskNotFound,
        /// <summary>任务已分配给其他节点（发送方是被隔离的旧节点）</summary>
        NotAssignedToNode,
        /// <summary>按状态机忽略，例如终态任务的迟到进度、Worker 排队时上报的 Pending</summary>
        Ignored,
        /// <summary>该任务的结果此前已落库，本次为重复结果</summary>
        Duplicate
    }

    /// <summary>
    /// Worker 上报的处理结果
    /// </summary>
    /// <param name="Outcome">处理结论</param>
    /// <param name="Task">处理后的任务（任务不存在时为 null）</param>
    public sealed record WorkerReportResult(WorkerReportOutcome Outcome, CrawlerTask? Task);

    public class CrawlResultProjection
    {
        public long Id { get; set; }
        public string TaskId { get; set; } = string.Empty;
        public string TaskName { get; set; } = string.Empty;
        public int PageResultCount { get; set; }
        public DateTime? CompletedAt { get; set; }
    }
}