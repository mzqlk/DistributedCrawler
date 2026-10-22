using CrawlerCore.Entities;
using CrawlerCore.Interfaces;
using CrawlerMaster.WPF.Data;
using CrawlerMaster.WPF.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;
using TaskStatus = CrawlerCore.Entities.TaskStatus;

namespace CrawlerCore.Tests.Services
{
    /// <summary>
    /// TaskDataService 中任务状态机的单元测试（T4：M-02、M-04、M-06、M-10、M-11、M-12）。
    /// 使用 SQLite 内存库，与生产相同的实体映射（状态按字符串存储、CrawlResults.TaskId 唯一索引）。
    /// </summary>
    public class TaskStateMachineTests : IDisposable
    {
        private const string NodeA = "node-a";
        private const string NodeB = "node-b";

        private readonly SqliteConnection _connection;
        private readonly CrawlerDbContext _dbContext;
        private readonly TaskDataService _service;

        public TaskStateMachineTests()
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();
            var options = new DbContextOptionsBuilder<CrawlerDbContext>().UseSqlite(_connection).Options;
            _dbContext = new CrawlerDbContext(options, new Mock<IConfiguration>().Object);
            _dbContext.Database.EnsureCreated();
            _service = new TaskDataService(_dbContext);
        }

        public void Dispose()
        {
            _dbContext.Dispose();
            _connection.Dispose();
        }

        #region 辅助方法
        private async Task<CrawlerTask> AddTaskAsync(TaskStatus status = TaskStatus.Pending, string assignedNode = "", string name = "任务")
        {
            var task = new CrawlerTask { TaskName = name, Status = status, AssignedNode = assignedNode, MaxPages = 10 };
            _dbContext.CrawlerTasks.Add(task);
            await _dbContext.SaveChangesAsync();
            _dbContext.ChangeTracker.Clear();
            return task;
        }

        private async Task<CrawlerTask> ReloadAsync(string taskId)
            => await _dbContext.CrawlerTasks.AsNoTracking().SingleAsync(t => t.TaskId == taskId);

        private static CrawlResult NewResult(string taskId, int pages = 2) => new()
        {
            TaskId = taskId,
            CompletedAt = DateTime.UtcNow,
            PageResults = Enumerable.Range(0, pages).Select(i => new PageResult
            {
                Url = $"http://example.test/{i}",
                FinalUrl = $"http://example.test/{i}",
                StatusCode = 200
            }).ToList()
        };

        private Task<int> CountResultsAsync(string taskId) => _dbContext.CrawlResults.CountAsync(r => r.TaskId == taskId);
        #endregion

        #region 通用状态更新（M-10）
        [Fact(DisplayName = "状态机：终态不回退，也不能借 UpdateTaskStatusAsync 改回 Pending")]
        public async Task UpdateTaskStatus_TerminalStatesDoNotRegress()
        {
            var task = await AddTaskAsync(TaskStatus.Completed, NodeA);

            Assert.False(await _service.UpdateTaskStatusAsync(task.TaskId, TaskStatus.Running));
            Assert.False(await _service.UpdateTaskStatusAsync(task.TaskId, TaskStatus.Failed));
            Assert.Equal(TaskStatus.Completed, (await ReloadAsync(task.TaskId)).Status);

            var running = await AddTaskAsync(TaskStatus.Running, NodeA);
            Assert.False(await _service.UpdateTaskStatusAsync(running.TaskId, TaskStatus.Pending));
            Assert.Equal(TaskStatus.Running, (await ReloadAsync(running.TaskId)).Status);
        }
        #endregion

        #region 调度下发（M-02③、M-17）
        [Fact(DisplayName = "下发前写库：只有 Pending 任务能改为 Running + 节点，且只生效一次")]
        public async Task TryMarkTaskDispatched_OnlyFromPending()
        {
            var task = await AddTaskAsync();

            Assert.True(await _service.TryMarkTaskDispatchedAsync(task.TaskId, NodeA));
            Assert.False(await _service.TryMarkTaskDispatchedAsync(task.TaskId, NodeB));

            var stored = await ReloadAsync(task.TaskId);
            Assert.Equal(TaskStatus.Running, stored.Status);
            Assert.Equal(NodeA, stored.AssignedNode);
        }

