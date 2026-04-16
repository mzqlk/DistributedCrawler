using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CrawlerCore.Events;
using CrawlerCore.Infrastructure;
using CrawlerCore.Interfaces;
using CrawlerCore.Protos;
using CrawlerMaster.WPF.Services;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using Microsoft.Extensions.DependencyInjection;

namespace CrawlerCore.Tests.Grpc
{
    /// <summary>
    /// CrawlerCoordinatorService.RegisterNode 方法的单元测试
    /// </summary>
    public class GrpcMasterRegistrationTests : IDisposable
    {
        private readonly Server _server;
        private readonly Channel _channel;
        private readonly CrawlerCoordinator.CrawlerCoordinatorClient _client;
        private readonly SessionManager _sessionManager;
        private readonly CrawlerCoordinatorService _service;
        private readonly GrpcSettings _grpcSettings;
        private const string ValidToken = "test-valid-token";

        public GrpcMasterRegistrationTests()
        {
            // 配置 gRPC 设置
            _grpcSettings = new GrpcSettings
            {
                HeartbeatIntervalSeconds = 30,
                MaxRetryAttempts = 3,
                RetryDelayMilliseconds = 500,
                MasterEndpoint = "127.0.0.1:0" // 动态端口
            };
            var options = Options.Create(_grpcSettings);
            _sessionManager = new SessionManager(NullLogger<SessionManager>.Instance);

            // 创建服务实例（注意：此处使用 NullLogger，事件测试时使用真实实例）
            var mockNodeMgmt = new Mock<INodeManagementService>();
            var mockEventAgg = new Mock<IEventAggregator>();
            // 使用真实的 ServiceProvider，以保证 CreateScope/GetRequiredService 可用
            var services = new ServiceCollection();
            services.AddSingleton<INodeManagementService>(mockNodeMgmt.Object);
            var serviceProvider = services.BuildServiceProvider();

            _service = new CrawlerCoordinatorService(
                _sessionManager,
                mockNodeMgmt.Object,
                options,
                NullLogger<CrawlerCoordinatorService>.Instance,
                mockEventAgg.Object,
                serviceProvider);

            // 创建 gRPC 服务器，并添加认证拦截器
            _server = new Server
            {
                Ports = { new ServerPort("127.0.0.1", 0, ServerCredentials.Insecure) },
                Services =
                {
                    CrawlerCoordinator.BindService(_service)
                        .Intercept(new TestAuthInterceptor(ValidToken))
                }
            };
            _server.Start();

            // 创建客户端通道
            var port = _server.Ports.First().BoundPort;
            _channel = new Channel($"127.0.0.1:{port}", ChannelCredentials.Insecure);
            _client = new CrawlerCoordinator.CrawlerCoordinatorClient(_channel);
        }

        public void Dispose()
        {
            _channel?.ShutdownAsync().Wait();
            _server?.ShutdownAsync().Wait();
        }

        #region 测试用例

        /// <summary>
        /// 测试合法注册请求应返回成功并包含会话ID
        /// </summary>
        [Fact]
        public async Task RegisterNode_ValidRequest_ReturnsSuccessAndSessionId()
        {
            // Arrange
            var request = new NodeRegistration
            {
                NodeId = "node-001",
                MachineName = "TestMachine",
                IpAddress = "192.168.1.100",
                CpuCores = 8,
                TotalMemoryGb = 16
            };
            var headers = new Metadata { { "authorization", $"Bearer {ValidToken}" } };

            // Act
            var response = await _client.RegisterNodeAsync(request, headers).ResponseAsync;

            // Assert
            Assert.True(response.Success);
            Assert.False(string.IsNullOrWhiteSpace(response.SessionId));
            Assert.Equal("Registration successful", response.Message);
            Assert.NotNull(response.SystemConfig);
            Assert.Equal(_grpcSettings.HeartbeatIntervalSeconds, response.SystemConfig.HeartbeatIntervalSeconds);
            Assert.Equal(_grpcSettings.MaxRetryAttempts, response.SystemConfig.MaxRetryAttempts);
            Assert.Equal(_grpcSettings.RetryDelayMilliseconds, response.SystemConfig.RetryDelayMilliseconds);
        }

        /// <summary>
        /// 测试无效 Token（未认证）应返回 Unauthenticated 状态
        /// </summary>
        [Fact]
        public async Task RegisterNode_InvalidToken_ReturnsUnauthenticated()
        {
            // Arrange
            var request = new NodeRegistration
            {
                NodeId = "node-002",
                MachineName = "TestMachine",
                IpAddress = "192.168.1.101"
            };
            var headers = new Metadata { { "authorization", "Bearer invalid-token" } };

            // Act & Assert
            var ex = await Assert.ThrowsAsync<RpcException>(
                async () => await _client.RegisterNodeAsync(request, headers).ResponseAsync);
            Assert.Equal(StatusCode.Unauthenticated, ex.Status.StatusCode);
        }

        /// <summary>
        /// 测试缺少认证头也应返回 Unauthenticated
        /// </summary>
        [Fact]
        public async Task RegisterNode_MissingAuthHeader_ReturnsUnauthenticated()
        {
            // Arrange
            var request = new NodeRegistration
            {
                NodeId = "node-003",
                MachineName = "TestMachine",
                IpAddress = "192.168.1.102"
            };
            var headers = new Metadata(); // 无 authorization 头

            // Act & Assert
            var ex = await Assert.ThrowsAsync<RpcException>(
                async () => await _client.RegisterNodeAsync(request, headers).ResponseAsync);
            Assert.Equal(StatusCode.Unauthenticated, ex.Status.StatusCode);
        }

