using System;
using System.Collections.Concurrent;

namespace CrawlerMaster.WPF.Services
{
    /// <summary>
    /// 节点会话管理类，使用 ConcurrentDictionary 保证线程安全。
    /// </summary>
    public class SessionManager
    {
        private readonly ConcurrentDictionary<string, NodeSession> _sessions = new();

        /// <summary>
        /// 注册一个新节点，生成唯一会话标识。
        /// </summary>
        /// <param name="nodeId">节点唯一标识</param>
        /// <returns>
        /// (success: true, sessionId: 新会话ID) 如果节点不存在；
        /// (success: false, sessionId: null) 如果节点已存在。
        /// </returns>
        public (bool success, string? sessionId) RegisterNode(string nodeId)
        {
            if (string.IsNullOrWhiteSpace(nodeId))
                throw new ArgumentException("节点ID不能为空", nameof(nodeId));

            var session = new NodeSession
            {
                NodeId = nodeId,
                SessionId = Guid.NewGuid().ToString(),
                RegisteredAt = DateTime.UtcNow
            };

            // TryAdd 是原子操作：如果 key 不存在则添加，返回 true；否则 false
            bool added = _sessions.TryAdd(nodeId, session);
            return added ? (true, session.SessionId) : (false, null);
        }

        /// <summary>
        /// 验证指定节点的会话是否有效。
        /// </summary>
        /// <param name="nodeId">节点ID</param>
        /// <param name="sessionId">待验证的会话ID</param>
        /// <returns>true 表示会话有效，false 表示无效或节点不存在</returns>
        public bool ValidateSession(string nodeId, string sessionId)
        {
            if (string.IsNullOrWhiteSpace(nodeId) || string.IsNullOrWhiteSpace(sessionId))
                return false;

            if (_sessions.TryGetValue(nodeId, out var session))
            {
                return session.SessionId == sessionId;
            }
            return false;
        }

        /// <summary>
        /// 移除节点的会话记录（如节点主动下线或超时）。
        /// </summary>
        /// <param name="nodeId">节点ID</param>
        public void RemoveSession(string nodeId)
        {
            if (!string.IsNullOrWhiteSpace(nodeId))
                _sessions.TryRemove(nodeId, out _);
        }

        /// <summary>
        /// 内部使用的会话实体
        /// </summary>
        private class NodeSession
        {
            public string NodeId { get; set; } = string.Empty;
            public string SessionId { get; set; } = string.Empty;
            public DateTime RegisteredAt { get; set; }
        }
    }
}