        [Fact(DisplayName = "下发失败撤销：只撤销仍为“Running + 该节点”的任务")]
        public async Task RevertTaskDispatch_OnlyForSameNode()
        {
            var task = await AddTaskAsync();
            await _service.TryMarkTaskDispatchedAsync(task.TaskId, NodeA);

            Assert.False(await _service.RevertTaskDispatchAsync(task.TaskId, NodeB));
            Assert.True(await _service.RevertTaskDispatchAsync(task.TaskId, NodeA));

            var stored = await ReloadAsync(task.TaskId);
            Assert.Equal(TaskStatus.Pending, stored.Status);
            Assert.Equal(string.Empty, stored.AssignedNode);
        }

        [Fact(DisplayName = "重新入队前确认：只有 Pending 任务返回 true 并清空分配节点")]
        public async Task ClearAssignmentIfPending_OnlyForPending()
        {
            var legacy = await AddTaskAsync(TaskStatus.Pending, NodeA);
            var done = await AddTaskAsync(TaskStatus.Completed, NodeA);

            Assert.True(await _service.ClearAssignmentIfPendingAsync(legacy.TaskId));
            Assert.False(await _service.ClearAssignmentIfPendingAsync(done.TaskId));
            Assert.Equal(string.Empty, (await ReloadAsync(legacy.TaskId)).AssignedNode);
            Assert.Equal(NodeA, (await ReloadAsync(done.TaskId)).AssignedNode);
        }
        #endregion

        #region Worker 进度（M-02④、M-04①、M-10、M-11）
        [Fact(DisplayName = "进度：Worker 排队时上报的 Pending 不会把 Running 降级")]
        public async Task ApplyWorkerProgress_PendingDoesNotDowngradeRunning()
        {
            var task = await AddTaskAsync(TaskStatus.Running, NodeA);

            var result = await _service.ApplyWorkerProgressAsync(NodeA, task.TaskId, TaskStatus.Pending);

            Assert.Equal(WorkerReportOutcome.Ignored, result.Outcome);
            Assert.Equal(TaskStatus.Running, (await ReloadAsync(task.TaskId)).Status);
        }

        [Fact(DisplayName = "进度：Completed 进度只代表执行完毕，结果落库前任务保持 Running（M-11）")]
        public async Task ApplyWorkerProgress_CompletedKeepsRunningUntilResultStored()
        {
            var task = await AddTaskAsync(TaskStatus.Running, NodeA);

            var result = await _service.ApplyWorkerProgressAsync(NodeA, task.TaskId, TaskStatus.Completed);

            Assert.Equal(WorkerReportOutcome.Accepted, result.Outcome);
            Assert.Equal(TaskStatus.Running, result.Task!.Status);
            Assert.Equal(TaskStatus.Running, (await ReloadAsync(task.TaskId)).Status);
        }

        [Fact(DisplayName = "进度：任务已分配给其他节点时丢弃（M-04①）")]
        public async Task ApplyWorkerProgress_FromOtherNode_IsRejected()
        {
            var task = await AddTaskAsync(TaskStatus.Running, NodeB);

            var running = await _service.ApplyWorkerProgressAsync(NodeA, task.TaskId, TaskStatus.Running);
            var failed = await _service.ApplyWorkerProgressAsync(NodeA, task.TaskId, TaskStatus.Failed);

            Assert.Equal(WorkerReportOutcome.NotAssignedToNode, running.Outcome);
            Assert.Equal(WorkerReportOutcome.NotAssignedToNode, failed.Outcome);
            var stored = await ReloadAsync(task.TaskId);
            Assert.Equal(TaskStatus.Running, stored.Status);
            Assert.Equal(NodeB, stored.AssignedNode);
        }

