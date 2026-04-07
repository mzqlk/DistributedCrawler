using CrawlerCore.Infrastructure;
using CrawlerCore.Protos;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Threading.Tasks;

namespace CrawlerMaster.WPF.Services
{
    /// <summary>
    /// gRPC 协调服务实现，处理 Worker 节点的注册与双向流通信。
    /// </summary>
    public class CrawlerCoordinatorService : CrawlerCoordinator.CrawlerCoordinatorBase
    {
        private readonly SessionManager _sessionManager;
        private readonly GrpcSettings _grpcSettings;
        private readonly ILogger<CrawlerCoordinatorService> _logger;

        /// <summary>
        /// 节点注册成功时触发的事件，用于通知 UI 更新。
        /// </summary>
        public event Action<NodeRegistration>? NodeRegistered;

        public CrawlerCoordinatorService(
            SessionManager sessionManager,
            IOptions<GrpcSettings> grpcSettingsOptions,
            ILogger<CrawlerCoordinatorService> logger)
        {
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
            _grpcSettings = grpcSettingsOptions?.Value ?? throw new ArgumentNullException(nameof(grpcSettingsOptions));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// 处理 Worker 节点的注册请求。
        /// </summary>
        /// <param name="request">节点注册请求，包含节点 ID、机器名、CPU 核心数等信息。</param>
        /// <param name="context">gRPC 服务调用上下文。</param>
        /// <returns>注册响应，包含成功标志、会话 ID 及系统配置。</returns>
        public override Task<RegistrationResponse> RegisterNode(NodeRegistration request, ServerCallContext context)
        {
            _logger.LogInformation("收到节点注册请求: NodeId={NodeId}, MachineName={MachineName}, IP={IpAddress}",
                request.NodeId, request.MachineName, request.IpAddress);

            if (string.IsNullOrWhiteSpace(request.NodeId))
            {
                _logger.LogWarning("节点注册失败：NodeId 为空");
                return Task.FromResult(new RegistrationResponse
                {
                    Success = false,
                    Message = "NodeId cannot be null or empty"
                });
            }

            var (success, sessionId) = _sessionManager.RegisterNode(request.NodeId);
            if (!success)
            {
                _logger.LogWarning("节点注册失败：节点 {NodeId} 已存在或注册冲突", request.NodeId);
                return Task.FromResult(new RegistrationResponse
                {
                    Success = false,
                    Message = "Node already registered or registration conflict"
                });
            }

            var configResponse = new SystemConfig
            {
                HeartbeatIntervalSeconds = _grpcSettings.HeartbeatIntervalSeconds,
                MaxRetryAttempts = _grpcSettings.MaxRetryAttempts,
                RetryDelayMilliseconds = _grpcSettings.RetryDelayMilliseconds
            };

            var response = new RegistrationResponse
            {
                Success = true,
                SessionId = sessionId,
                SystemConfig = configResponse,
                Message = "Registration successful"
            };

            NodeRegistered?.Invoke(request);
            _logger.LogInformation("节点 {NodeId} 注册成功，会话ID: {SessionId}", request.NodeId, sessionId);
            return Task.FromResult(response);
        }

        /// <summary>
        /// 处理 Worker 节点的双向流通信。
        /// </summary>
        /// <param name="requestStream">Worker 发送的消息流（来自 Worker）。</param>
        /// <param name="responseStream">Master 发送的消息流（去向 Worker）。</param>
        /// <param name="context">gRPC 服务调用上下文，包含请求元数据和取消令牌。</param>
        public override async Task StartStreaming(
            IAsyncStreamReader<FromWorker> requestStream,
            IServerStreamWriter<FromMaster> responseStream,
            ServerCallContext context)
        {
            // 1. 从请求元数据中获取 node-id 和 session-id
            var headers = context.RequestHeaders;
            var nodeId = headers.GetValue("node-id");
            var sessionId = headers.GetValue("session-id");

            if (string.IsNullOrEmpty(nodeId) || string.IsNullOrEmpty(sessionId))
            {
                _logger.LogWarning("双向流建立失败：缺少 node-id 或 session-id");
                throw new RpcException(new Status(StatusCode.PermissionDenied, "Missing node-id or session-id"));
            }

            // 2. 验证会话有效性
            if (!_sessionManager.ValidateSession(nodeId, sessionId))
            {
                _logger.LogWarning("双向流建立失败：节点 {NodeId} 的会话 {SessionId} 无效", nodeId, sessionId);
                throw new RpcException(new Status(StatusCode.PermissionDenied, "Invalid session"));
            }

            _logger.LogInformation("节点 {NodeId} 成功建立双向流通道", nodeId);

            try
            {
                // 3. 异步读取 Worker 发送的所有消息
                await foreach (var message in requestStream.ReadAllAsync(context.CancellationToken))
                {
                    switch (message.MessageCase)
                    {
                        case FromWorker.MessageOneofCase.Heartbeat:
                            var hb = message.Heartbeat;
                            _logger.LogInformation(
                                "节点 {NodeId} 心跳: CPU={CpuUsage:F2}%, 内存={MemoryUsageMb:F2}MB, 活跃任务={ActiveTasks}, 时间戳={Timestamp}",
                                nodeId, hb.CpuUsage, hb.MemoryUsageMb, hb.ActiveTasks, hb.Timestamp);
                            break;

                        case FromWorker.MessageOneofCase.LogEntry:
                            var log = message.LogEntry;
                            _logger.LogInformation(
                                "节点 {NodeId} 日志: Level={LogLevel}, TaskId={TaskId}, Message={Message}, Exception={Exception}",
                                nodeId, log.LogLevel, log.TaskId, log.Message, log.ExceptionDetail);
                            break;

                        case FromWorker.MessageOneofCase.CrawlResult:
                            var result = message.CrawlResult;
                            _logger.LogInformation(
                                "节点 {NodeId} 返回爬取结果: TaskId={TaskId}, 页面数={PageCount}, 完成时间={CompletedAt}",
                                nodeId, result.TaskId, result.PageResults.Count, result.CompletedAt);
                            break;

                        case FromWorker.MessageOneofCase.TaskProgress:
                            var progress = message.TaskProgress;
                            _logger.LogInformation(
                                "节点 {NodeId} 任务进度: TaskId={TaskId}, 已爬取={CrawledPages}/{TotalPages}, 状态={Status}, 错误={Error}",
                                nodeId, progress.TaskId, progress.CrawledPages, progress.TotalPages,
                                progress.CurrentStatus, progress.ErrorMessage);
                            break;

                        default:
                            _logger.LogWarning("节点 {NodeId} 发送了未知类型消息: {MessageCase}", nodeId, message.MessageCase);
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("节点 {NodeId} 的双向流因取消而终止", nodeId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "节点 {NodeId} 的双向流发生异常", nodeId);
            }
            finally
            {
                // 4. 流结束时清理会话
                _sessionManager.RemoveSession(nodeId);
                _logger.LogInformation("节点 {NodeId} 的双向流已关闭，会话已移除", nodeId);
            }
        }

        /// <summary>
        /// 预留的向指定节点发送消息的方法（暂未实现具体发送逻辑）。
        /// </summary>
        /// <param name="nodeId">目标节点 ID。</param>
        /// <param name="message">要发送的消息（FromMaster 类型）。</param>
        /// <returns>表示异步操作的任务。</returns>
        /// <exception cref="NotImplementedException">始终抛出，表示尚未实现。</exception>
        public Task SendMessageToNodeAsync(string nodeId, FromMaster message)
        {
            throw new NotImplementedException("发送消息到节点功能尚未实现");
        }
    }
}