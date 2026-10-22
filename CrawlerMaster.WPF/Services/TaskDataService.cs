using CrawlerCore.Entities;
using CrawlerCore.Infrastructure;
using CrawlerCore.Interfaces;
using CrawlerMaster.WPF.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TaskStatus = CrawlerCore.Entities.TaskStatus;

namespace CrawlerMaster.WPF.Services
{
    /// <summary>
    /// 爬虫任务数据服务。任务状态的所有写入都集中在这里，按状态机执行：
    /// Pending → Running → Completed/Failed，终态不回退；重置为 Pending 只能由故障恢复与重启恢复完成。
    /// 状态更新使用带条件的 UPDATE（ExecuteUpdate），条件即“允许的来源状态”，保证并发下的原子性。
    /// </summary>
    public class TaskDataService : ITaskDataService
    {
        private readonly CrawlerDbContext _dbContext;

        public TaskDataService(CrawlerDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        /// <summary>
        /// 创建新任务
        /// </summary>
        public async Task<CrawlerTask> AddTaskAsync(CrawlerTask task)
        {
            await _dbContext.CrawlerTasks.AddAsync(task);
            await _dbContext.SaveChangesAsync();
            return task;
        }

        /// <summary>
        /// 按ID查询任务（不跟踪：状态由带条件的更新语句修改，跟踪的实体会过期）
        /// </summary>
        public async Task<CrawlerTask?> GetTaskByIdAsync(string taskId)
        {
            return await _dbContext.CrawlerTasks
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.TaskId == taskId);
        }

        /// <summary>
        /// 查询所有任务（按创建时间倒序，只读）
        /// </summary>
        public async Task<List<CrawlerTask>> GetAllTasksAsync()
        {
            return await _dbContext.CrawlerTasks
                .AsNoTracking()
                .OrderByDescending(t => t.CreatedAt)
                .ToListAsync();
        }