        [Fact(DisplayName = "进度：未知任务丢弃（M-10）")]
        public async Task ApplyWorkerProgress_UnknownTask_IsRejected()
        {
            var result = await _service.ApplyWorkerProgressAsync(NodeA, "no-such-task", TaskStatus.Running);

            Assert.Equal(WorkerReportOutcome.TaskNotFound, result.Outcome);
            Assert.Null(result.Task);
            Assert.Empty(await _dbContext.CrawlerTasks.ToListAsync());
        }

        [Fact(DisplayName = "进度：终态任务收到迟到的 Running/Failed 被忽略（M-10）")]
        public async Task ApplyWorkerProgress_TerminalTask_IgnoresLateProgress()
        {
            var task = await AddTaskAsync(TaskStatus.Completed, NodeA);

            Assert.Equal(WorkerReportOutcome.Ignored, (await _service.ApplyWorkerProgressAsync(NodeA, task.TaskId, TaskStatus.Running)).Outcome);
            Assert.Equal(WorkerReportOutcome.Ignored, (await _service.ApplyWorkerProgressAsync(NodeA, task.TaskId, TaskStatus.Failed)).Outcome);
            Assert.Equal(TaskStatus.Completed, (await ReloadAsync(task.TaskId)).Status);
        }

        [Fact(DisplayName = "进度：被重置为 Pending 的任务，原节点重连后继续上报即认领为 Running，调度器不再重复下发")]
        public async Task ApplyWorkerProgress_ResetTask_IsAdoptedByReportingNode()
        {
            var task = await AddTaskAsync(TaskStatus.Pending);

            var result = await _service.ApplyWorkerProgressAsync(NodeA, task.TaskId, TaskStatus.Running);

            Assert.Equal(WorkerReportOutcome.Accepted, result.Outcome);
            var stored = await ReloadAsync(task.TaskId);
            Assert.Equal(TaskStatus.Running, stored.Status);
            Assert.Equal(NodeA, stored.AssignedNode);
            Assert.False(await _service.TryMarkTaskDispatchedAsync(task.TaskId, NodeB), "已被认领的任务不应再被下发");
        }

        [Fact(DisplayName = "进度：只接受当前执行者的 Failed；被重置后旧执行的失败被忽略")]
        public async Task ApplyWorkerProgress_Failed_OnlyFromCurrentExecutor()
        {
            var running = await AddTaskAsync(TaskStatus.Running, NodeA);
            var reset = await AddTaskAsync(TaskStatus.Pending);

            Assert.Equal(WorkerReportOutcome.Accepted, (await _service.ApplyWorkerProgressAsync(NodeA, running.TaskId, TaskStatus.Failed)).Outcome);
            Assert.Equal(WorkerReportOutcome.Ignored, (await _service.ApplyWorkerProgressAsync(NodeA, reset.TaskId, TaskStatus.Failed)).Outcome);

            Assert.Equal(TaskStatus.Failed, (await ReloadAsync(running.TaskId)).Status);
            Assert.Equal(TaskStatus.Pending, (await ReloadAsync(reset.TaskId)).Status);
        }
        #endregion

        #region 结果落库（M-04②、M-11、M-12）
        [Fact(DisplayName = "结果：落库与 Completed 同时生效（M-11）")]
        public async Task SaveCrawlResult_StoresResultAndCompletesTask()
        {
            var task = await AddTaskAsync(TaskStatus.Running, NodeA);

            var result = await _service.SaveCrawlResultAsync(NodeA, NewResult(task.TaskId, pages: 3));

            Assert.Equal(WorkerReportOutcome.Accepted, result.Outcome);
            Assert.Equal(TaskStatus.Completed, (await ReloadAsync(task.TaskId)).Status);
            Assert.Equal(1, await CountResultsAsync(task.TaskId));
            Assert.Equal(3, await _dbContext.PageResults.CountAsync());
        }

