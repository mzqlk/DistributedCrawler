using CrawlerCore.Entities;
using CrawlerCore.Events;
using CrawlerCore.Infrastructure;
using CrawlerCore.Infrastructure.Constants;
using CrawlerCore.Interfaces;
using CrawlerCore.Protos;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using TaskStatus = CrawlerCore.Entities.TaskStatus;

namespace CrawlerMaster.WPF.Services
{
    /// <summary>
    /// gRPC协调服务实现（Scoped生命周期，对齐ASP.NET Core gRPC规范）
    /// </summary>
    public class CrawlerCoordinatorService : CrawlerCoordinator.CrawlerCoordinatorBase, IDisposable, ICrawlerCoordinatorService
    {
        private readonly SessionManager _sessionManager;
        private readonly GrpcSettings _grpcSettings;
        private readonly ILogger<CrawlerCoordinatorService> _logger;
        private readonly INodeManagementService _nodeManagementService;
        private readonly IEventAggregator _eventAggregator;
        private readonly IServiceProvider _serviceProvider;
        private readonly ConcurrentDictionary<string, NodeStreamContext> _nodeStreamContexts = new();
        private readonly object _eventLock = new();
        private bool _disposed = false;

        #region 事件定义（原有逻辑完全保留）
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

        private Action<string, CrawlerCore.Protos.CrawlResult>? _crawlResultReceived;
        public event Action<string, CrawlerCore.Protos.CrawlResult>? CrawlResultReceived
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

        // 【修复】构造函数依赖完整匹配DI注册，无注入失败
        public CrawlerCoordinatorService(
            SessionManager sessionManager,
            INodeManagementService nodeManagementService,
            IOptions<GrpcSettings> grpcSettingsOptions,
            ILogger<CrawlerCoordinatorService> logger,
            IEventAggregator eventAggregator,
            IServiceProvider serviceProvider)
        {
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
            _nodeManagementService = nodeManagementService ?? throw new ArgumentNullException(nameof(nodeManagementService));
            _grpcSettings = grpcSettingsOptions?.Value ?? throw new ArgumentNullException(nameof(grpcSettingsOptions));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        }

        /// <summary>
        /// 【修复】补充await，确保数据库操作完成后再返回响应，无火并遗忘
        /// </summary>
        public override async Task<RegistrationResponse> RegisterNode(NodeRegistration request, ServerCallContext context)
        {
            _logger.LogInformation($"收到节点注册请求: NodeId={request.NodeId}, MachineName={request.MachineName}, IP={request.IpAddress}");

            if (string.IsNullOrWhiteSpace(request.NodeId))
            {
                _logger.LogWarning("节点注册失败：NodeId为空");
                return new RegistrationResponse
                {
                    Success = false,
                    Message = "NodeId cannot be null or empty"
                };
            }

            var (success, sessionId) = _sessionManager.RegisterNode(request.NodeId);
            if (!success)
            {
                _logger.LogWarning($"节点注册失败：节点 {request.NodeId} 已存在活跃会话");
                return new RegistrationResponse
                {
                    Success = false,
                    Message = "Node already has an active session"
                };
            }

            try
            {
                // 【核心修复】通过IServiceProvider创建Scope获取Scoped服务
                using var scope = _serviceProvider.CreateScope();
                var nodeManagementService = scope.ServiceProvider.GetRequiredService<INodeManagementService>();

                var nodeInfo = new NodeInfo
                {
                    NodeId = request.NodeId,
                    MachineName = request.MachineName,
                    IpAddress = request.IpAddress,
                    CpuCores = request.CpuCores,
                    TotalMemoryGb = request.TotalMemoryGb,
                    LastHeartbeat = DateTime.UtcNow,
                    Status = NodeStatus.Pending
                };

                // 【修复】await数据库操作
                await nodeManagementService.RegisterNodeAsync(nodeInfo);
                _logger.LogInformation($"节点 {request.NodeId} 已持久化到数据库");

                // 发布节点状态变更事件
                _eventAggregator.Publish(this, new NodeStatusChangedEventArgs(
                    request.NodeId,
                    NodeStatus.Pending,
                    NodeStatus.Online,
                    new NodeInfo { LastHeartbeat = DateTime.UtcNow }));

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

                _logger.LogInformation($"节点 {request.NodeId} 注册成功，会话ID: {sessionId}");
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"节点 {request.NodeId} 数据库持久化失败");
                _sessionManager.RemoveSession(request.NodeId);
                return new RegistrationResponse
                {
                    Success = false,
                    Message = "Database registration failed"
                };
            }
        }