        /// <summary>
        /// 按状态机更新任务状态：只允许从 Pending/Running 转到 Running/Completed/Failed，终态不回退；
        /// 不允许借此改回 Pending（重置只能走故障恢复与重启恢复）
        /// </summary>
        public async Task<bool> UpdateTaskStatusAsync(string taskId, TaskStatus newStatus)
        {
            if (newStatus == TaskStatus.Pending)
            {
                LogHelper.Warn($"任务 {taskId} 不允许直接改回 Pending，重置请使用故障恢复或重启恢复");
                return false;
            }

            var affected = await _dbContext.CrawlerTasks
                .Where(t => t.TaskId == taskId && (t.Status == TaskStatus.Pending || t.Status == TaskStatus.Running))
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, newStatus));
            return affected > 0;
        }

        /// <summary>
        /// 为任务分配节点
        /// </summary>
        public async Task<bool> AssignNodeToTaskAsync(string taskId, string nodeId)
        {
            var task = await _dbContext.CrawlerTasks.FirstOrDefaultAsync(t => t.TaskId == taskId);
            if (task == null) return false;

            task.AssignedNode = nodeId;
            return await _dbContext.SaveChangesAsync() > 0;
        }

        /// <summary>
        /// 删除任务
        /// </summary>
        public async Task<bool> DeleteTaskAsync(string taskId)
        {
            var task = await _dbContext.CrawlerTasks.FirstOrDefaultAsync(t => t.TaskId == taskId);
            if (task == null) return false;

            _dbContext.CrawlerTasks.Remove(task);
            return await _dbContext.SaveChangesAsync() > 0;
        }

        public async Task<bool> TryMarkTaskDispatchedAsync(string taskId, string nodeId)
        {
            var affected = await _dbContext.CrawlerTasks
                .Where(t => t.TaskId == taskId && t.Status == TaskStatus.Pending)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.Status, TaskStatus.Running)
                    .SetProperty(t => t.AssignedNode, nodeId))
                .ConfigureAwait(false);
            return affected > 0;
        }

        public async Task<bool> RevertTaskDispatchAsync(string taskId, string nodeId)
        {
            var affected = await _dbContext.CrawlerTasks
                .Where(t => t.TaskId == taskId && t.Status == TaskStatus.Running && t.AssignedNode == nodeId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.Status, TaskStatus.Pending)
                    .SetProperty(t => t.AssignedNode, string.Empty))
                .ConfigureAwait(false);
            return affected > 0;
        }

        public async Task<bool> ClearAssignmentIfPendingAsync(string taskId)
        {
            var affected = await _dbContext.CrawlerTasks
                .Where(t => t.TaskId == taskId && t.Status == TaskStatus.Pending)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.AssignedNode, string.Empty))
                .ConfigureAwait(false);
            return affected > 0;
        }

        public async Task<WorkerReportResult> ApplyWorkerProgressAsync(string nodeId, string taskId, TaskStatus reportedStatus)
        {
            var task = await GetTaskByIdAsync(taskId).ConfigureAwait(false);
            if (task == null)
            {
                return new WorkerReportResult(WorkerReportOutcome.TaskNotFound, null);
            }
            if (!string.IsNullOrEmpty(task.AssignedNode) && task.AssignedNode != nodeId)
            {
                return new WorkerReportResult(WorkerReportOutcome.NotAssignedToNode, task);
            }
            if (IsTerminal(task.Status))
            {
                // 终态不回退：迟到的旧进度、重复执行的第二份 Completed 都在这里被忽略
                return new WorkerReportResult(WorkerReportOutcome.Ignored, task);
            }

            switch (reportedStatus)
            {
                case TaskStatus.Running:
                case TaskStatus.Completed:
                    {
                        // Completed 进度只代表 Worker 执行完毕，库中保持 Running，直到结果落库（SaveCrawlResultAsync）
                        if (task.Status == TaskStatus.Running && task.AssignedNode == nodeId)
                        {
                            return new WorkerReportResult(WorkerReportOutcome.Accepted, task);
                        }

                        // 认领：任务被重置为 Pending（节点被判离线后又重连，仍在执行它），
                        // 或历史数据中的“Running 但未记录节点”。直接记到该节点名下，
                        // 调度器出队时发现任务已不是 Pending，就不会再重复下发
                        var adopted = await _dbContext.CrawlerTasks
                            .Where(t => t.TaskId == taskId
                                        && (t.Status == TaskStatus.Pending || t.Status == TaskStatus.Running)
                                        && (t.AssignedNode == string.Empty || t.AssignedNode == nodeId))
                            .ExecuteUpdateAsync(s => s
                                .SetProperty(t => t.Status, TaskStatus.Running)
                                .SetProperty(t => t.AssignedNode, nodeId))
                            .ConfigureAwait(false);
                        if (adopted == 0)
                        {
                            return new WorkerReportResult(WorkerReportOutcome.Ignored, task);
                        }

                        LogHelper.Info($"节点 {nodeId} 认领任务 {taskId}（原状态 {task.Status}）");
                        task.Status = TaskStatus.Running;
                        task.AssignedNode = nodeId;
                        return new WorkerReportResult(WorkerReportOutcome.Accepted, task);
                    }

                case TaskStatus.Failed:
                    {
                        // 只接受当前执行者的失败；任务被重置后，旧执行的失败不影响重新调度
                        if (task.Status != TaskStatus.Running)
                        {
                            return new WorkerReportResult(WorkerReportOutcome.Ignored, task);
                        }

                        var failed = await _dbContext.CrawlerTasks
                            .Where(t => t.TaskId == taskId && t.Status == TaskStatus.Running && t.AssignedNode == nodeId)
                            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, TaskStatus.Failed))
                            .ConfigureAwait(false);
                        if (failed == 0)
                        {
                            return new WorkerReportResult(WorkerReportOutcome.Ignored, task);
                        }

                        task.Status = TaskStatus.Failed;
                        return new WorkerReportResult(WorkerReportOutcome.Accepted, task);
                    }

                default:
                    // Pending：Worker 把任务放进本地队列时上报，Master 以调度器写入的 Running 为准
                    return new WorkerReportResult(WorkerReportOutcome.Ignored, task);
            }
        }

        public async Task<WorkerReportResult> SaveCrawlResultAsync(string nodeId, CrawlResult result)
        {
            ArgumentNullException.ThrowIfNull(result);

            // Microsoft.Data.Sqlite 的事务默认是 BEGIN IMMEDIATE：先拿写锁，检查与写入之间不会被其他连接插入
            await using var transaction = await _dbContext.Database.BeginTransactionAsync().ConfigureAwait(false);

            var task = await _dbContext.CrawlerTasks
                .FirstOrDefaultAsync(t => t.TaskId == result.TaskId)
                .ConfigureAwait(false);
            if (task == null)
            {
                return new WorkerReportResult(WorkerReportOutcome.TaskNotFound, null);
            }

            if (await _dbContext.CrawlResults.AnyAsync(r => r.TaskId == result.TaskId).ConfigureAwait(false))
            {
                return new WorkerReportResult(WorkerReportOutcome.Duplicate, task);
            }

            if (!string.IsNullOrEmpty(task.AssignedNode) && task.AssignedNode != nodeId)
            {
                return new WorkerReportResult(WorkerReportOutcome.NotAssignedToNode, task);
            }

            _dbContext.CrawlResults.Add(result);
            task.Status = TaskStatus.Completed;
            task.AssignedNode = nodeId;

            try
            {
                await _dbContext.SaveChangesAsync().ConfigureAwait(false);
                await transaction.CommitAsync().ConfigureAwait(false);
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                // 唯一索引兜底：同一任务的结果已经存在
                await transaction.RollbackAsync().ConfigureAwait(false);
                _dbContext.ChangeTracker.Clear();
                return new WorkerReportResult(WorkerReportOutcome.Duplicate, await GetTaskByIdAsync(result.TaskId).ConfigureAwait(false));
            }

            return new WorkerReportResult(WorkerReportOutcome.Accepted, task);
        }

        public async Task<bool> MarkTaskFailedAsync(string taskId, string nodeId)
        {
            var affected = await _dbContext.CrawlerTasks
                .Where(t => t.TaskId == taskId
                            && (t.Status == TaskStatus.Pending || t.Status == TaskStatus.Running)
                            && (t.AssignedNode == string.Empty || t.AssignedNode == nodeId))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.Status, TaskStatus.Failed)
                    .SetProperty(t => t.AssignedNode, nodeId))
                .ConfigureAwait(false);
            return affected > 0;
        }

        public async Task<List<CrawlerTask>> GetUnfinishedTasksByNodeIdAsync(string nodeId)
        {
            if (string.IsNullOrWhiteSpace(nodeId))
            {
                LogHelper.Warn("获取节点未结束任务失败，节点ID为空");
                return new List<CrawlerTask>();
            }

            return await _dbContext.CrawlerTasks
                .AsNoTracking()
                .Where(t => t.AssignedNode == nodeId
                            && (t.Status == TaskStatus.Pending || t.Status == TaskStatus.Running))
                .ToListAsync()
                .ConfigureAwait(false);
        }

        public async Task<List<CrawlerTask>> ResetUnfinishedTasksOfNodeAsync(string nodeId)
        {
            if (string.IsNullOrWhiteSpace(nodeId))
            {
                LogHelper.Warn("重置节点任务失败，节点ID为空");
                return new List<CrawlerTask>();
            }

            await using var transaction = await _dbContext.Database.BeginTransactionAsync().ConfigureAwait(false);
            try
            {
                // 条件里带上 AssignedNode：已经改派给其他节点的任务不会被误重置，重复调用也是幂等的
                var toReset = await _dbContext.CrawlerTasks
                    .Where(t => t.AssignedNode == nodeId
                                && (t.Status == TaskStatus.Pending || t.Status == TaskStatus.Running))
                    .ToListAsync()
                    .ConfigureAwait(false);

                foreach (var task in toReset)
                {
                    task.Status = TaskStatus.Pending;
                    task.AssignedNode = string.Empty;
                }

                if (toReset.Count > 0)
                {
                    await _dbContext.SaveChangesAsync().ConfigureAwait(false);
                }
                await transaction.CommitAsync().ConfigureAwait(false);
                return toReset;
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync().ConfigureAwait(false);
                LogHelper.Error(ex, $"重置节点 {nodeId} 的未结束任务失败，已回滚事务");
                throw;
            }
        }

        public async Task<List<CrawlerTask>> ResetTasksForStartupRecoveryAsync()
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync().ConfigureAwait(false);

            var onlineNodeIds = _dbContext.NodeInfos
                .Where(n => n.Status == NodeStatus.Online)
                .Select(n => n.NodeId);

            // 在数据库里筛选：所有 Pending（包括“Pending + 已分配”的旧数据），以及执行节点已不在线的 Running
            var toRecover = await _dbContext.CrawlerTasks
                .Where(t => t.Status == TaskStatus.Pending
                            || (t.Status == TaskStatus.Running
                                && (t.AssignedNode == string.Empty || !onlineNodeIds.Contains(t.AssignedNode))))
                .OrderBy(t => t.CreatedAt)
                .ToListAsync()
                .ConfigureAwait(false);

            foreach (var task in toRecover)
            {
                task.Status = TaskStatus.Pending;
                task.AssignedNode = string.Empty;
            }

            if (toRecover.Count > 0)
            {
                await _dbContext.SaveChangesAsync().ConfigureAwait(false);
            }
            await transaction.CommitAsync().ConfigureAwait(false);
            return toRecover;
        }

        public async Task<Dictionary<string, int>> GetUnfinishedTaskCountsByNodeAsync()
        {
            return await _dbContext.CrawlerTasks
                .AsNoTracking()
                .Where(t => t.AssignedNode != string.Empty
                            && (t.Status == TaskStatus.Pending || t.Status == TaskStatus.Running))
                .GroupBy(t => t.AssignedNode)
                .Select(g => new { NodeId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.NodeId, x => x.Count)
                .ConfigureAwait(false);
        }

        public async Task<bool> UpdateTaskAsync(CrawlerTask task)
        {
            var existing = await _dbContext.CrawlerTasks.FindAsync(task.TaskId);
            if (existing == null) return false;

            _dbContext.Entry(existing).CurrentValues.SetValues(task);
            return await _dbContext.SaveChangesAsync() > 0;
        }

        private static bool IsTerminal(TaskStatus status) => status is TaskStatus.Completed or TaskStatus.Failed;

        /// <summary>SQLite 约束冲突（SQLITE_CONSTRAINT = 19），这里只可能来自 CrawlResults.TaskId 的唯一索引</summary>
        private static bool IsUniqueConstraintViolation(DbUpdateException ex)
            => ex.InnerException is SqliteException { SqliteErrorCode: 19 };

        #region ITaskDataService 扩展实现
        public async Task<List<CrawlResult>> GetAllCrawlResultsAsync()
        {
            // 不预加载PageResult，按需延迟加载，避免一次性加载大量数据
            return await _dbContext.CrawlResults
                .AsNoTracking()
                .OrderByDescending(cr => cr.CompletedAt)
                .ToListAsync();
        }

        public async Task<CrawlResult?> GetCrawlResultByTaskIdAsync(string taskId)
        {
            return await _dbContext.CrawlResults
                .AsNoTracking()
                .FirstOrDefaultAsync(cr => cr.TaskId == taskId);
        }

        public async Task<List<PageResult>> GetPageResultsByResultIdAsync(long resultId)
        {
            return await _dbContext.PageResults
                .AsNoTracking()
                .Where(pr => pr.ResultId == resultId)
                .OrderBy(pr => pr.Url)
                .ToListAsync();
        }

        public async Task<(List<CrawlResult> Items, int TotalCount)> GetPagedCrawlResultsAsync(int pageIndex, int pageSize)
        {
            var totalCount = await _dbContext.CrawlResults.CountAsync();
            var items = await _dbContext.CrawlResults
                .AsNoTracking()
                .OrderByDescending(cr => cr.CompletedAt)
                .Skip(pageIndex * pageSize)
                .Take(pageSize)
                .ToListAsync();
            return (items, totalCount);
        }

        public async Task<(List<CrawlResultProjection> Items, int TotalCount)> GetPagedCrawlResultProjectionsAsync(int pageIndex, int pageSize, string searchKeyword = "")
        {
            // 1. 构建基础查询（带筛选）
            var query = _dbContext.CrawlResults
                .AsNoTracking()
                // 关联CrawlerTask获取TaskName
                .Join(_dbContext.CrawlerTasks,
                    cr => cr.TaskId,
                    ct => ct.TaskId,
                    (cr, ct) => new { cr, ct })
                // 左关联PageResult获取数量
                .GroupJoin(_dbContext.PageResults,
                    x => x.cr.Id,
                    pr => pr.ResultId,
                    (x, prs) => new CrawlResultProjection
                    {
                        Id = x.cr.Id,
                        TaskId = x.cr.TaskId,
                        TaskName = x.ct.TaskName,
                        PageResultCount = prs.Count(),
                        CompletedAt = x.cr.CompletedAt
                    });

            // 2. 应用筛选（TaskId精确匹配或TaskName模糊匹配）
            if (!string.IsNullOrWhiteSpace(searchKeyword))
            {
                query = query.Where(p =>
                    p.TaskId == searchKeyword ||
                    p.TaskName.Contains(searchKeyword));
            }

            // 3. 先获取总条数（避免分页后再Count）
            var totalCount = await query.CountAsync();

            // 4. 应用排序和分页
            var items = await query
                .OrderByDescending(p => p.CompletedAt)
                .Skip(pageIndex * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return (items, totalCount);
        }
        #endregion
    }
}
