using CrawlerCore.Entities;
using CrawlerCore.Events;
using System.Threading;
using System.Threading.Tasks;

namespace CrawlerWorker.Services.Engine
{
    public interface ICrawlerEngine
    {
        /// <summary>核心爬取执行方法</summary>
        Task<CrawlResult> ExecuteAsync(CrawlerTask task, CancellationToken cancellationToken);

        /// <summary>进度更新事件（与具体类中的同名事件对应）</summary>
        event EventHandler<TaskProgressUpdatedEventArgs>? ProgressUpdated;
    }
}