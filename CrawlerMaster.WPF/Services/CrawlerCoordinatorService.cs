using CrawlerCore.Entities;
using CrawlerCore.Events;
using CrawlerCore.Infrastructure;
using CrawlerCore.Interfaces;
using CrawlerCore.Protos;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using TaskStatus = CrawlerCore.Entities.TaskStatus;

namespace CrawlerMaster.WPF.Services
{
    /// <summary>
    /// gRPC 协调服务实现：节点注册、双向流（每个节点一条，读循环按到达顺序处理消息，写入经通道由单一写循环写出）。
    /// 任务状态的变化统一交给 <see cref="ITaskDataService"/> 的状态机；节点状态事件只由 <see cref="INodeManagementService"/> 在库中状态真正变化后发布。
    /// </summary>
    public class CrawlerCoordinatorService : CrawlerCoordinator.CrawlerCoordinatorBase, IDisposable, ICrawlerCoordinatorService
    {
        private readonly SessionManager _sessionManager;
        private readonly GrpcSettings _grpcSettings;
        private readonly ILogger<CrawlerCoordinatorService> _logger;
        private readonly IEventAggregator _eventAggregator;
        private readonly IServiceProvider _serviceProvider;
        private readonly ConcurrentDictionary<string, NodeStreamContext> _nodeStreamContexts = new();
        private readonly object _eventLock = new();
        private volatile bool _disposed = false;

        #region 事件定义
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

        public CrawlerCoordinatorService(
            SessionManager sessionManager,
            IOptions<GrpcSettings> grpcSettingsOptions,
            ILogger<CrawlerCoordinatorService> logger,
            IEventAggregator eventAggregator,
            IServiceProvider serviceProvider)
        {
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
            _grpcSettings = grpcSettingsOptions?.Value ?? throw new ArgumentNullException(nameof(grpcSettingsOptions));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

            // 会话过期清理不清理有活跃流的会话
            _sessionManager.ActiveStreamProbe = IsNodeConnected;
        }

        #region 节点注册
        /// <summary>
        /// 注册节点：节点没有活跃流时创建（或替换）会话，持久化节点信息，返回系统配置。
        /// 节点仍有活跃流时拒绝，防止两个 NodeId 相同的 Worker 互相踢下线；
        /// 崩溃重启的 Worker 旧流已经结束，可以立即重新注册
        /// </summary>
        public override async Task<RegistrationResponse> RegisterNode(NodeRegistration request, ServerCallContext context)
        {
            _logger.LogInformation("收到节点注册请求: NodeId={NodeId}, MachineName={MachineName}, IP={IpAddress}, 并发上限={MaxConcurrentTasks}",
                request.NodeId, request.MachineName, request.IpAddress, request.MaxConcurrentTasks);

            if (string.IsNullOrWhiteSpace(request.NodeId))
            {
                _logger.LogWarning("节点注册失败：NodeId为空");
                return new RegistrationResponse
                {
                    Success = false,
                    Message = "NodeId cannot be null or empty"
                };
            }

            if (IsNodeConnected(request.NodeId))
            {
                _logger.LogWarning("节点注册失败：节点 {NodeId} 仍有活跃的双向流（可能有两个 Worker 使用了相同的 NodeId）", request.NodeId);
                return new RegistrationResponse
                {
                    Success = false,
                    Message = "Node already has an active session"
                };
            }

            var sessionId = _sessionManager.ReplaceSession(request.NodeId, request.MaxConcurrentTasks);

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var nodeManagementService = scope.ServiceProvider.GetRequiredService<INodeManagementService>();

                await nodeManagementService.RegisterNodeAsync(new NodeInfo
                {
                    NodeId = request.NodeId,
                    MachineName = request.MachineName,
                    IpAddress = request.IpAddress,
                    CpuCores = request.CpuCores,
                    TotalMemoryGb = request.TotalMemoryGb,
                    LastHeartbeat = DateTime.UtcNow,
                    Status = NodeStatus.Pending
                });
                _logger.LogInformation("节点 {NodeId} 已持久化到数据库", request.NodeId);

                var nodeRegisteredHandler = Volatile.Read(ref _nodeRegistered);
                if (nodeRegisteredHandler != null)
                {
                    foreach (Action<NodeRegistration> subscriber in nodeRegisteredHandler.GetInvocationList())
                    {
                        try
                        {
                            subscriber.Invoke(request);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "执行 NodeRegistered 订阅者时发生异常");
                        }
                    }
                }

