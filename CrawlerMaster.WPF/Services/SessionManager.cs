using CrawlerCore.Infrastructure.Constants;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CrawlerMaster.WPF.Services
{
    /// <summary>
    /// 节点会话管理类，线程安全，带过期会话自动清理
    /// 严格对齐原项目流程，修复内存泄漏、重注册失败问题
    /// </summary>
    public class SessionManager : BackgroundService
    {
        private readonly ConcurrentDictionary<string, NodeSession> _sessions = new();
        private readonly object _sessionLock = new();
        private readonly ILogger<SessionManager> _logger;

        public SessionManager(ILogger<SessionManager> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// 注册一个新节点，生成唯一会话标识
        /// </summary>
        public (bool success, string? sessionId) RegisterNode(string nodeId)
        {
            if (string.IsNullOrWhiteSpace(nodeId))
                throw new ArgumentException("节点ID不能为空", nameof(nodeId));

            // 先移除已存在的过期会话，允许节点重注册
            if (_sessions.TryGetValue(nodeId, out var existingSession))
            {
                var isExpired = DateTime.UtcNow - existingSession.LastActiveAt > TimeSpan.FromMinutes(GrpcConstants.DefaultSessionExpiredMinutes);
                if (isExpired)
                {
                    _sessions.TryRemove(nodeId, out _);
                    _logger.LogInformation($"已移除节点 {nodeId} 的过期会话，允许重新注册");
                }
                else
                {
                    _logger.LogWarning($"节点 {nodeId} 已存在活跃会话，拒绝重复注册");
                    return (false, null);
                }
            }

            var session = new NodeSession
            {
                NodeId = nodeId,
                SessionId = Guid.NewGuid().ToString(),
                RegisteredAt = DateTime.UtcNow,
                LastActiveAt = DateTime.UtcNow
            };

            bool added = _sessions.TryAdd(nodeId, session);
            if (added)
            {
                _logger.LogInformation($"节点注册成功，NodeId={nodeId}，SessionId={session.SessionId}");
            }
            return added ? (true, session.SessionId) : (false, null);
        }

        /// <summary>
        /// 验证指定节点的会话是否有效
        /// </summary>
        public bool ValidateSession(string nodeId, string sessionId)
        {
            if (string.IsNullOrWhiteSpace(nodeId) || string.IsNullOrWhiteSpace(sessionId))
                return false;

            if (_sessions.TryGetValue(nodeId, out var session))
            {
                lock (_sessionLock)
                {
                    return session.SessionId.Equals(sessionId, StringComparison.Ordinal);
                }
            }
            return false;
        }

        /// <summary>
        /// 线程安全更新节点会话最后活跃时间（UTC）
        /// </summary>
        public bool UpdateSessionLastActiveAt(string nodeId)
        {
            if (string.IsNullOrWhiteSpace(nodeId))
                return false;

            if (_sessions.TryGetValue(nodeId, out var session))
            {
                lock (_sessionLock)
                {
                    session.LastActiveAt = DateTime.UtcNow;
                }
                return true;
            }
            return false;
        }

        /// <summary>
        /// 移除节点的会话记录（节点注销时调用）
        /// </summary>
        public void RemoveSession(string nodeId)
        {
            if (!string.IsNullOrWhiteSpace(nodeId))
            {
                if (_sessions.TryRemove(nodeId, out _))
                {
                    _logger.LogInformation($"节点会话已移除，NodeId={nodeId}");
                }
            }
        }

        /// <summary>
        /// 获取所有在线会话的节点ID
        /// </summary>
        public string[] GetAllOnlineNodeIds()
        {
            return _sessions.Keys.ToArray();
        }

        /// <summary>
        /// 后台定时清理过期会话
        /// </summary>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("会话过期清理服务已启动");
            var cleanupTimer = new PeriodicTimer(TimeSpan.FromMinutes(GrpcConstants.DefaultSessionCleanupIntervalMinutes));

            try
            {
                while (await cleanupTimer.WaitForNextTickAsync(stoppingToken))
                {
                    var expiredTime = DateTime.UtcNow - TimeSpan.FromMinutes(GrpcConstants.DefaultSessionExpiredMinutes);
                    var expiredNodes = _sessions.Where(kv => kv.Value.LastActiveAt < expiredTime).Select(kv => kv.Key).ToArray();

                    foreach (var nodeId in expiredNodes)
                    {
                        _sessions.TryRemove(nodeId, out _);
                        _logger.LogInformation($"已自动清理过期会话，NodeId={nodeId}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("会话过期清理服务已停止");
            }
            finally
            {
                cleanupTimer.Dispose();
            }
        }

        /// <summary>
        /// 内部会话实体，线程安全访问
        /// </summary>
        private class NodeSession
        {
            public string NodeId { get; set; } = string.Empty;
            public string SessionId { get; set; } = string.Empty;
            public DateTime RegisteredAt { get; set; }
            public DateTime LastActiveAt { get; set; }
        }
    }
}