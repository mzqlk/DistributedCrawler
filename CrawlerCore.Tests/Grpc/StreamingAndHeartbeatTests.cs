using CrawlerCore.Infrastructure;
using CrawlerCore.Protos;
using CrawlerMaster.WPF.Services;
using CrawlerWorker.Communication;
using CrawlerWorker.Services;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace CrawlerCore.Tests.Grpc
{
    public class StreamingAndHeartbeatTests : IDisposable
    {
        private readonly SessionManager _sessionManager;
        private readonly NodeInfoCollector _nodeInfoCollector;
        private readonly GrpcClientManager _grpcClientManager;
        private readonly WorkerSessionManager _workerSessionManager;
        private readonly GrpcSettings _testGrpcSettings = new()
        {
            MasterEndpoint = "http://localhost:5001",
            HeartbeatIntervalSeconds = 1,
            MaxRetryAttempts = 3,
            RetryDelayMilliseconds = 100,
            MaxMessageSizeMb = 64
        };

        public StreamingAndHeartbeatTests()
        {
            _sessionManager = new SessionManager(Microsoft.Extensions.Logging.Abstractions.NullLogger<SessionManager>.Instance);
            _nodeInfoCollector = new NodeInfoCollector(Microsoft.Extensions.Logging.Abstractions.NullLogger<NodeInfoCollector>.Instance);
            _grpcClientManager = new GrpcClientManager(
                Options.Create(_testGrpcSettings),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<GrpcClientManager>.Instance,
                new GrpcClientAuthenticationInterceptor(Options.Create(_testGrpcSettings), Microsoft.Extensions.Logging.Abstractions.NullLogger<GrpcClientAuthenticationInterceptor>.Instance)
            );
            _workerSessionManager = new WorkerSessionManager(Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkerSessionManager>.Instance);
        }

        public void Dispose()
        {
            _grpcClientManager?.Dispose();
            _sessionManager?.Dispose();
        }

        #region 会话校验测试

        [Fact]
        public void ValidateSession_ValidSession_ReturnsTrue()
        {
            var nodeId = "test-node-001";
            var (success, sessionId) = _sessionManager.RegisterNode(nodeId);
            var result = _sessionManager.ValidateSession(nodeId, sessionId!);

            Assert.True(success);
            Assert.True(result);
        }

        [Fact]
        public void ValidateSession_InvalidSession_ReturnsFalse()
        {
            var nodeId = "test-node-002";
            _sessionManager.RegisterNode(nodeId);
            var result = _sessionManager.ValidateSession(nodeId, "invalid-session-id");

            Assert.False(result);
        }

        [Fact]
        public void ValidateSession_UnregisteredNode_ReturnsFalse()
        {
            var result = _sessionManager.ValidateSession("unregistered-node", "any-session-id");
            Assert.False(result);
        }

        [Fact]
        public void UpdateSessionLastActiveAt_ValidNode_ReturnsTrue()
        {
            var nodeId = "test-node-003";
            _sessionManager.RegisterNode(nodeId);
            var result = _sessionManager.UpdateSessionLastActiveAt(nodeId);

            Assert.True(result);
        }

        [Fact]
        public void RemoveSession_ValidNode_RemovesSuccessfully()
        {
            var nodeId = "test-node-004";
            _sessionManager.RegisterNode(nodeId);
            _sessionManager.RemoveSession(nodeId);
            var result = _sessionManager.ValidateSession(nodeId, "any-session-id");

            Assert.False(result);
        }

        #endregion

        #region 心跳消息体测试

        [Fact(Timeout = 5000)]
        public async Task CollectHeartbeatAsync_Message100PercentMatchesProtoDefinition()
        {
            var heartbeat = await _nodeInfoCollector.CollectHeartbeatAsync();

            Assert.NotEmpty(heartbeat.NodeId);
            Assert.InRange(heartbeat.CpuUsage, 0, 100);
            Assert.True(heartbeat.MemoryUsageMb >= 0);
            Assert.InRange(heartbeat.DiskUsagePercent, 0, 100);
            Assert.Equal(0, heartbeat.ActiveTasks);
            Assert.NotNull(heartbeat.Timestamp);
            Assert.Equal(DateTimeKind.Utc, heartbeat.Timestamp.ToDateTime().Kind);
        }

        [Fact]
        public void GetNodeId_ReturnsNonEmptyValue()
        {
            var nodeId = _nodeInfoCollector.GetNodeId();
            Assert.NotEmpty(nodeId);
        }

        #endregion

        #region Worker会话管理测试

        [Fact]
        public void SaveSession_ValidSuccessResponse_SetsRegisteredState()
        {
            var response = new RegistrationResponse
            {
                Success = true,
                SessionId = "test-session-001",
                SystemConfig = new SystemConfig { HeartbeatIntervalSeconds = 5 }
            };

            _workerSessionManager.SaveSession(response);

            Assert.True(_workerSessionManager.IsRegistered);
            Assert.Equal(response.SessionId, _workerSessionManager.SessionId);
            Assert.Equal(response.SystemConfig, _workerSessionManager.SystemConfig);
        }

        [Fact]
        public void SaveSession_FailedResponse_ClearsSession()
        {
            var response = new RegistrationResponse
            {
                Success = false,
                SessionId = "test-session-002",
                Message = "注册失败"
            };

            _workerSessionManager.SaveSession(response);

            Assert.False(_workerSessionManager.IsRegistered);
            Assert.Null(_workerSessionManager.SessionId);
            Assert.Null(_workerSessionManager.SystemConfig);
        }

        [Fact]
        public void ClearSession_ClearsAllState()
        {
            var response = new RegistrationResponse
            {
                Success = true,
                SessionId = "test-session-003"
            };
            _workerSessionManager.SaveSession(response);
            _workerSessionManager.ClearSession();

            Assert.False(_workerSessionManager.IsRegistered);
            Assert.Null(_workerSessionManager.SessionId);
            Assert.Null(_workerSessionManager.SystemConfig);
        }

        #endregion

        #region 线程安全测试

        [Fact]
        public void RegisterNode_ConcurrentCalls_NoRaceCondition()
        {
            var nodeId = "test-node-concurrent-001";
            var successCount = 0;
            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = 10 };

            Parallel.For(0, 100, parallelOptions, _ =>
            {
                var (success, _) = _sessionManager.RegisterNode(nodeId);
                if (success) Interlocked.Increment(ref successCount);
            });

            Assert.Equal(1, successCount);
        }

        #endregion

        #region 连通性检查测试

        [Fact(Timeout = 5000)]
        public async Task CheckMasterConnectivityAsync_InvalidEndpoint_ReturnsFalse()
        {
            using var invalidCts = new CancellationTokenSource(3000);
            var invalidSettings = Options.Create(new GrpcSettings
            {
                MasterEndpoint = "http://invalid-host:9999",
                MaxRetryAttempts = 1
            });
            using var invalidClientManager = new GrpcClientManager(
                invalidSettings,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<GrpcClientManager>.Instance,
                new GrpcClientAuthenticationInterceptor(invalidSettings, Microsoft.Extensions.Logging.Abstractions.NullLogger<GrpcClientAuthenticationInterceptor>.Instance)
            );

            var result = await invalidClientManager.CheckMasterConnectivityAsync(invalidCts.Token);
            Assert.False(result);
        }

        [Fact]
        public void ParseEndpoint_IPv6Address_ParsesCorrectly()
        {
            var settings = Options.Create(new GrpcSettings
            {
                MasterEndpoint = "http://[::1]:5001",
                MaxRetryAttempts = 1
            });
            using var clientManager = new GrpcClientManager(
                settings,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<GrpcClientManager>.Instance,
                new GrpcClientAuthenticationInterceptor(settings, Microsoft.Extensions.Logging.Abstractions.NullLogger<GrpcClientAuthenticationInterceptor>.Instance)
            );

            // 调用 .ToString() 验证解析不会抛出异常
            Assert.NotNull(clientManager.CoordinatorClient.ToString());
        }

        #endregion
    }
}