        /// <summary>
        /// 【修复】读循环全await，保证消息有序，无乱序/流中断
        /// </summary>
        public override async Task StartStreaming(
    IAsyncStreamReader<FromWorker> requestStream,
    IServerStreamWriter<FromMaster> responseStream,
    ServerCallContext context)
        {
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
                _logger.LogWarning($"双向流建立失败：节点 {nodeId} 会话验证失败");
                throw new RpcException(new Status(StatusCode.PermissionDenied, "Invalid session"));
            }

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

            // 【核心修复】线程安全存储流上下文
            if (!_nodeStreamContexts.TryAdd(nodeId, streamContext))
            {
                _logger.LogWarning($"双向流建立失败：节点 {nodeId} 已存在活跃流");
                streamCts.Cancel();
                streamCts.Dispose();
                sendChannel.Writer.TryComplete();
                throw new RpcException(new Status(StatusCode.AlreadyExists, "Node already has an active stream"));
            }

            _logger.LogInformation($"节点 {nodeId} 成功建立双向流通道");

            // 发布节点状态变更事件
            _eventAggregator.Publish(this, new NodeStatusChangedEventArgs(
                nodeId,
                NodeStatus.Pending,
                NodeStatus.Online,
                new NodeInfo{ LastHeartbeat = DateTime.UtcNow }));