        /// <summary>
        /// 测试重复注册同一 NodeId 应返回失败
        /// </summary>
        [Fact]
        public async Task RegisterNode_DuplicateNodeId_ReturnsFailure()
        {
            // Arrange
            const string nodeId = "node-dup-001";
            var request1 = new NodeRegistration
            {
                NodeId = nodeId,
                MachineName = "FirstMachine",
                IpAddress = "192.168.1.200"
            };
            var request2 = new NodeRegistration
            {
                NodeId = nodeId,
                MachineName = "SecondMachine",
                IpAddress = "192.168.1.201"
            };
            var headers = new Metadata { { "authorization", $"Bearer {ValidToken}" } };

            // Act
            var response1 = await _client.RegisterNodeAsync(request1, headers).ResponseAsync;
            var response2 = await _client.RegisterNodeAsync(request2, headers).ResponseAsync;

            // Assert
            Assert.True(response1.Success);
            Assert.False(response2.Success);
            Assert.Equal("Node already has an active session", response2.Message);
        }

        /// <summary>
        /// 测试节点注册成功时触发 NodeRegistered 事件
        /// </summary>
        [Fact]
        public async Task RegisterNode_FiresNodeRegisteredEvent()
        {
            // Arrange
            // 创建一个新的事件可测试的服务实例
            var mockNodeMgmt = new Mock<INodeManagementService>();
            var mockEventAgg = new Mock<IEventAggregator>();
            // 使用真实 ServiceProvider
            var services = new ServiceCollection();
            services.AddSingleton<INodeManagementService>(mockNodeMgmt.Object);
            var serviceProvider = services.BuildServiceProvider();
            var eventService = new CrawlerCoordinatorService(
    new SessionManager(NullLogger<SessionManager>.Instance),
    mockNodeMgmt.Object,
    Options.Create(_grpcSettings),
    NullLogger<CrawlerCoordinatorService>.Instance,
    mockEventAgg.Object,
    serviceProvider);
            NodeRegistration receivedRegistration = null;
            var tcs = new TaskCompletionSource<bool>();

            eventService.NodeRegistered += registration =>
            {
                receivedRegistration = registration;
                tcs.SetResult(true);
            };

            // 创建独立的服务器和客户端
            var server = new Server
            {
                Ports = { new ServerPort("127.0.0.1", 0, ServerCredentials.Insecure) },
                Services =
                {
                    CrawlerCoordinator.BindService(eventService)
                        .Intercept(new TestAuthInterceptor(ValidToken))
                }
            };
            server.Start();
            var port = server.Ports.First().BoundPort;
            var channel = new Channel($"127.0.0.1:{port}", ChannelCredentials.Insecure);
            var client = new CrawlerCoordinator.CrawlerCoordinatorClient(channel);
            var request = new NodeRegistration
            {
                NodeId = "event-node",
                MachineName = "EventMachine",
                IpAddress = "10.0.0.1"
            };
            var headers = new Metadata { { "authorization", $"Bearer {ValidToken}" } };

            // Act
            var response = await client.RegisterNodeAsync(request, headers).ResponseAsync;

            // Assert
            Assert.True(response.Success);
            await tcs.Task.TimeoutAfter(TimeSpan.FromSeconds(5)); // 等待事件触发
            Assert.NotNull(receivedRegistration);
            Assert.Equal(request.NodeId, receivedRegistration.NodeId);
            Assert.Equal(request.MachineName, receivedRegistration.MachineName);
            Assert.Equal(request.IpAddress, receivedRegistration.IpAddress);

            // 清理：显式 shutdown（避免使用不支持 IDisposable 的 using）
            await channel.ShutdownAsync();
            await server.ShutdownAsync();
        }

        #endregion
    }

    /// <summary>
    /// 测试专用的认证拦截器，检查 authorization 头是否为 Bearer {ValidToken}
    /// </summary>
    public class TestAuthInterceptor : Interceptor
    {
        private readonly string _validToken;

        public TestAuthInterceptor(string validToken)
        {
            _validToken = validToken;
        }

        public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
            TRequest request,
            ServerCallContext context,
            UnaryServerMethod<TRequest, TResponse> continuation)
        {
            var headers = context.RequestHeaders;
            var authHeader = headers?.Get("authorization")?.Value;
            if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer "))
            {
                throw new RpcException(new Status(StatusCode.Unauthenticated, "Missing or invalid token"));
            }
            var token = authHeader.Substring("Bearer ".Length);
            if (token != _validToken)
            {
                throw new RpcException(new Status(StatusCode.Unauthenticated, "Invalid token"));
            }
            return await continuation(request, context);
        }
    }

    /// <summary>
    /// 辅助扩展方法，用于异步超时控制
    /// </summary>
    public static class TaskExtensions
    {
        public static async Task TimeoutAfter(this Task task, TimeSpan timeout)
        {
            if (await Task.WhenAny(task, Task.Delay(timeout)) != task)
                throw new TimeoutException("操作超时");
            await task;
        }
    }
}