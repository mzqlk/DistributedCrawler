using CrawlerCore.Entities;
using System;

namespace CrawlerCore.Events
{
    /// <summary>
    /// 节点状态变更事件参数
    /// </summary>
    public class NodeStatusChangedEventArgs : EventArgs
    {
        public string NodeId { get; }
        public NodeStatus OldStatus { get; }
        public NodeStatus NewStatus { get; }
        public NodeInfo NodeInfo { get; }
        public DateTime ChangedTime { get; } = DateTime.UtcNow;

        public NodeStatusChangedEventArgs(string nodeId, NodeStatus oldStatus, NodeStatus newStatus, NodeInfo nodeInfo)
        {
            NodeId = nodeId;
            OldStatus = oldStatus;
            NewStatus = newStatus;
            NodeInfo = nodeInfo;
        }
    }
}