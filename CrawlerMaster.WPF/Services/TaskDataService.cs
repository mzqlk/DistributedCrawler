using CrawlerCore.Entities;
using CrawlerCore.Interfaces;
using CrawlerMaster.WPF.Data;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

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
    }
}