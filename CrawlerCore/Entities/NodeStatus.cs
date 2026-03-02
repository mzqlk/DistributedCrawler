namespace CrawlerCore.Entities
{
    /// <summary>
    /// 工作节点状态枚举
    /// </summary>
    public enum NodeStatus
    {
        /// <summary>
        /// 待连接（初始状态）
        /// </summary>
        Pending,
        /// <summary>
        /// 在线（正常运行）
        /// </summary>
        Online,
        /// <summary>
        /// 离线（超时/断连）
        /// </summary>
        Offline
    }
}