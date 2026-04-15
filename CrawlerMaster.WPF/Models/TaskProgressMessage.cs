using CrawlerCore.Entities;

namespace CrawlerMaster.WPF.Models;

/// <summary>
/// 任务进度实时推送消息
/// </summary>
public class TaskProgressMessage
{
    public string TaskId { get; set; } = string.Empty;
    public string TaskName { get; set; } = string.Empty;
    public CrawlerCore.Entities.TaskStatus Status { get; set; }
    public int CompletedPages { get; set; }
    public int TotalPages { get; set; }
    public double ProgressPercent => TotalPages == 0 ? 0 : Math.Round((double)CompletedPages / TotalPages * 100, 2);
    public string AssignedNode { get; set; } = string.Empty;
}