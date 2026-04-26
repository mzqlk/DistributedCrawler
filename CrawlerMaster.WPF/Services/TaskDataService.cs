using CrawlerCore.Entities;
using CrawlerCore.Infrastructure;
using CrawlerCore.Interfaces;
using CrawlerMaster.WPF.Data;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TaskStatus = CrawlerCore.Entities.TaskStatus;

namespace CrawlerMaster.WPF.Services
{
    /// <summary>
    /// 爬虫任务数据CRUD服务实现
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
        /// 按ID查询任务
        /// </summary>
        public async Task<CrawlerTask?> GetTaskByIdAsync(string taskId)
        {
            return await _dbContext.CrawlerTasks
                .FirstOrDefaultAsync(t => t.TaskId == taskId);
        }

        /// <summary>
        /// 查询所有任务（按创建时间倒序）
        /// </summary>
        public async Task<List<CrawlerTask>> GetAllTasksAsync()
        {
            return await _dbContext.CrawlerTasks
                .OrderByDescending(t => t.CreatedAt)
                .ToListAsync();
        }

        /// <summary>
        /// 更新任务状态
        /// </summary>
        public async Task<bool> UpdateTaskStatusAsync(string taskId, CrawlerCore.Entities.TaskStatus newStatus)
        {
            var task = await GetTaskByIdAsync(taskId);
            if (task == null) return false;

            task.Status = newStatus;
            return await _dbContext.SaveChangesAsync() > 0;
        }

        /// <summary>
        /// 为任务分配节点
        /// </summary>
        public async Task<bool> AssignNodeToTaskAsync(string taskId, string nodeId)
        {
            var task = await GetTaskByIdAsync(taskId);
            if (task == null) return false;

            task.AssignedNode = nodeId;
            return await _dbContext.SaveChangesAsync() > 0;
        }

        /// <summary>
        /// 删除任务
        /// </summary>
        public async Task<bool> DeleteTaskAsync(string taskId)
        {
            var task = await GetTaskByIdAsync(taskId);
            if (task == null) return false;

            _dbContext.CrawlerTasks.Remove(task);
            return await _dbContext.SaveChangesAsync() > 0;
        }

        public async Task<List<CrawlerTask>> GetRunningTasksByNodeIdAsync(string nodeId)
        {
            if (string.IsNullOrWhiteSpace(nodeId))
            {
                LogHelper.Warn("获取节点运行任务失败，节点ID为空");
                return new List<CrawlerTask>();
            }

            return await _dbContext.CrawlerTasks
                .Where(t => t.AssignedNode == nodeId && t.Status == TaskStatus.Running)
                .AsNoTracking()
                .ToListAsync()
                .ConfigureAwait(false);
        }

        public async Task<List<CrawlerTask>> BatchResetFailedTasksAsync(List<string> taskIds)
        {
            if (taskIds == null || taskIds.Count == 0)
            {
                LogHelper.Warn("批量重置故障任务失败，待处理任务ID列表为空");
                return new List<CrawlerTask>();
            }

            var validTaskIds = taskIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
            if (validTaskIds.Count == 0)
            {
                LogHelper.Error("批量重置故障任务失败，所有任务ID均为无效值");
                return new List<CrawlerTask>();
            }

            using var transaction = await _dbContext.Database.BeginTransactionAsync().ConfigureAwait(false);
            try
            {
                // 为确保在所有数据库提供程序（包括 SQLite 内存模式）上稳定工作，
                // 先查询出所有仍为 Running 的实体，逐条修改后保存。
                var toReset = await _dbContext.CrawlerTasks
                    .Where(t => validTaskIds.Contains(t.TaskId) && t.Status == TaskStatus.Running)
                    .ToListAsync()
                    .ConfigureAwait(false);

                if (toReset.Count == 0)
                {
                    await transaction.CommitAsync().ConfigureAwait(false);
                    LogHelper.Warn("批量重置故障任务完成，无符合条件的任务被更新");
                    return new List<CrawlerTask>();
                }

                foreach (var t in toReset)
                {
                    t.Status = TaskStatus.Pending;
                    t.AssignedNode = string.Empty;
                }

                await _dbContext.SaveChangesAsync().ConfigureAwait(false);

                // 更新完成后，重新查询成功重置的任务并返回（AsNoTracking 保证返回的是数据库最新值）
                var resetTasks = await _dbContext.CrawlerTasks
                    .Where(t => validTaskIds.Contains(t.TaskId) && t.Status == TaskStatus.Pending && string.IsNullOrEmpty(t.AssignedNode))
                    .AsNoTracking()
                    .ToListAsync()
                    .ConfigureAwait(false);

                await transaction.CommitAsync().ConfigureAwait(false);

                LogHelper.Info(
                    "批量重置故障任务完成 | 待处理任务数:{TotalCount} 成功重置数:{SuccessCount}",
                    validTaskIds.Count, resetTasks.Count);
                return resetTasks;
            }
            catch (Exception ex)
            {
                // 异常时强制回滚事务，保证数据一致性
                await transaction.RollbackAsync().ConfigureAwait(false);
                LogHelper.Error(ex, $"批量重置故障任务失败，已回滚事务 | 待处理任务数:{validTaskIds.Count}");
                return new List<CrawlerTask>();
            }
        }

        public async Task<bool> UpdateTaskAsync(CrawlerTask task)
        {
            var existing = await _dbContext.CrawlerTasks.FindAsync(task.TaskId);
            if (existing == null) return false;

            _dbContext.Entry(existing).CurrentValues.SetValues(task);
            return await _dbContext.SaveChangesAsync() > 0;
        }

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