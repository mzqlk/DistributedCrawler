using CrawlerCore.Infrastructure.Constants;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CrawlerMaster.WPF.Services
{
    /// <summary>
    /// 节点会话管理：NodeId ↔ SessionId，以及注册时声明的并发上限。线程安全。
    /// 作为托管服务运行时定期清理过期会话（有活跃流的会话不清理）。
    /// 流断开时不移除会话：Worker 短暂断线后会用旧会话重连。
    /// </summary>
    public class SessionManager : BackgroundService
    {
        private readonly ConcurrentDictionary<string, NodeSession> _sessions = new();
        private readonly ILogger<SessionManager> _logger;

        public SessionManager(ILogger<SessionManager> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// 判断节点当前是否有活跃流，由协调服务设置；过期清理会跳过有活跃流的会话。
        /// 用委托而不是直接依赖协调服务，避免两者循环依赖
        /// </summary>
        public Func<string, bool>? ActiveStreamProbe { get; set; }

        /// <summary>
        /// 注册一个新节点：已有未过期的会话时拒绝（只用于没有流信息的场景，协调服务注册节点用 <see cref="ReplaceSession"/>）
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
                    _sessions.TryRemove(new KeyValuePair<string, NodeSession>(nodeId, existingSession));
                    _logger.LogInformation("已移除节点 {NodeId} 的过期会话，允许重新注册", nodeId);
                }
                else
                {
                    _logger.LogWarning("节点 {NodeId} 已存在活跃会话，拒绝重复注册", nodeId);
                    return (false, null);
                }
            }

            var session = NodeSession.Create(nodeId, 0);
            bool added = _sessions.TryAdd(nodeId, session);
            if (added)
            {
                _logger.LogInformation("节点注册成功，NodeId={NodeId}", nodeId);
            }
            return added ? (true, session.SessionId) : (false, null);
        }

        /// <summary>
        /// 为节点创建新会话；已有会话时直接替换（旧会话随即失效）。
        /// 调用方负责先确认该节点没有活跃流，避免两个 NodeId 相同的 Worker 互相踢下线
        /// </summary>
        /// <param name="nodeId">节点ID</param>
        /// <param name="maxConcurrentTasks">节点声明的并发任务上限，0 表示不限</param>
        /// <returns>新会话ID</returns>
        public string ReplaceSession(string nodeId, int maxConcurrentTasks = 0)
        {
            if (string.IsNullOrWhiteSpace(nodeId))
                throw new ArgumentException("节点ID不能为空", nameof(nodeId));

            var session = NodeSession.Create(nodeId, Math.Max(0, maxConcurrentTasks));
            var replaced = false;
            _sessions.AddOrUpdate(nodeId, session, (_, _) =>
            {
                replaced = true;
                return session;
            });

            _logger.LogInformation(replaced
                ? "节点 {NodeId} 重新注册，旧会话已替换，并发上限 {MaxConcurrentTasks}"
                : "节点 {NodeId} 注册成功，并发上限 {MaxConcurrentTasks}", nodeId, session.MaxConcurrentTasks);
            return session.SessionId;
        }

        /// <summary>
        /// 验证指定节点的会话是否有效
        /// </summary>
        public bool ValidateSession(string nodeId, string sessionId)
        {
            if (string.IsNullOrWhiteSpace(nodeId) || string.IsNullOrWhiteSpace(sessionId))
                return false;

            return _sessions.TryGetValue(nodeId, out var session)
                   && session.SessionId.Equals(sessionId, StringComparison.Ordinal);
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
                session.Touch();
                return true;
            }
            return false;
        }

        /// <summary>
        /// 节点注册时声明的并发任务上限，0 表示不限或节点没有会话
        /// </summary>
        public int GetMaxConcurrentTasks(string nodeId)
            => !string.IsNullOrWhiteSpace(nodeId) && _sessions.TryGetValue(nodeId, out var session) ? session.MaxConcurrentTasks : 0;

        /// <summary>
        /// 移除节点的会话记录（节点注销时调用）
        /// </summary>
        public void RemoveSession(string nodeId)
        {
            if (!string.IsNullOrWhiteSpace(nodeId) && _sessions.TryRemove(nodeId, out _))
            {
                _logger.LogInformation("节点会话已移除，NodeId={NodeId}", nodeId);
            }
        }

        /// <summary>
        /// 只在会话仍是指定会话时移除（例如注册落库失败时回滚自己刚创建的会话，不影响并发注册产生的新会话）
        /// </summary>
        public bool RemoveSession(string nodeId, string sessionId)
        {
            if (string.IsNullOrWhiteSpace(nodeId)
                || !_sessions.TryGetValue(nodeId, out var session)
                || !session.SessionId.Equals(sessionId, StringComparison.Ordinal))
            {
                return false;
            }

            var removed = _sessions.TryRemove(new KeyValuePair<string, NodeSession>(nodeId, session));
            if (removed)
            {
                _logger.LogInformation("节点会话已移除，NodeId={NodeId}", nodeId);
            }
            return removed;
        }

        /// <summary>
        /// 获取所有在线会话的节点ID
        /// </summary>
        public string[] GetAllOnlineNodeIds()
        {
            return _sessions.Keys.ToArray();
        }

        /// <summary>
        /// 清理在 <paramref name="utcNow"/> 时已过期、且没有活跃流的会话
        /// </summary>
        /// <returns>清理的会话数</returns>
        public int CleanupExpiredSessions(DateTime utcNow)
        {
            var expiredTime = utcNow - TimeSpan.FromMinutes(GrpcConstants.DefaultSessionExpiredMinutes);
            var probe = ActiveStreamProbe;
            var removed = 0;

            foreach (var (nodeId, session) in _sessions)
            {
                if (session.LastActiveAt >= expiredTime) continue;
                if (probe != null && probe(nodeId)) continue;

                if (_sessions.TryRemove(new KeyValuePair<string, NodeSession>(nodeId, session)))
                {
                    removed++;
                    _logger.LogInformation("已自动清理过期会话，NodeId={NodeId}", nodeId);
                }
            }
            return removed;
        }

        /// <summary>
        /// 后台定时清理过期会话
        /// </summary>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("会话过期清理服务已启动");
            using var cleanupTimer = new PeriodicTimer(TimeSpan.FromMinutes(GrpcConstants.DefaultSessionCleanupIntervalMinutes));

            try
            {
                while (await cleanupTimer.WaitForNextTickAsync(stoppingToken))
                {
                    try
                    {
                        CleanupExpiredSessions(DateTime.UtcNow);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "清理过期会话失败");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("会话过期清理服务已停止");
            }
        }

        /// <summary>
        /// 内部会话实体。SessionId 与并发上限创建后不变；最后活跃时间用 Interlocked 读写
        /// </summary>
        private sealed class NodeSession
        {
            private long _lastActiveTicks;

            private NodeSession(string nodeId, string sessionId, int maxConcurrentTasks)
            {
                NodeId = nodeId;
                SessionId = sessionId;
                MaxConcurrentTasks = maxConcurrentTasks;
                _lastActiveTicks = DateTime.UtcNow.Ticks;
            }

            public string NodeId { get; }
            public string SessionId { get; }
            public int MaxConcurrentTasks { get; }
            public DateTime LastActiveAt => new(Interlocked.Read(ref _lastActiveTicks), DateTimeKind.Utc);

            public void Touch() => Interlocked.Exchange(ref _lastActiveTicks, DateTime.UtcNow.Ticks);

            public static NodeSession Create(string nodeId, int maxConcurrentTasks)
                => new(nodeId, Guid.NewGuid().ToString(), maxConcurrentTasks);
        }
    }
}
