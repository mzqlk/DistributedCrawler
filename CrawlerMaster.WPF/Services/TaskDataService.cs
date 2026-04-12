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

            // 严格匹配需求：实时查询数据库，确保数据最新
            return await _dbContext.CrawlerTasks
                .Where(t => t.AssignedNode == nodeId && t.Status == TaskStatus.Running)
                .AsNoTracking()
                .ToListAsync()
                .ConfigureAwait(false);
        }

        public async Task<List<CrawlerTask>> BatchResetFailedTasksAsync(List<string> taskIds)
        {
            // 全参数校验，拦截无效数据
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

            // 严格匹配需求：开启数据库事务，保证原子性
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
    }
}