namespace CrawlerCore.Entities
{
    /// <summary>
    /// 爬虫任务状态枚举
    /// </summary>
    public enum TaskStatus
    {
        /// <summary>
        /// 待执行（已入队，未分配）
        /// </summary>
        Pending,
        /// <summary>
        /// 运行中（已分配到节点，正在执行）
        /// </summary>
        Running,
        /// <summary>
        /// 已完成（正常执行结束）
        /// </summary>
        Completed,
        /// <summary>
        /// 失败（执行异常/节点离线导致中断）
        /// </summary>
        Failed
    }
}