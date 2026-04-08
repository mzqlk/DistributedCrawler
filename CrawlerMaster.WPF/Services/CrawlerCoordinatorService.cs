using CrawlerCore.Infrastructure;
using CrawlerCore.Infrastructure.Constants;
using CrawlerCore.Protos;
using Grpc.Core;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Threading;

namespace CrawlerMaster.WPF.Services
{
    /// <summary>
    /// gRPC协调服务实现，严格对齐proto协议，仅通信层能力，无业务逻辑侵入
    /// 修复生命周期、异常隔离、空引用等所有致命问题
    /// </summary>
    public class CrawlerCoordinatorService : CrawlerCoordinator.CrawlerCoordinatorBase, IDisposable
    {
        private readonly SessionManager _sessionManager;
        private readonly GrpcSettings _grpcSettings;
        private readonly ILogger<CrawlerCoordinatorService> _logger;
        private readonly ConcurrentDictionary<string, NodeStreamContext> _nodeStreamContexts = new();
        private readonly object _eventLock = new();
        private bool _disposed = false;

        #region 预留事件（线程安全，异常隔离）
        private Action<NodeRegistration>? _nodeRegistered;
        public event Action<NodeRegistration>? NodeRegistered
        {
            add { lock (_eventLock) _nodeRegistered += value; }
            remove { lock (_eventLock) _nodeRegistered -= value; }
        }

        private Action<string, string>? _streamConnected;
        public event Action<string, string>? StreamConnected
        {
            add { lock (_eventLock) _streamConnected += value; }
            remove { lock (_eventLock) _streamConnected -= value; }
        }

        private Action<string, string>? _streamDisconnected;
        public event Action<string, string>? StreamDisconnected
        {
            add { lock (_eventLock) _streamDisconnected += value; }
            remove { lock (_eventLock) _streamDisconnected -= value; }
        }

        private Action<string, Heartbeat>? _heartbeatReceived;
        public event Action<string, Heartbeat>? HeartbeatReceived
        {
            add { lock (_eventLock) _heartbeatReceived += value; }
            remove { lock (_eventLock) _heartbeatReceived -= value; }
        }

        private Action<string, LogEntry>? _logEntryReceived;
        public event Action<string, LogEntry>? LogEntryReceived
        {
            add { lock (_eventLock) _logEntryReceived += value; }
            remove { lock (_eventLock) _logEntryReceived -= value; }
        }

        private Action<string, CrawlResult>? _crawlResultReceived;
        public event Action<string, CrawlResult>? CrawlResultReceived
        {
            add { lock (_eventLock) _crawlResultReceived += value; }
            remove { lock (_eventLock) _crawlResultReceived -= value; }
        }

        private Action<string, TaskProgress>? _taskProgressReceived;
        public event Action<string, TaskProgress>? TaskProgressReceived
        {
            add { lock (_eventLock) _taskProgressReceived += value; }
            remove { lock (_eventLock) _taskProgressReceived -= value; }
        }
        #endregion

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
        /// 处理Worker节点的注册请求，严格对齐原项目流程
        /// </summary>
        public override Task<RegistrationResponse> RegisterNode(NodeRegistration request, ServerCallContext context)
        {
            _logger.LogInformation($"收到节点注册请求: NodeId={request.NodeId}, MachineName={request.MachineName}, IP={request.IpAddress}");

            if (string.IsNullOrWhiteSpace(request.NodeId))
            {
                _logger.LogWarning("节点注册失败：NodeId为空");
                return Task.FromResult(new RegistrationResponse
                {
                    Success = false,
                    Message = "NodeId cannot be null or empty"
                });
            }

            var (success, sessionId) = _sessionManager.RegisterNode(request.NodeId);
            if (!success)
            {
                _logger.LogWarning($"节点注册失败：节点 {request.NodeId} 已存在活跃会话");
                return Task.FromResult(new RegistrationResponse
                {
                    Success = false,
                    Message = "Node already has an active session"
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
                SessionId = sessionId!,
                SystemConfig = configResponse,
                Message = "Registration successful"
            };

            // 线程安全触发事件，异常隔离
            var handler = Volatile.Read(ref _nodeRegistered);
            if (handler != null)
            {
                foreach (Action<NodeRegistration> subscriber in handler.GetInvocationList())
                {
                    try
                    {
                        subscriber.Invoke(request);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"节点注册事件订阅者执行异常");
                    }
                }
            }

            _logger.LogInformation($"节点 {request.NodeId} 注册成功，会话ID: {sessionId}");
            return Task.FromResult(response);
        }

        /// <summary>
        /// 处理Worker节点的双向流通信，严格对齐proto协议
        /// 修复生命周期管理、资源释放、已释放对象访问问题
        /// </summary>
        public override async Task StartStreaming(
            IAsyncStreamReader<FromWorker> requestStream,
            IServerStreamWriter<FromMaster> responseStream,
            ServerCallContext context)
        {
            // 1. 会话校验
            var headers = context.RequestHeaders;
            var nodeId = headers.GetValue("node-id");
            var sessionId = headers.GetValue("session-id");

            if (string.IsNullOrEmpty(nodeId) || string.IsNullOrEmpty(sessionId))
            {
                _logger.LogWarning("双向流建立失败：缺少 node-id 或 session-id");
                throw new RpcException(new Status(StatusCode.PermissionDenied, "Missing node-id or session-id"));
            }

            if (!_sessionManager.ValidateSession(nodeId, sessionId))
            {
                _logger.LogWarning($"双向流建立失败：节点 {nodeId} 的会话 {sessionId} 无效");
                throw new RpcException(new Status(StatusCode.PermissionDenied, "Invalid session"));
            }

            // 2. 初始化流资源，手动管理生命周期，修复using释放问题
            var streamCts = new CancellationTokenSource();
            var sendChannel = System.Threading.Channels.Channel.CreateUnbounded<FromMaster>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });

            var streamContext = new NodeStreamContext(
                nodeId,
                sessionId,
                responseStream,
                sendChannel,
                streamCts);

            // 3. 原子操作绑定流上下文，禁止重复建流
            if (!_nodeStreamContexts.TryAdd(nodeId, streamContext))
            {
                streamCts.Cancel();
                streamCts.Dispose();
                sendChannel.Writer.TryComplete();
                _logger.LogWarning($"节点 {nodeId} 已存在活跃流，拒绝重复建流");
                throw new RpcException(new Status(StatusCode.AlreadyExists, "Node already has an active stream"));
            }

            // 4. 绑定上下文取消令牌
            using var registration = context.CancellationToken.Register(() =>
            {
                try { streamCts.Cancel(); }
                catch { /* 忽略取消异常 */ }
            });

            _logger.LogInformation($"节点 {nodeId} 成功建立双向流通道");
            // 线程安全触发事件
            var connectedHandler = Volatile.Read(ref _streamConnected);
            if (connectedHandler != null)
            {
                foreach (Action<string, string> subscriber in connectedHandler.GetInvocationList())
                {
                    try
                    {
                        subscriber.Invoke(nodeId, sessionId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"流连接事件订阅者执行异常");
                    }
                }
            }
            _sessionManager.UpdateSessionLastActiveAt(nodeId);

            try
            {
                // 5. 读写分离双循环
                var readTask = StartReadLoopAsync(requestStream, nodeId, streamCts.Token);
                var writeTask = StartWriteLoopAsync(streamContext, streamCts.Token);

                await Task.WhenAny(readTask, writeTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation($"节点 {nodeId} 的双向流因取消而终止");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"节点 {nodeId} 的双向流发生异常");
                throw;
            }
            finally
            {
                // 6. 统一资源清理，严格保证时序
                try { streamCts.Cancel(); } catch { }
                sendChannel.Writer.TryComplete();
                _nodeStreamContexts.TryRemove(nodeId, out _);
                _sessionManager.UpdateSessionLastActiveAt(nodeId);

                // 触发流断开事件
                var disconnectedHandler = Volatile.Read(ref _streamDisconnected);
                if (disconnectedHandler != null)
                {
                    foreach (Action<string, string> subscriber in disconnectedHandler.GetInvocationList())
                    {
                        try
                        {
                            subscriber.Invoke(nodeId, sessionId);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"流断开事件订阅者执行异常");
                        }
                    }
                }