        [Fact(DisplayName = "结果：同一任务的第二份结果被丢弃，库中只有一份（M-04②）")]
        public async Task SaveCrawlResult_SecondResultIsDuplicate()
        {
            var task = await AddTaskAsync(TaskStatus.Running, NodeA);
            await _service.SaveCrawlResultAsync(NodeA, NewResult(task.TaskId));
            _dbContext.ChangeTracker.Clear();

            var second = await _service.SaveCrawlResultAsync(NodeA, NewResult(task.TaskId));

            Assert.Equal(WorkerReportOutcome.Duplicate, second.Outcome);
            Assert.Equal(1, await CountResultsAsync(task.TaskId));
        }

        [Fact(DisplayName = "结果：任务已分配给其他节点时丢弃（M-04①）")]
        public async Task SaveCrawlResult_FromOtherNode_IsRejected()
        {
            var task = await AddTaskAsync(TaskStatus.Running, NodeB);

            var result = await _service.SaveCrawlResultAsync(NodeA, NewResult(task.TaskId));

            Assert.Equal(WorkerReportOutcome.NotAssignedToNode, result.Outcome);
            Assert.Equal(0, await CountResultsAsync(task.TaskId));
            Assert.Equal(TaskStatus.Running, (await ReloadAsync(task.TaskId)).Status);
        }

        [Fact(DisplayName = "结果：未知任务丢弃")]
        public async Task SaveCrawlResult_UnknownTask_IsRejected()
        {
            var result = await _service.SaveCrawlResultAsync(NodeA, NewResult("no-such-task"));

            Assert.Equal(WorkerReportOutcome.TaskNotFound, result.Outcome);
            Assert.Equal(0, await _dbContext.CrawlResults.CountAsync());
        }

        [Fact(DisplayName = "结果：因落库失败被标为 Failed 的任务，再次收到结果时改为 Completed（为 Worker 重发预留）")]
        public async Task SaveCrawlResult_FailedTaskFromAssignedNode_IsCompleted()
        {
            var task = await AddTaskAsync(TaskStatus.Running, NodeA);
            Assert.True(await _service.MarkTaskFailedAsync(task.TaskId, NodeA));
            Assert.Equal(TaskStatus.Failed, (await ReloadAsync(task.TaskId)).Status);

            var result = await _service.SaveCrawlResultAsync(NodeA, NewResult(task.TaskId));

            Assert.Equal(WorkerReportOutcome.Accepted, result.Outcome);
            Assert.Equal(TaskStatus.Completed, (await ReloadAsync(task.TaskId)).Status);
        }

        [Fact(DisplayName = "结果：MarkTaskFailed 不影响已完成或属于其他节点的任务（M-12）")]
        public async Task MarkTaskFailed_OnlyUnfinishedTasksOfThatNode()
        {
            var done = await AddTaskAsync(TaskStatus.Completed, NodeA);
            var other = await AddTaskAsync(TaskStatus.Running, NodeB);

            Assert.False(await _service.MarkTaskFailedAsync(done.TaskId, NodeA));
            Assert.False(await _service.MarkTaskFailedAsync(other.TaskId, NodeA));
            Assert.Equal(TaskStatus.Completed, (await ReloadAsync(done.TaskId)).Status);
            Assert.Equal(TaskStatus.Running, (await ReloadAsync(other.TaskId)).Status);
        }

        [Fact(DisplayName = "唯一索引：同一 TaskId 的第二份结果在数据库层被拒绝（M-04②）")]
        public async Task CrawlResultTaskId_HasUniqueIndex()
        {
            _dbContext.CrawlResults.Add(new CrawlResult { TaskId = "dup", CompletedAt = DateTime.UtcNow });
            await _dbContext.SaveChangesAsync();

            _dbContext.CrawlResults.Add(new CrawlResult { TaskId = "dup", CompletedAt = DateTime.UtcNow });
            await Assert.ThrowsAsync<DbUpdateException>(() => _dbContext.SaveChangesAsync());
        }
        #endregion

