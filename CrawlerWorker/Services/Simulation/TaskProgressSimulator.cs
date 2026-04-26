using CrawlerCore.Infrastructure;
using CrawlerCore.Protos;
using System;
using System.Threading;
using System.Threading.Tasks;
using TaskStatus = CrawlerCore.Protos.TaskStatus;

namespace CrawlerWorker.Services.Simulation
{
    /// <summary>
    /// 任务进度模拟器（仅DEBUG生效）
    /// </summary>
#if DEBUG
    public static class TaskProgressSimulator
    {
        private static readonly Random _random = new();

        /// <summary>
        /// 启动进度模拟
        /// </summary>
        /// <param name="taskId">任务ID</param>
        /// <param name="maxPages">最大页面数</param>
        /// <param name="progressCallback">进度回调（线程安全）</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>模拟任务</returns>
        public static async Task StartSimulationAsync(
            string taskId,
            int maxPages,
            Func<TaskProgress, Task> progressCallback,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(taskId) || progressCallback == null)
            {
                LogHelper.Warn("进度模拟启动失败：入参为空");
                return;
            }

            int totalPages = maxPages <= 0 ? 100 : maxPages;
            int crawledPages = 0;
            LogHelper.Info($"[模拟] 启动进度 TaskId={taskId}, 总页数={totalPages}");

            try
            {
                // 边界场景：单页任务立即完成
                if (totalPages == 1)
                {
                    crawledPages = 1;
                    var finishProgress = new TaskProgress
                    {
                        TaskId = taskId,
                        CrawledPages = totalPages,
                        TotalPages = totalPages,
                        CurrentStatus = TaskStatus.Completed
                    };
                    await progressCallback.Invoke(finishProgress).ConfigureAwait(false);
                    LogHelper.Info($"[模拟] 单页任务立即完成 TaskId={taskId}");
                    return;
                }

                // 多页任务正常模拟
                while (crawledPages < totalPages && !cancellationToken.IsCancellationRequested)
                {
                    crawledPages += _random.Next(1, 5);
                    crawledPages = Math.Min(crawledPages, totalPages);

                    var progress = new TaskProgress
                    {
                        TaskId = taskId,
                        CrawledPages = crawledPages,
                        TotalPages = totalPages,
                        CurrentStatus = crawledPages >= totalPages ? TaskStatus.Completed : TaskStatus.Running,
                        ErrorMessage = string.Empty
                    };

                    await progressCallback.Invoke(progress).ConfigureAwait(false);

                    // 仅在未完成时等待
                    if (crawledPages < totalPages)
                    {
                        await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
                    }
                }

                LogHelper.Info($"[模拟] 多页任务完成 TaskId={taskId}");
            }
            catch (OperationCanceledException)
            {
                LogHelper.Warn($"[模拟] 任务取消 TaskId={taskId}");
            }
            catch (Exception ex)
            {
                LogHelper.Error(ex, $"[模拟] 任务异常 TaskId={taskId}");
                var failProgress = new TaskProgress
                {
                    TaskId = taskId,
                    CrawledPages = crawledPages,
                    TotalPages = totalPages,
                    CurrentStatus = TaskStatus.Failed,
                    ErrorMessage = ex.Message
                };
                await progressCallback.Invoke(failProgress).ConfigureAwait(false);
            }
        }
    }
#endif
}