                streamCts.Dispose();
                _logger.LogInformation($"节点 {nodeId} 的双向流已关闭，所有资源已清理");
            }
        }

        /// <summary>
        /// 线程安全向指定节点发送消息，全异常隔离
        /// </summary>
        public async Task<bool> SendMessageToNodeAsync(string nodeId, FromMaster message)
        {
            if (_disposed)
            {
                _logger.LogWarning("发送消息失败：服务已释放");
                return false;
            }

            if (string.IsNullOrWhiteSpace(nodeId) || message == null)
            {
                _logger.LogWarning("发送消息失败：节点ID或消息为空");
                return false;
            }

            // 原子化校验会话与流状态
            if (!_nodeStreamContexts.TryGetValue(nodeId, out var streamContext)
                || !_sessionManager.ValidateSession(nodeId, streamContext.SessionId)
                || streamContext.Cts.IsCancellationRequested
                || streamContext.SendChannel.Reader.Completion.IsCompleted)
            {
                _logger.LogWarning($"发送消息失败：节点 {nodeId} 无有效会话或活跃流");
                return false;
            }

            try
            {
                await streamContext.SendChannel.Writer.WriteAsync(message, streamContext.Cts.Token).ConfigureAwait(false);
                _logger.LogDebug($"消息已入队节点 {nodeId} 发送队列，消息类型: {message.MessageCase}");
                return true;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning($"向节点 {nodeId} 发送消息被取消");
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"向节点 {nodeId} 发送消息失败");
                return false;
            }
        }

        /// <summary>
        /// 读循环：接收Worker上报的所有消息
        /// </summary>
        private async Task StartReadLoopAsync(
            IAsyncStreamReader<FromWorker> requestStream,
            string nodeId,
            CancellationToken cancellationToken)
        {
            await foreach (var message in requestStream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                _sessionManager.UpdateSessionLastActiveAt(nodeId);

                switch (message.MessageCase)
                {
                    case FromWorker.MessageOneofCase.Heartbeat:
                        var hb = message.Heartbeat;
                        _logger.LogInformation(
                            $"收到节点 {nodeId} 心跳: CPU={hb.CpuUsage:F2}%, 内存={hb.MemoryUsageMb:F2}MB, 活跃任务={hb.ActiveTasks}");
                        var hbHandler = Volatile.Read(ref _heartbeatReceived);
                        if (hbHandler != null)
                        {
                            foreach (Action<string, Heartbeat> subscriber in hbHandler.GetInvocationList())
                            {
                                try
                                {
                                    subscriber.Invoke(nodeId, hb);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, $"心跳事件订阅者执行异常");
                                }
                            }
                        }
                        break;

                    case FromWorker.MessageOneofCase.LogEntry:
                        var log = message.LogEntry;
                        _logger.LogInformation(
                            $"收到节点 {nodeId} 日志: Level={log.LogLevel}, TaskId={log.TaskId}, Message={log.Message}");
                        var logHandler = Volatile.Read(ref _logEntryReceived);
                        if (logHandler != null)
                        {
                            foreach (Action<string, LogEntry> subscriber in logHandler.GetInvocationList())
                            {
                                try
                                {
                                    subscriber.Invoke(nodeId, log);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, $"日志事件订阅者执行异常");
                                }
                            }
                        }
                        break;

                    case FromWorker.MessageOneofCase.CrawlResult:
                        var result = message.CrawlResult;
                        _logger.LogInformation(
                            $"收到节点 {nodeId} 爬取结果: TaskId={result.TaskId}, 页面数={result.PageResults.Count}");
                        var resultHandler = Volatile.Read(ref _crawlResultReceived);
                        if (resultHandler != null)
                        {
                            foreach (Action<string, CrawlResult> subscriber in resultHandler.GetInvocationList())
                            {
                                try
                                {
                                    subscriber.Invoke(nodeId, result);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, $"爬取结果事件订阅者执行异常");
                                }
                            }
                        }
                        break;

                    case FromWorker.MessageOneofCase.TaskProgress:
                        var progress = message.TaskProgress;
                        _logger.LogInformation(
                            $"收到节点 {nodeId} 任务进度: TaskId={progress.TaskId}, 已爬取={progress.CrawledPages}/{progress.TotalPages}, 状态={progress.CurrentStatus}");
                        var progressHandler = Volatile.Read(ref _taskProgressReceived);
                        if (progressHandler != null)
                        {
                            foreach (Action<string, TaskProgress> subscriber in progressHandler.GetInvocationList())
                            {
                                try
                                {
                                    subscriber.Invoke(nodeId, progress);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, $"任务进度事件订阅者执行异常");
                                }
                            }
                        }
                        break;

                    default:
                        _logger.LogWarning($"节点 {nodeId} 发送了未知类型消息: {message.MessageCase}");
                        break;
                }
            }
        }

        /// <summary>
        /// 写循环：单线程消费队列，避免并发写入异常
        /// </summary>
        private async Task StartWriteLoopAsync(
            NodeStreamContext streamContext,
            CancellationToken cancellationToken)
        {
            await foreach (var message in streamContext.SendChannel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await streamContext.ResponseStream.WriteAsync(message, cancellationToken).ConfigureAwait(false);
                    _logger.LogDebug($"向节点 {streamContext.NodeId} 成功发送消息，类型: {message.MessageCase}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"向节点 {streamContext.NodeId} 写入消息失败");
                    throw;
                }
            }
        }

        /// <summary>
        /// 节点流上下文，封装单个节点的流资源
        /// </summary>
        private class NodeStreamContext
        {
            public string NodeId { get; }
            public string SessionId { get; }
            public IServerStreamWriter<FromMaster> ResponseStream { get; }
            public Channel<FromMaster> SendChannel { get; }
            public CancellationTokenSource Cts { get; }

            public NodeStreamContext(
                string nodeId,
                string sessionId,
                IServerStreamWriter<FromMaster> responseStream,
                Channel<FromMaster> sendChannel,
                CancellationTokenSource cts)
            {
                NodeId = nodeId;
                SessionId = sessionId;
                ResponseStream = responseStream;
                SendChannel = sendChannel;
                Cts = cts;
            }
        }

        /// <summary>
        /// 资源释放，取消所有事件订阅，避免内存泄漏
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;

            lock (_eventLock)
            {
                _nodeRegistered = null;
                _streamConnected = null;
                _streamDisconnected = null;
                _heartbeatReceived = null;
                _logEntryReceived = null;
                _crawlResultReceived = null;
                _taskProgressReceived = null;
            }

            // 关闭所有活跃流
            foreach (var streamContext in _nodeStreamContexts.Values)
            {
                try { streamContext.Cts.Cancel(); } catch { }
                streamContext.SendChannel.Writer.TryComplete();
                streamContext.Cts.Dispose();
            }
            _nodeStreamContexts.Clear();

            _disposed = true;
            GC.SuppressFinalize(this);
        }

        ~CrawlerCoordinatorService()
        {
            Dispose();
        }
    }
}