                _logger.LogInformation("节点 {NodeId} 注册成功", request.NodeId);
                return new RegistrationResponse
                {
                    Success = true,
                    SessionId = sessionId,
                    SystemConfig = new SystemConfig
                    {
                        HeartbeatIntervalSeconds = _grpcSettings.HeartbeatIntervalSeconds,
                        MaxRetryAttempts = _grpcSettings.MaxRetryAttempts,
                        RetryDelayMilliseconds = _grpcSettings.RetryDelayMilliseconds
                    },
                    Message = "Registration successful"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "节点 {NodeId} 数据库持久化失败", request.NodeId);
                _sessionManager.RemoveSession(request.NodeId, sessionId);
                return new RegistrationResponse
                {
                    Success = false,
                    Message = "Database registration failed"
                };
            }
        }
        #endregion

        #region 双向流
        /// <summary>
        /// 双向流：校验会话，登记流上下文（同一节点的旧流被新流替换并关闭），运行读写循环，结束时清理。
        /// 读、写循环任一结束（客户端断开、写入失败、读到超限消息、被健康检查断开），整条流随即关闭并返回
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
                _logger.LogWarning("双向流建立失败：节点 {NodeId} 会话验证失败", nodeId);
                throw new RpcException(new Status(StatusCode.PermissionDenied, "Invalid session"));
            }

            var streamCts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            var sendChannel = System.Threading.Channels.Channel.CreateUnbounded<FromMaster>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
            var streamContext = new NodeStreamContext(nodeId, sessionId, responseStream, sendChannel, streamCts);

            var replaced = AttachStream(streamContext);
            if (replaced != null)
            {
                // 同一会话的新流（Worker 认为旧连接已断开并重连），旧流多半是半开连接：关闭它，以新流为准
                _logger.LogWarning("节点 {NodeId} 重新建立双向流，关闭旧流", nodeId);
                replaced.Close();
            }

            _sessionManager.UpdateSessionLastActiveAt(nodeId);
            _logger.LogInformation("节点 {NodeId} 成功建立双向流通道", nodeId);

            try
            {
                // 立即发送响应头，Worker 据此确认流已被接受（会话有效、已登记），
                // 再重发未确认的结果、把失败计数清零（T5：W-02、W-03）
                await context.WriteResponseHeadersAsync(new Metadata()).ConfigureAwait(false);

                var readTask = StartReadLoopAsync(requestStream, nodeId, streamContext.Token);
                var writeTask = StartWriteLoopAsync(streamContext, streamContext.Token);

                var finished = await Task.WhenAny(readTask, writeTask).ConfigureAwait(false);
                // 任一循环结束都关闭整条流，另一个循环随之退出
                streamContext.Close();
                try
                {
                    await Task.WhenAll(readTask, writeTask).ConfigureAwait(false);
                }
                catch when (!finished.IsFaulted)
                {
                    // 先结束的循环是正常结束或被取消的，另一个循环因取消抛出的异常不是断开原因，忽略
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("节点 {NodeId} 的双向流已取消", nodeId);
            }
            catch (IOException ex)
            {
                _logger.LogInformation("节点 {NodeId} 的双向流连接已断开: {Message}", nodeId, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "节点 {NodeId} 的双向流发生异常", nodeId);
            }
            finally
            {
                await CleanupNodeStreamAsync(streamContext).ConfigureAwait(false);
                streamCts.Dispose();
            }
        }

        /// <summary>
        /// 登记流上下文；同一节点已有流时替换，返回被替换的旧流
        /// </summary>
        private NodeStreamContext? AttachStream(NodeStreamContext streamContext)
        {
            while (true)
            {
                if (_nodeStreamContexts.TryAdd(streamContext.NodeId, streamContext))
                {
                    return null;
                }
                if (_nodeStreamContexts.TryGetValue(streamContext.NodeId, out var existing)
                    && _nodeStreamContexts.TryUpdate(streamContext.NodeId, streamContext, existing))
                {
                    return existing;
                }
            }
        }

        /// <summary>
        /// 清理流：只执行一次；只有字典里登记的仍是这条流时才移除并把节点标记为离线，
        /// 已被同一节点的新流替换的旧流不影响新流
        /// </summary>
        private async Task CleanupNodeStreamAsync(NodeStreamContext streamContext)
        {
            if (!streamContext.TryBeginCleanup()) return;

            var nodeId = streamContext.NodeId;
            streamContext.Close();

            if (!_nodeStreamContexts.TryRemove(new KeyValuePair<string, NodeStreamContext>(nodeId, streamContext)))
            {
                _logger.LogInformation("节点 {NodeId} 的旧流已关闭（已由新流替换，节点保持在线）", nodeId);
                return;
            }

            if (_disposed) return;

            try
            {
                // 库中状态真正变化时由节点管理服务发布离线事件，触发故障恢复
                using var scope = _serviceProvider.CreateScope();
                var nodeManagementService = scope.ServiceProvider.GetRequiredService<INodeManagementService>();
                await nodeManagementService.MarkSingleNodeAsOfflineAsync(nodeId).ConfigureAwait(false);
                _logger.LogInformation("节点 {NodeId} 的双向流已关闭，节点已标记为离线", nodeId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "标记节点 {NodeId} 离线失败", nodeId);
            }
        }

        /// <summary>
        /// 读循环：持续读取节点消息，更新会话活跃时间，按到达顺序逐条处理（不并发，保证状态不乱序）
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
                        await HandleHeartbeatMessage(nodeId, message.Heartbeat).ConfigureAwait(false);
                        break;
                    case FromWorker.MessageOneofCase.LogEntry:
                        HandleLogEntryMessage(nodeId, message.LogEntry);
                        break;
                    case FromWorker.MessageOneofCase.CrawlResult:
                        await HandleCrawlResultMessage(nodeId, message.CrawlResult).ConfigureAwait(false);
                        break;
                    case FromWorker.MessageOneofCase.TaskProgress:
                        await HandleTaskProgressMessage(nodeId, message.TaskProgress).ConfigureAwait(false);
                        break;
                    default:
                        _logger.LogWarning("节点 {NodeId} 发送了未知类型消息: {MessageCase}", nodeId, message.MessageCase);
                        break;
                }
            }
        }

        /// <summary>
        /// 写循环：所有发往节点的消息都经过发送通道，由这里串行写出
        /// </summary>
        private async Task StartWriteLoopAsync(
            NodeStreamContext streamContext,
            CancellationToken cancellationToken)
        {
            await foreach (var message in streamContext.SendChannel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await streamContext.ResponseStream.WriteAsync(message).ConfigureAwait(false);
                _logger.LogDebug("向节点 {NodeId} 成功发送消息，类型: {MessageCase}", streamContext.NodeId, message.MessageCase);
            }
        }
        #endregion

        #region 消息处理方法
        private async Task HandleHeartbeatMessage(string nodeId, Heartbeat hb)
        {
            _logger.LogInformation(
                $"收到节点 {nodeId} 心跳: CPU={hb.CpuUsage:F2}%, 内存={hb.MemoryUsageMb:F2}MB, 活跃任务={hb.ActiveTasks}");

            // 每次心跳使用独立作用域：单例服务不能长期持有 Scoped 的 DbContext，
            // 否则会与 UI 查询共用同一个 DbContext 并发访问
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var nodeManagementService = scope.ServiceProvider.GetRequiredService<INodeManagementService>();
                await nodeManagementService.UpdateNodeHeartbeatAsync(new NodeInfo
                {
                    NodeId = nodeId,
                    LastHeartbeat = DateTime.UtcNow,
                    CpuUsage = Math.Max(0, hb.CpuUsage),
                    MemoryUsageMb = Math.Max(0, hb.MemoryUsageMb),
                    ActiveTasks = Math.Max(0, hb.ActiveTasks),
                    DiskUsagePercent = Math.Max(0, hb.DiskUsagePercent)
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 单次心跳落库失败不应中断整条双向流
                _logger.LogError(ex, "节点 {NodeId} 心跳数据持久化失败", nodeId);
            }

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
                        _logger.LogError(ex, "心跳事件订阅者执行异常");
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
                        _logger.LogError(ex, "日志事件订阅者执行异常");
                    }
                }
            }
        }

        /// <summary>
        /// 爬取结果：结果落库与任务改为 Completed 在同一事务中完成，之后发送回执。
        /// 重复结果、来自被隔离节点的结果被丢弃；落库失败时任务标为 Failed，不发回执
        /// </summary>
        private async Task HandleCrawlResultMessage(string nodeId, CrawlerCore.Protos.CrawlResult result)
        {
            _logger.LogInformation("收到节点 {NodeId} 爬取结果: TaskId={TaskId}, 页面数={PageCount}",
                nodeId, result.TaskId, result.PageResults.Count);

            var completedAt = result.CompletedAt != null && result.CompletedAt.Seconds > 0
                ? DateTimeOffset.FromUnixTimeSeconds(result.CompletedAt.Seconds).UtcDateTime
                : DateTime.UtcNow;

            WorkerReportResult? outcome = null;
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var taskDataService = scope.ServiceProvider.GetRequiredService<ITaskDataService>();
                outcome = await taskDataService.SaveCrawlResultAsync(nodeId, ToEntity(result, completedAt)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "持久化节点 {NodeId} 的爬取结果失败，任务标记为失败: TaskId={TaskId}", nodeId, result.TaskId);
                await MarkTaskFailedAfterResultLossAsync(nodeId, result).ConfigureAwait(false);
            }

            if (outcome != null)
            {
                switch (outcome.Outcome)
                {
                    case WorkerReportOutcome.Accepted:
                        _logger.LogInformation("爬取结果已持久化，任务完成: TaskId={TaskId}, 页面数={PageCount}", result.TaskId, result.PageResults.Count);
                        PublishSafely(new CrawlResultReceivedEventArgs(result.TaskId, result.PageResults.Count, completedAt));
                        PublishTaskProgress(outcome.Task!, result.PageResults.Count, outcome.Task!.MaxPages, nodeId);
                        await SendResultAckAsync(nodeId, result.TaskId, accepted: true).ConfigureAwait(false);
                        break;
                    case WorkerReportOutcome.Duplicate:
                        _logger.LogWarning("任务 {TaskId} 的结果已存在，丢弃节点 {NodeId} 重复提交的结果", result.TaskId, nodeId);
                        await SendResultAckAsync(nodeId, result.TaskId, accepted: true).ConfigureAwait(false);
                        break;
                    case WorkerReportOutcome.NotAssignedToNode:
                        _logger.LogWarning("任务 {TaskId} 已分配给节点 {AssignedNode}，丢弃来自节点 {NodeId} 的结果",
                            result.TaskId, outcome.Task?.AssignedNode, nodeId);
                        await SendResultAckAsync(nodeId, result.TaskId, accepted: false).ConfigureAwait(false);
                        break;
                    default:
                        _logger.LogWarning("收到未知任务 {TaskId} 的结果（节点 {NodeId}），已丢弃", result.TaskId, nodeId);
                        await SendResultAckAsync(nodeId, result.TaskId, accepted: false).ConfigureAwait(false);
                        break;
                }
            }

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
                        _logger.LogError(ex, "爬取结果事件订阅者执行异常");
                    }
                }
            }
        }

        /// <summary>
        /// 结果落库失败：把任务标为 Failed 并通知界面，避免“已完成但没有数据”或数据静默丢失。
        /// 不发送回执：以后 Worker 重发（T5）时，结果落库成功会把任务改为 Completed
        /// </summary>
        private async Task MarkTaskFailedAfterResultLossAsync(string nodeId, CrawlerCore.Protos.CrawlResult result)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var taskDataService = scope.ServiceProvider.GetRequiredService<ITaskDataService>();
                if (await taskDataService.MarkTaskFailedAsync(result.TaskId, nodeId).ConfigureAwait(false))
                {
                    var task = await taskDataService.GetTaskByIdAsync(result.TaskId).ConfigureAwait(false);
                    if (task != null)
                    {
                        PublishTaskProgress(task, result.PageResults.Count, task.MaxPages, nodeId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "结果落库失败后标记任务 {TaskId} 失败时再次出错", result.TaskId);
            }
        }

        /// <summary>
        /// 任务进度：交给状态机处理，只有生效的进度才发布给界面
        /// </summary>
        private async Task HandleTaskProgressMessage(string nodeId, TaskProgress progress)
        {
            _logger.LogInformation("收到节点 {NodeId} 任务进度: TaskId={TaskId}, 已爬取={CrawledPages}/{TotalPages}, 状态={Status}",
                nodeId, progress.TaskId, progress.CrawledPages, progress.TotalPages, progress.CurrentStatus);

            var statusValue = (int)progress.CurrentStatus;
            if (!Enum.IsDefined(typeof(TaskStatus), statusValue))
            {
                _logger.LogWarning("任务 {TaskId} 的状态值 {StatusValue} 无效，忽略该进度", progress.TaskId, statusValue);
            }
            else
            {
                var reportedStatus = (TaskStatus)statusValue;
                if (reportedStatus == TaskStatus.Failed)
                {
                    _logger.LogWarning("节点 {NodeId} 报告任务 {TaskId} 失败，原因: {ErrorMessage}",
                        nodeId, progress.TaskId, string.IsNullOrWhiteSpace(progress.ErrorMessage) ? "（未提供）" : progress.ErrorMessage);
                }

                try
                {
                    WorkerReportResult outcome;
                    using (var scope = _serviceProvider.CreateScope())
                    {
                        var taskDataService = scope.ServiceProvider.GetRequiredService<ITaskDataService>();
                        outcome = await taskDataService.ApplyWorkerProgressAsync(nodeId, progress.TaskId, reportedStatus).ConfigureAwait(false);
                    }

                    switch (outcome.Outcome)
                    {
                        case WorkerReportOutcome.Accepted:
                            PublishTaskProgress(outcome.Task!, progress.CrawledPages, progress.TotalPages, nodeId);
                            break;
                        case WorkerReportOutcome.TaskNotFound:
                            _logger.LogWarning("收到未知任务 {TaskId} 的进度（节点 {NodeId}），已丢弃", progress.TaskId, nodeId);
                            break;
                        case WorkerReportOutcome.NotAssignedToNode:
                            _logger.LogWarning("任务 {TaskId} 已分配给节点 {AssignedNode}，丢弃来自节点 {NodeId} 的进度",
                                progress.TaskId, outcome.Task?.AssignedNode, nodeId);
                            break;
                        default:
                            _logger.LogDebug("任务 {TaskId} 当前为 {CurrentStatus}，按状态机忽略节点 {NodeId} 上报的 {ReportedStatus}",
                                progress.TaskId, outcome.Task?.Status, nodeId, reportedStatus);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "处理任务 {TaskId} 的进度失败", progress.TaskId);
                }
            }

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
                        _logger.LogError(ex, "任务进度事件订阅者执行异常");
                    }
                }
            }
        }

        private void PublishTaskProgress(CrawlerCore.Entities.CrawlerTask task, int crawledPages, int totalPages, string nodeId)
        {
            PublishSafely(new TaskProgressUpdatedEventArgs(
                task.TaskId,
                task.TaskName,
                task.Status,
                Math.Max(0, crawledPages),
                Math.Max(0, totalPages),
                nodeId));
        }

        private void PublishSafely<T>(T args) where T : EventArgs
        {
            try
            {
                _eventAggregator.Publish(this, args);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "发布事件 {EventType} 失败", typeof(T).Name);
            }
        }

        private Task<bool> SendResultAckAsync(string nodeId, string taskId, bool accepted)
            => SendMessageToNodeAsync(nodeId, new FromMaster { ResultAck = new ResultAck { TaskId = taskId, Accepted = accepted } });

        private static CrawlerCore.Entities.CrawlResult ToEntity(CrawlerCore.Protos.CrawlResult result, DateTime completedAt)
        {
            var entity = new CrawlerCore.Entities.CrawlResult
            {
                TaskId = result.TaskId,
                CompletedAt = completedAt,
                PageResults = new List<CrawlerCore.Entities.PageResult>()
            };
            foreach (var page in result.PageResults)
            {
                entity.PageResults.Add(new CrawlerCore.Entities.PageResult
                {
                    Url = page.Url,
                    FinalUrl = page.Finalurl,
                    StatusCode = page.StatusCode,
                    ParsedData = page.ParsedData.ToDictionary(kv => kv.Key, kv => (object)kv.Value),
                    Links = new List<string>(page.Links)
                });
            }
            return entity;
        }
        #endregion

        #region 对调度与健康检查暴露的操作
        public bool IsNodeConnected(string nodeId)
            => !string.IsNullOrWhiteSpace(nodeId)
               && _nodeStreamContexts.TryGetValue(nodeId, out var context)
               && !context.IsClosing;

        public int GetMaxConcurrentTasks(string nodeId) => _sessionManager.GetMaxConcurrentTasks(nodeId);

        public void DisconnectNode(string nodeId)
        {
            if (!string.IsNullOrWhiteSpace(nodeId) && _nodeStreamContexts.TryGetValue(nodeId, out var context))
            {
                _logger.LogWarning("主动断开节点 {NodeId} 的双向流", nodeId);
                context.Close();
            }
        }

        /// <summary>
        /// 发送消息到节点（写入发送通道，由写循环写出）
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
                || streamContext.IsClosing)
            {
                _logger.LogWarning("发送消息失败：节点 {NodeId} 无有效会话或活跃流", nodeId);
                return false;
            }

            try
            {
                await streamContext.SendChannel.Writer.WriteAsync(message, streamContext.Token).ConfigureAwait(false);
                _logger.LogDebug("消息已入队节点 {NodeId} 发送队列，消息类型: {MessageCase}", nodeId, message.MessageCase);
                return true;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("向节点 {NodeId} 发送消息被取消", nodeId);
                return false;
            }
            catch (ChannelClosedException)
            {
                _logger.LogWarning("向节点 {NodeId} 发送消息失败：通道已关闭", nodeId);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "向节点 {NodeId} 发送消息失败", nodeId);
                return false;
            }
        }

        /// <summary>
        /// 发送任务到节点
        /// </summary>
        public async Task<bool> SendTaskToNodeAsync(string nodeId, CrawlerCore.Entities.CrawlerTask task)
        {
            if (string.IsNullOrWhiteSpace(nodeId) || task == null)
            {
                _logger.LogWarning("发送任务失败：参数无效");
                return false;
            }

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

            var sent = await SendMessageToNodeAsync(nodeId, new FromMaster { NewTask = protoTask }).ConfigureAwait(false);
            if (sent)
            {
                _logger.LogInformation("任务已发送到节点 {NodeId}，TaskId: {TaskId}", nodeId, task.TaskId);
            }
            return sent;
        }
        #endregion

        #region 私有类与Dispose
        private sealed class NodeStreamContext
        {
            private int _cleanupStarted;

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
                Token = cts.Token;
            }

            public string NodeId { get; }
            public string SessionId { get; }
            public IServerStreamWriter<FromMaster> ResponseStream { get; }
            public Channel<FromMaster> SendChannel { get; }
            private CancellationTokenSource Cts { get; }

            /// <summary>流的取消令牌（创建时保存，CTS 释放后仍可安全读取）</summary>
            public CancellationToken Token { get; }

            public bool IsClosing => Token.IsCancellationRequested;

            /// <summary>关闭流：取消读写循环并关闭发送通道。可重复调用</summary>
            public void Close()
            {
                try
                {
                    Cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // 流已结束并释放
                }
                SendChannel.Writer.TryComplete();
            }

            /// <summary>清理只执行一次</summary>
            public bool TryBeginCleanup() => Interlocked.Exchange(ref _cleanupStarted, 1) == 0;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

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
                streamContext.Close();
            }
            _nodeStreamContexts.Clear();

            GC.SuppressFinalize(this);
        }

        ~CrawlerCoordinatorService()
        {
            Dispose();
        }
        #endregion
    }
}