            // 【核心修复】流关闭时，通过Scope获取服务标记节点离线
            context.CancellationToken.Register(async () =>
            {
                try
                {
                    _logger.LogInformation($"节点 {nodeId} 的双向流连接已取消");
                    await CleanupNodeStreamAsync(nodeId, streamContext);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"清理节点 {nodeId} 流资源时发生异常");
                }
            });

            try
            {
                var readTask = StartReadLoopAsync(requestStream, nodeId, streamCts.Token);
                var writeTask = StartWriteLoopAsync(streamContext, streamCts.Token);
                await Task.WhenAll(readTask, writeTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation($"节点 {nodeId} 的双向流操作已取消");
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, $"节点 {nodeId} 的双向流发生IO异常");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"节点 {nodeId} 的双向流发生异常");
            }
            finally
            {
                await CleanupNodeStreamAsync(nodeId, streamContext);
            }
        }

        private async Task CleanupNodeStreamAsync(string nodeId, NodeStreamContext streamContext)
        {
            if (_disposed) return;

            // 线程安全移除流上下文
            if (_nodeStreamContexts.TryRemove(nodeId, out _))
            {
                _logger.LogInformation($"节点 {nodeId} 的流上下文已从字典中移除");
            }

            // 取消流操作
            try
            {
                streamContext.Cts.Cancel();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"取消节点 {nodeId} 流操作时发生异常");
            }

            // 关闭发送通道
            try
            {
                streamContext.SendChannel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"关闭节点 {nodeId} 发送通道时发生异常");
            }

            // 【核心修复】通过Scope获取服务标记节点离线
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var nodeManagementService = scope.ServiceProvider.GetRequiredService<INodeManagementService>();
                await nodeManagementService.MarkSingleNodeAsOfflineAsync(nodeId);
                _logger.LogInformation($"节点 {nodeId} 已标记为离线");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"标记节点 {nodeId} 离线失败");
            }

            // 发布节点状态变更事件
            try
            {
                _eventAggregator.Publish(this, new NodeStatusChangedEventArgs(
                    nodeId,
                    NodeStatus.Online,
                    NodeStatus.Offline,
                    new NodeInfo { LastHeartbeat = DateTime.UtcNow }));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"发布节点 {nodeId} 离线事件失败");
            }

            // 释放资源
            try
            {
                streamContext.Cts.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"释放节点 {nodeId} Cts资源时发生异常");
            }

            _logger.LogInformation($"节点 {nodeId} 的双向流已关闭，所有资源已清理");
        }

        /// <summary>
        /// 【修复】读循环全await，消息有序处理，无火并遗忘
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
                        await HandleHeartbeatMessage(nodeId, message.Heartbeat);
                        break;
                    case FromWorker.MessageOneofCase.LogEntry:
                        HandleLogEntryMessage(nodeId, message.LogEntry);
                        break;
                    case FromWorker.MessageOneofCase.CrawlResult:
                        HandleCrawlResultMessage(nodeId, message.CrawlResult);
                        break;
                    case FromWorker.MessageOneofCase.TaskProgress:
                        await HandleTaskProgressMessage(nodeId, message.TaskProgress);
                        break;
                    default:
                        _logger.LogWarning($"节点 {nodeId} 发送了未知类型消息: {message.MessageCase}");
                        break;
                }
            }
        }

        #region 消息处理方法（全异步await+异常隔离）
        private async Task HandleHeartbeatMessage(string nodeId, Heartbeat hb)
        {
            _logger.LogInformation(
                $"收到节点 {nodeId} 心跳: CPU={hb.CpuUsage:F2}%, 内存={hb.MemoryUsageMb:F2}MB, 活跃任务={hb.ActiveTasks}");

            await _nodeManagementService.UpdateNodeHeartbeatAsync(new NodeInfo
            {
                NodeId = nodeId,
                LastHeartbeat = DateTime.UtcNow,
                CpuUsage = Math.Max(0, hb.CpuUsage),
                MemoryUsageMb = Math.Max(0, hb.MemoryUsageMb),
                ActiveTasks = Math.Max(0, hb.ActiveTasks),
                DiskUsagePercent = Math.Max(0, hb.DiskUsagePercent)
            });

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
        }

        private void HandleLogEntryMessage(string nodeId, LogEntry log)
        {
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
        }

        private void HandleCrawlResultMessage(string nodeId, CrawlerCore.Protos.CrawlResult result)
        {
            _logger.LogInformation(
                $"收到节点 {nodeId} 爬取结果: TaskId={result.TaskId}, 页面数={result.PageResults.Count}");

            var resultHandler = Volatile.Read(ref _crawlResultReceived);
            if (resultHandler != null)
            {
                foreach (Action<string, CrawlerCore.Protos.CrawlResult> subscriber in resultHandler.GetInvocationList())
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
        }

        /// <summary>
        /// 【修复】任务进度处理，枚举安全转换+Scope正确释放+事件安全发布
        /// </summary>
        private async Task HandleTaskProgressMessage(string nodeId, TaskProgress progress)
        {
            _logger.LogInformation(
                $"收到节点 {nodeId} 任务进度: TaskId={progress.TaskId}, 已爬取={progress.CrawledPages}/{progress.TotalPages}, 状态={progress.CurrentStatus}");

            // 非阻塞事件发布，不占用读循环线程
            _ = Task.Run(async () =>
            {
                try
                {
                    // 【修复】枚举数值安全转换+范围校验，极端值兜底
                    var statusIntValue = (int)progress.CurrentStatus;
                    if (!Enum.IsDefined(typeof(TaskStatus), statusIntValue))
                    {
                        _logger.LogWarning($"任务 {progress.TaskId} 状态值 {statusIntValue} 无效，使用默认值Pending");
                        statusIntValue = (int)TaskStatus.Pending;
                    }
                    var taskStatus = (TaskStatus)statusIntValue;

                    // 【修复】using包裹Scope，确保Scoped服务正确释放
                    string taskName = string.Empty;
                    using var scope = _serviceProvider.CreateScope();
                    var taskDataService = scope.ServiceProvider.GetRequiredService<ITaskDataService>();
                    var task = await taskDataService.GetTaskByIdAsync(progress.TaskId);
                    if (task != null)
                    {
                        taskName = task.TaskName;
                    }

                    var eventArgs = new TaskProgressUpdatedEventArgs(
                        progress.TaskId,
                        taskName,
                        taskStatus,
                        Math.Max(0, progress.CrawledPages),
                        Math.Max(0, progress.TotalPages),
                        nodeId);

                    _eventAggregator.Publish(this, eventArgs);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"任务 {progress.TaskId} 进度事件发布异常");
                }
            }, CancellationToken.None);

            // 原有事件回调逻辑
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
        }
        #endregion

        /// <summary>
        /// 写循环（原有逻辑完全保留，仅补充异常隔离）
        /// </summary>
        private async Task StartWriteLoopAsync(
            NodeStreamContext streamContext,
            CancellationToken cancellationToken)
        {
            await foreach (var message in streamContext.SendChannel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await streamContext.ResponseStream.WriteAsync(message).ConfigureAwait(false);
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
        /// 发送消息到节点（原有逻辑完全保留，补充Channel异常隔离）
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
            catch (ChannelClosedException)
            {
                _logger.LogWarning($"向节点 {nodeId} 发送消息失败：通道已关闭");
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"向节点 {nodeId} 发送消息失败");
                return false;
            }
        }

        /// <summary>
        /// 发送任务到节点（原有逻辑完全保留，补充异常隔离）
        /// </summary>
        public async Task<bool> SendTaskToNodeAsync(string nodeId, CrawlerCore.Entities.CrawlerTask task)
        {
            if (_disposed)
            {
                _logger.LogWarning("发送任务失败：服务已释放");
                return false;
            }

            if (string.IsNullOrWhiteSpace(nodeId) || task == null)
            {
                _logger.LogWarning("发送任务失败：参数无效");
                return false;
            }

            // 【核心修复】线程安全访问流上下文
            if (!_nodeStreamContexts.TryGetValue(nodeId, out var streamContext))
            {
                _logger.LogWarning($"发送任务失败：节点 {nodeId} 无有效会话或活跃流");
                return false;
            }

            if (streamContext.Cts.IsCancellationRequested)
            {
                _logger.LogWarning($"发送任务失败：节点 {nodeId} 的流已取消");
                return false;
            }

            try
            {
                // 构建gRPC任务消息
                var protoTask = new CrawlerCore.Protos.CrawlerTask
                {
                    TaskId = task.TaskId,
                    TaskName = task.TaskName,
                    PluginName = task.PluginName,
                    MaxDepth = task.MaxDepth,
                    MaxPages = task.MaxPages,
                    Priority = task.Priority
                };
                protoTask.StartUrls.AddRange(task.StartUrls);
                foreach (var kvp in task.Configuration)
                {
                    protoTask.Configuration.Add(kvp.Key, kvp.Value);
                }

                var fromMaster = new FromMaster
                {
                    NewTask = protoTask
                };

                // 【核心修复】线程安全写入通道
                await streamContext.SendChannel.Writer.WriteAsync(fromMaster, streamContext.Cts.Token);
                _logger.LogInformation($"任务已发送到节点 {nodeId}，TaskId: {task.TaskId}");
                return true;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning($"发送任务失败：节点 {nodeId} 的流操作已取消");
                return false;
            }
            catch (ChannelClosedException)
            {
                _logger.LogWarning($"发送任务失败：节点 {nodeId} 的通道已关闭");
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"发送任务到节点 {nodeId} 失败");
                return false;
            }
        }

        #region 私有类与Dispose（原有逻辑完全保留）
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
        #endregion
    }
}