        #region 故障恢复与重启恢复（M-02①②）
        [Fact(DisplayName = "故障恢复：重置分配给该节点的 Pending 与 Running 任务，不动其他节点和终态任务")]
        public async Task ResetUnfinishedTasksOfNode_ResetsPendingAndRunningOfThatNode()
        {
            var running = await AddTaskAsync(TaskStatus.Running, NodeA);
            var queued = await AddTaskAsync(TaskStatus.Pending, NodeA);
            var completed = await AddTaskAsync(TaskStatus.Completed, NodeA);
            var otherNode = await AddTaskAsync(TaskStatus.Running, NodeB);

            var reset = await _service.ResetUnfinishedTasksOfNodeAsync(NodeA);

            Assert.Equal(new[] { queued.TaskId, running.TaskId }.OrderBy(x => x), reset.Select(t => t.TaskId).OrderBy(x => x));
            foreach (var id in new[] { running.TaskId, queued.TaskId })
            {
                var stored = await ReloadAsync(id);
                Assert.Equal(TaskStatus.Pending, stored.Status);
                Assert.Equal(string.Empty, stored.AssignedNode);
            }
            Assert.Equal(TaskStatus.Completed, (await ReloadAsync(completed.TaskId)).Status);
            Assert.Equal(NodeB, (await ReloadAsync(otherNode.TaskId)).AssignedNode);

            Assert.Empty(await _service.ResetUnfinishedTasksOfNodeAsync(NodeA));
        }

        [Fact(DisplayName = "重启恢复：所有 Pending 与执行节点不在线的 Running 被重置；在线节点上的 Running 保留")]
        public async Task ResetTasksForStartupRecovery_SelectsRecoverableTasks()
        {
            _dbContext.NodeInfos.AddRange(
                new NodeInfo { NodeId = NodeA, Status = NodeStatus.Online },
                new NodeInfo { NodeId = NodeB, Status = NodeStatus.Offline });
            await _dbContext.SaveChangesAsync();
            _dbContext.ChangeTracker.Clear();

            var pendingUnassigned = await AddTaskAsync(TaskStatus.Pending);
            var pendingAssigned = await AddTaskAsync(TaskStatus.Pending, NodeA);
            var runningOnOffline = await AddTaskAsync(TaskStatus.Running, NodeB);
            var runningOnMissing = await AddTaskAsync(TaskStatus.Running, "node-gone");
            var runningOnOnline = await AddTaskAsync(TaskStatus.Running, NodeA);
            var completed = await AddTaskAsync(TaskStatus.Completed, NodeB);

            var recovered = await _service.ResetTasksForStartupRecoveryAsync();

            var expected = new[] { pendingUnassigned, pendingAssigned, runningOnOffline, runningOnMissing }.Select(t => t.TaskId).OrderBy(x => x);
            Assert.Equal(expected, recovered.Select(t => t.TaskId).OrderBy(x => x));
            Assert.All(recovered, t =>
            {
                Assert.Equal(TaskStatus.Pending, t.Status);
                Assert.Equal(string.Empty, t.AssignedNode);
            });
            Assert.Equal(TaskStatus.Running, (await ReloadAsync(runningOnOnline.TaskId)).Status);
            Assert.Equal(TaskStatus.Completed, (await ReloadAsync(completed.TaskId)).Status);
        }

        [Fact(DisplayName = "负载均衡统计：按节点统计已分配且未结束的任务数（M-06）")]
        public async Task GetUnfinishedTaskCountsByNode_CountsPendingAndRunning()
        {
            await AddTaskAsync(TaskStatus.Running, NodeA);
            await AddTaskAsync(TaskStatus.Pending, NodeA);
            await AddTaskAsync(TaskStatus.Completed, NodeA);
            await AddTaskAsync(TaskStatus.Running, NodeB);
            await AddTaskAsync(TaskStatus.Pending);

            var counts = await _service.GetUnfinishedTaskCountsByNodeAsync();

            Assert.Equal(2, counts[NodeA]);
            Assert.Equal(1, counts[NodeB]);
            Assert.Equal(2, counts.Count);
        }
        #endregion
    }
}
