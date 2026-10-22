using CrawlerCore.Common;
using CrawlerCore.Entities;
using CrawlerCore.Events;
using CrawlerCore.Infrastructure;
using CrawlerCore.Infrastructure.Constants;
using CrawlerCore.Protos;
using CrawlerWorker.Communication;
using CrawlerWorker.Services.Engine;
using CrawlerWorker.Services.Simulation;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using CrawlerTask = CrawlerCore.Entities.CrawlerTask;
using TaskStatus = CrawlerCore.Entities.TaskStatus;

namespace CrawlerWorker.Services
{
    /// <summary>
    /// Worker核心生命周期托管服务
    /// </summary>
    public class WorkerHostedService : BackgroundService, IDisposable
    {
        #region 依赖注入
        private readonly IGrpcClientManager _grpcClientManager;
        private readonly INodeInfoCollector _nodeInfoCollector;
        private readonly IWorkerSessionManager _sessionManager;
        private readonly GrpcSettings _grpcSettings;
        private readonly CrawlerSettings _crawlerSettings;
        private readonly SignalRSettings _signalRSettings;
        private readonly IHostApplicationLifetime _appLifetime;
        private readonly ICrawlerEngine _crawlerEngine;
        #endregion

        #region 线程安全与并发控制字段
        private readonly object _streamLock = new();
        private readonly object _eventLock = new();
        private bool _disposed = false;
        // 任务接收队列
        private readonly ConcurrentQueue<CrawlerTask> _localTaskQueue = new();
        // 并发任务控制信号量
        private readonly SemaphoreSlim _taskConcurrencySemaphore;
        // 进度上报节流缓存
        private readonly ConcurrentDictionary<string, DateTime> _lastProgressReportTime = new();
        // 任务执行全局取消令牌源
        private CancellationTokenSource? _taskExecutionCts;
        // 任务消费循环运行标记
        private int _isTaskConsumerRunning = 0;
        #endregion

        #region 事件定义
        private Func<NodeRegistration, RegistrationResponse, CancellationToken, Task>? _nodeRegistered;
        public event Func<NodeRegistration, RegistrationResponse, CancellationToken, Task> NodeRegistered
        {
            add { lock (_eventLock) _nodeRegistered += value; }
            remove { lock (_eventLock) _nodeRegistered -= value; }
        }

        private Func<string, CancellationToken, Task>? _streamConnected;
        public event Func<string, CancellationToken, Task> StreamConnected
        {
            add { lock (_eventLock) _streamConnected += value; }
            remove { lock (_eventLock) _streamConnected -= value; }
        }

        private Func<string, CancellationToken, Task>? _streamDisconnected;
        public event Func<string, CancellationToken, Task> StreamDisconnected
        {
            add { lock (_eventLock) _streamDisconnected += value; }
            remove { lock (_eventLock) _streamDisconnected -= value; }
        }

        private Func<CrawlerTask, CancellationToken, Task>? _newTaskReceived;
        public event Func<CrawlerTask, CancellationToken, Task> NewTaskReceived
        {
            add { lock (_eventLock) _newTaskReceived += value; }
            remove { lock (_eventLock) _newTaskReceived -= value; }
        }

        private Func<TaskControl, CancellationToken, Task>? _taskControlReceived;
        public event Func<TaskControl, CancellationToken, Task> TaskControlReceived
        {
            add { lock (_eventLock) _taskControlReceived += value; }
            remove { lock (_eventLock) _taskControlReceived -= value; }
        }

        private Func<SystemConfig, CancellationToken, Task>? _systemConfigReceived;
        public event Func<SystemConfig, CancellationToken, Task> SystemConfigReceived
        {
            add { lock (_eventLock) _systemConfigReceived += value; }
            remove { lock (_eventLock) _systemConfigReceived -= value; }
        }
        #endregion

        #region 流与心跳管理字段
        private Channel<FromWorker>? _sendChannel;
        private CancellationTokenSource? _streamCts;
        private PeriodicTimer? _heartbeatTimer;
        private int _isStreamRunning = 0;
        private int _reconnectAttempts = 0;
        #endregion

        #region 全局生命周期字段
        private CancellationTokenSource? _globalCts;
        #endregion

        public WorkerHostedService(
            IGrpcClientManager grpcClientManager,
            INodeInfoCollector nodeInfoCollector,
            IWorkerSessionManager sessionManager,
            IOptions<GrpcSettings> grpcSettings,
            IOptions<CrawlerSettings> crawlerSettings,
            IOptions<SignalRSettings> signalRSettings,
            IHostApplicationLifetime appLifetime,
            ICrawlerEngine crawlerEngine)
        {
            _grpcClientManager = grpcClientManager ?? throw new ArgumentNullException(nameof(grpcClientManager));
            _nodeInfoCollector = nodeInfoCollector ?? throw new ArgumentNullException(nameof(nodeInfoCollector));
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
            _grpcSettings = grpcSettings.Value ?? throw new ArgumentNullException(nameof(grpcSettings));
            _crawlerSettings = crawlerSettings.Value ?? throw new ArgumentNullException(nameof(crawlerSettings));
            _signalRSettings = signalRSettings.Value ?? throw new ArgumentNullException(nameof(signalRSettings));
            _appLifetime = appLifetime ?? throw new ArgumentNullException(nameof(appLifetime));
            _crawlerEngine = crawlerEngine ?? throw new ArgumentNullException(nameof(crawlerEngine));

            // 初始化并发控制信号量
            _taskConcurrencySemaphore = new SemaphoreSlim(_crawlerSettings.MaxConcurrentTasks, _crawlerSettings.MaxConcurrentTasks);
            // 注册节点注册事件
            _nodeRegistered += OnNodeRegistered_StartStreamingAsync;
            // 订阅爬虫引擎进度更新事件
            _crawlerEngine.ProgressUpdated += OnCrawlerEngineProgressUpdated;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // 绑定全局停止令牌：所有后台任务统一绑定
            _globalCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            // 初始化任务执行专用CTS
            _taskExecutionCts = CancellationTokenSource.CreateLinkedTokenSource(_globalCts.Token);

            LogHelper.Info($"Worker节点启动，进程ID: {Process.GetCurrentProcess().Id}");

            try
            {
                var nodeRegistration = await _nodeInfoCollector.CollectNodeRegistrationAsync(stoppingToken).ConfigureAwait(false);

                if (!ValidateNodeRegistration(nodeRegistration))
                {
                    LogHelper.Fatal("节点注册参数校验失败，Worker启动终止");
                    _appLifetime.StopApplication();
                    return;
                }

                var connectivity = await _grpcClientManager.CheckMasterConnectivityAsync(stoppingToken).ConfigureAwait(false);
                if (!connectivity)
                {
                    LogHelper.Fatal("Master端点不可达，Worker启动失败");
                    _appLifetime.StopApplication();
                    return;
                }

                var registrationResponse = await ExecuteRegistrationWithRetryAsync(nodeRegistration, stoppingToken).ConfigureAwait(false);
                if (registrationResponse == null || !registrationResponse.Success)
                {
                    LogHelper.Fatal("节点注册失败，Worker启动失败");
                    _appLifetime.StopApplication();
                    return;
                }

                _sessionManager.SaveSession(registrationResponse);
                LogHelper.Info($"节点注册成功，SessionId: {registrationResponse.SessionId}");

                // 触发节点注册事件
                var handler = Volatile.Read(ref _nodeRegistered);
                if (handler != null)
                {
                    foreach (Func<NodeRegistration, RegistrationResponse, CancellationToken, Task> subscriber in handler.GetInvocationList())
                    {
                        try
                        {
                            await subscriber.Invoke(nodeRegistration, registrationResponse, stoppingToken).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            LogHelper.Error(ex, "节点注册事件订阅者执行异常");
                        }
                    }
                }

                // 启动任务消费后台循环
                _ = StartTaskConsumerLoopAsync(_taskExecutionCts.Token);

                LogHelper.Info("Worker节点正常运行，等待停止信号");
                await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                LogHelper.Info("Worker执行流程被取消，开始优雅关闭");
            }
            catch (Exception ex)
            {
                LogHelper.Fatal(ex, "Worker执行流程发生致命异常");
                _appLifetime.StopApplication();
            }
            finally
            {
                await CleanupStreamResourcesAsync().ConfigureAwait(false);
            }
        }

        #region 任务消费循环与引擎执行
        /// <summary>
        /// 任务消费后台循环：持续消费本地任务队列，基于信号量控制并发，调用爬虫引擎执行任务
        /// </summary>
        private async Task StartTaskConsumerLoopAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref _isTaskConsumerRunning, 1, 0) != 0)
            {
                LogHelper.Warn("任务消费循环已在运行，跳过重复启动");
                return;
            }

            LogHelper.Info($"任务消费循环启动，最大并发任务数: {_crawlerSettings.MaxConcurrentTasks}");

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    // 尝试从队列获取待执行任务
                    if (!_localTaskQueue.TryDequeue(out var task))
                    {
                        await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    // 并发控制：等待信号量
                    await _taskConcurrencySemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

                    // 为每个任务创建独立的取消令牌，与全局令牌联动
                    var taskCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var taskCancellationToken = taskCts.Token;

                    // 后台异步执行任务，不阻塞消费循环
                    _ = ExecuteTaskWithLifecycleAsync(task, taskCts, taskCancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                LogHelper.Info("任务消费循环被取消");
            }
            catch (Exception ex)
            {
                LogHelper.Fatal(ex, "任务消费循环发生致命异常");
            }
            finally
            {
                Interlocked.Exchange(ref _isTaskConsumerRunning, 0);
                LogHelper.Info("任务消费循环已停止");
            }
        }

        /// <summary>
        /// 任务全生命周期执行：封装引擎调用、状态管理、异常处理、资源释放
        /// </summary>
        private async Task ExecuteTaskWithLifecycleAsync(CrawlerTask task, CancellationTokenSource taskCts, CancellationToken cancellationToken)
        {
            try
            {
                LogHelper.Info($"开始执行任务，TaskId: {task.TaskId}, 任务名称: {task.TaskName}, 插件: {task.PluginName}");

                // 更新任务状态为运行中
                task.Status = TaskStatus.Running;

                // 上报任务开始执行的状态
                await ReportTaskProgressAsync(task, 0, TaskStatus.Running).ConfigureAwait(false);

                // 调用爬虫引擎核心执行方法
                var crawlResult = await _crawlerEngine.ExecuteAsync(task, cancellationToken).ConfigureAwait(false);

                // 任务正常完成，上报最终状态
                LogHelper.Info($"任务执行完成，TaskId: {task.TaskId}, 总爬取页面数: {crawlResult.PageResults.Count}");
                await ReportTaskFinalProgressAsync(task, crawlResult, TaskStatus.Completed).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 任务被取消，更新状态为失败
                task.Status = TaskStatus.Failed;
                LogHelper.Warn($"任务被取消，TaskId: {task.TaskId}");
                await ReportTaskFinalProgressAsync(task, new CrawlerCore.Entities.CrawlResult(), TaskStatus.Failed, "任务被取消").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 任务执行异常，更新状态为失败
                task.Status = TaskStatus.Failed;
                LogHelper.Error(ex, $"任务执行异常，TaskId: {task.TaskId}");
                await ReportTaskFinalProgressAsync(task, new CrawlerCore.Entities.CrawlResult(), TaskStatus.Failed, ex.Message).ConfigureAwait(false);
            }
            finally
            {
                // 释放信号量、减少活跃任务计数、清理资源
                try
                {
                    _taskConcurrencySemaphore.Release();
                }
                catch (SemaphoreFullException ex)
                {
                    LogHelper.Error(ex, $"信号量释放异常，TaskId: {task.TaskId}");
                }

                _sessionManager.DecrementActiveTasks();
                _lastProgressReportTime.TryRemove(task.TaskId, out _);
                taskCts.Dispose();
            }
        }
        #endregion

        #region 进度上报与节流控制
        /// <summary>
        /// 爬虫引擎进度更新事件处理程序：实现节流控制，线程安全上报到Master
        /// </summary>
        private async void OnCrawlerEngineProgressUpdated(object? sender, TaskProgressUpdatedEventArgs e)
        {
            try
            {
                // 空值防护
                if (string.IsNullOrWhiteSpace(e.TaskId)) return;

                await HandleProgressUpdateAsync(e).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogHelper.Error(ex, "进度上报事件处理异常");
            }
        }

        /// <summary>
        /// 实际处理进度更新的异步方法，避免 async void 导致的异常问题
        /// </summary>
        private async Task HandleProgressUpdateAsync(TaskProgressUpdatedEventArgs e)
        {
            // 1. 节流判断：最终状态强制上报，非最终状态需满足节流间隔
            var isFinalStatus = e.CurrentStatus is TaskStatus.Completed or TaskStatus.Failed;
            var now = DateTime.UtcNow;
            var throttleInterval = TimeSpan.FromSeconds(_signalRSettings.TaskProgressThrottleSeconds);
            bool shouldReport = false;

            if (isFinalStatus)
            {
                shouldReport = true;
            }
            else
            {
                // 原子性检查和更新，避免竞态条件
                _lastProgressReportTime.AddOrUpdate(
                    e.TaskId,
                    now, // 如果不存在，添加当前时间并报告
                    (key, existingTime) =>
                    {
                        if (now - existingTime >= throttleInterval)
                        {
                            shouldReport = true;
                            return now;
                        }
                        shouldReport = false;
                        return existingTime;
                    });
            }

            if (!shouldReport) return;

            // 2. 构建上报消息
            var progressMessage = new TaskProgress
            {
                TaskId = e.TaskId,
                TaskName = e.TaskName,
                CrawledPages = e.CrawledPages,
                TotalPages = e.TotalPages,
                CurrentStatus = (CrawlerCore.Protos.TaskStatus)e.CurrentStatus,
                AssignedNodeId = e.AssignedNodeId
            };

            // 3. 线程安全上报：复用现有Channel机制，保证gRPC写入串行化
            var fromWorkerMessage = new FromWorker { TaskProgress = progressMessage };
            var sendSuccess = await SendMessageAsync(fromWorkerMessage).ConfigureAwait(false);

            // 4. 记录日志
            if (sendSuccess)
            {
                LogHelper.Debug($"进度上报成功，TaskId: {e.TaskId}, 已爬页数: {e.CrawledPages}/{e.TotalPages}");
            }
        }

        /// <summary>
        /// 上报任务进度（非最终状态）
        /// </summary>
        private async Task ReportTaskProgressAsync(CrawlerTask task, int crawledPages, TaskStatus status, string? errorMessage = null)
        {
            try
            {
                var progressMessage = new TaskProgress
                {
                    TaskId = task.TaskId,
                    TaskName = task.TaskName,
                    CrawledPages = crawledPages,
                    TotalPages = task.MaxPages,
                    CurrentStatus = (CrawlerCore.Protos.TaskStatus)status,
                    AssignedNodeId = task.AssignedNode
                };

                var fromWorkerMessage = new FromWorker { TaskProgress = progressMessage };
                await SendMessageAsync(fromWorkerMessage).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogHelper.Error(ex, $"任务进度上报失败，TaskId: {task.TaskId}");
            }
        }

        /// <summary>
        /// 任务最终状态强制上报：忽略节流间隔，确保Master收到最终结果
        /// </summary>
        private async Task ReportTaskFinalProgressAsync(CrawlerTask task, CrawlerCore.Entities.CrawlResult crawlResult, TaskStatus finalStatus, string? errorMessage = null)
        {
            try
            {
                var progressMessage = new TaskProgress
                {
                    TaskId = task.TaskId,
                    TaskName = task.TaskName,
                    CrawledPages = crawlResult.PageResults.Count,
                    TotalPages = task.MaxPages,
                    CurrentStatus = (CrawlerCore.Protos.TaskStatus)finalStatus,
                    AssignedNodeId = task.AssignedNode,
                    ErrorMessage = errorMessage ?? string.Empty
                };

                var fromWorkerMessage = new FromWorker { TaskProgress = progressMessage };
                await SendMessageAsync(fromWorkerMessage).ConfigureAwait(false);
                LogHelper.Info($"任务最终状态上报完成，TaskId: {task.TaskId}, 状态: {finalStatus}");

                if (finalStatus == TaskStatus.Completed)
                {
                    var crawlResultMessage = new CrawlerCore.Protos.CrawlResult
                    {
                        TaskId = task.TaskId,
                        CompletedAt = crawlResult.CompletedAt.HasValue
                        ? Timestamp.FromDateTime(crawlResult.CompletedAt.Value.ToUniversalTime())
                        : null
                    };

                    if (crawlResult.PageResults != null)
                    {
                        foreach (var pr in crawlResult.PageResults)
                        {
                            var protoPr = new CrawlerCore.Protos.PageResult
                            {
                                Url = pr.Url ?? string.Empty,
                                Finalurl = pr.FinalUrl ?? string.Empty,
                                StatusCode = pr.StatusCode
                            };

                            if (pr.ParsedData != null && pr.ParsedData.Count > 0)
                            {
                                protoPr.ParsedData.Add(ParsedDataConverter.ToProtoMap(pr.ParsedData));
                            }

                            if (pr.Links != null && pr.Links.Count > 0)
                            {
                                protoPr.Links.Add(pr.Links);
                            }

                            crawlResultMessage.PageResults.Add(protoPr);
                        }
                        fromWorkerMessage = new FromWorker { CrawlResult = crawlResultMessage };
                        await SendMessageAsync(fromWorkerMessage).ConfigureAwait(false);
                        LogHelper.Info($"任务结果上报完成，TaskId: {task.TaskId}, 爬取页面数: {crawlResult.PageResults.Count}");
                    }
                }
            }
            catch (Exception ex)
            {
                LogHelper.Error(ex, $"任务最终状态上报失败，TaskId: {task.TaskId}");
            }
        }
        #endregion

        #region 双向流核心实现
        private async Task OnNodeRegistered_StartStreamingAsync(
            NodeRegistration registration,
            RegistrationResponse response,
            CancellationToken cancellationToken)
        {
            LogHelper.Debug("节点注册成功事件触发，准备启动双向流");
            _reconnectAttempts = 0;

            _ = StartStreamingAsync(cancellationToken);

            await Task.CompletedTask;
        }

        private async Task StartStreamingAsync(CancellationToken stoppingToken)
        {
            if (Interlocked.CompareExchange(ref _isStreamRunning, 1, 0) != 0)
            {
                LogHelper.Warn("双向流已在运行，跳过重复启动");
                return;
            }

            try
            {
                if (!_sessionManager.IsRegistered)
                {
                    LogHelper.Error("启动双向流失败：节点未注册");
                    return;
                }

                _streamCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                _sendChannel = System.Threading.Channels.Channel.CreateUnbounded<FromWorker>(new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false
                });

                var headers = new Metadata
                {
                    { "node-id", _nodeInfoCollector.GetNodeId() },
                    { "session-id", _sessionManager.SessionId! },
                    { "authorization", $"Bearer {_grpcSettings.NodeAuthToken}" }
                };

                LogHelper.Info("开始建立与Master的双向流通道");
                var call = _grpcClientManager.CoordinatorClient.StartStreaming(
                    headers: headers,
                    cancellationToken: _streamCts.Token);

                LogHelper.Info("双向流通道建立成功");
                var connectedHandler = Volatile.Read(ref _streamConnected);
                if (connectedHandler != null)
                {
                    foreach (Func<string, CancellationToken, Task> subscriber in connectedHandler.GetInvocationList())
                    {
                        try
                        {
                            await subscriber.Invoke(_sessionManager.SessionId!, _streamCts.Token).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            LogHelper.Error(ex, "流连接事件订阅者执行异常");
                        }
                    }
                }

                _reconnectAttempts = 0;

                var readTask = StartReadLoopAsync(call.ResponseStream, _streamCts.Token);
                var writeTask = StartWriteLoopAsync(call.RequestStream, _streamCts.Token);
                var heartbeatTask = StartHeartbeatLoopAsync(_streamCts.Token);

                await Task.WhenAny(readTask, writeTask, heartbeatTask).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogHelper.Error(ex, "双向流运行异常");
                throw;
            }
            finally
            {
                await CleanupStreamResourcesAsync().ConfigureAwait(false);
                Interlocked.Exchange(ref _isStreamRunning, 0);

                var disconnectedHandler = Volatile.Read(ref _streamDisconnected);
                if (disconnectedHandler != null)
                {
                    foreach (Func<string, CancellationToken, Task> subscriber in disconnectedHandler.GetInvocationList())
                    {
                        try
                        {
                            await subscriber.Invoke(_sessionManager.SessionId!, stoppingToken).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            LogHelper.Error(ex, "流断开事件订阅者执行异常");
                        }
                    }
                }

                await HandleStreamReconnectAsync(stoppingToken).ConfigureAwait(false);
            }
        }

        private async Task StartReadLoopAsync(
            IAsyncStreamReader<FromMaster> responseStream,
            CancellationToken cancellationToken)
        {
            LogHelper.Debug("启动双向流读循环");

            await foreach (var message in responseStream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                switch (message.MessageCase)
                {
                    case FromMaster.MessageOneofCase.NewTask:
                        {
                            var protoTask = message.NewTask;
                            LogHelper.Info($"收到Master下发的新任务: TaskId={protoTask.TaskId}, Name={protoTask.TaskName}");

                            var crawlerTask = new CrawlerTask
                            {
                                TaskId = protoTask.TaskId,
                                TaskName = protoTask.TaskName,
                                PluginName = protoTask.PluginName,
                                MaxDepth = protoTask.MaxDepth,
                                MaxPages = protoTask.MaxPages,
                                Priority = protoTask.Priority,
                                Status = (TaskStatus)protoTask.Status,
                                StartUrls = protoTask.StartUrls.ToList(),
                                Configuration = protoTask.Configuration.ToDictionary(kv => kv.Key, kv => kv.Value)
                            };

                            // 任务入队
                            await ProcessNewTaskAsync(crawlerTask).ConfigureAwait(false);

                            // 触发外部事件（仅用于扩展）
                            var taskHandler = Volatile.Read(ref _newTaskReceived);
                            if (taskHandler != null)
                            {
                                foreach (Func<CrawlerTask, CancellationToken, Task> subscriber in taskHandler.GetInvocationList())
                                {
                                    try
                                    {
                                        await subscriber.Invoke(crawlerTask, cancellationToken).ConfigureAwait(false);
                                    }
                                    catch (Exception ex)
                                    {
                                        LogHelper.Error(ex, "新任务事件订阅者执行异常");
                                    }
                                }
                            }
                            break;
                        }

                    case FromMaster.MessageOneofCase.TaskControl:
                        LogHelper.Info($"收到Master下发的任务控制指令: TaskId={message.TaskControl.TaskId}, Command={message.TaskControl.Command}");
                        var controlHandler = Volatile.Read(ref _taskControlReceived);
                        if (controlHandler != null)
                        {
                            foreach (Func<TaskControl, CancellationToken, Task> subscriber in controlHandler.GetInvocationList())
                            {
                                try
                                {
                                    await subscriber.Invoke(message.TaskControl, cancellationToken).ConfigureAwait(false);
                                }
                                catch (Exception ex)
                                {
                                    LogHelper.Error(ex, "任务控制事件订阅者执行异常");
                                }
                            }
                        }
                        break;

                    case FromMaster.MessageOneofCase.SystemConfig:
                        LogHelper.Info("收到Master下发的系统配置更新");
                        var configHandler = Volatile.Read(ref _systemConfigReceived);
                        if (configHandler != null)
                        {
                            foreach (Func<SystemConfig, CancellationToken, Task> subscriber in configHandler.GetInvocationList())
                            {
                                try
                                {
                                    await subscriber.Invoke(message.SystemConfig, cancellationToken).ConfigureAwait(false);
                                }
                                catch (Exception ex)
                                {
                                    LogHelper.Error(ex, "系统配置事件订阅者执行异常");
                                }
                            }
                        }
                        break;

                    default:
                        LogHelper.Warn($"收到Master未知类型消息: {message.MessageCase}");
                        break;
                }
            }

            LogHelper.Debug("双向流读循环结束");
        }

        private async Task StartWriteLoopAsync(
            IClientStreamWriter<FromWorker> requestStream,
            CancellationToken cancellationToken)
        {
            LogHelper.Debug("启动双向流写循环");

            await foreach (var message in _sendChannel!.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await requestStream.WriteAsync(message, cancellationToken).ConfigureAwait(false);
                    LogHelper.Debug($"成功向Master发送消息，类型: {message.MessageCase}");
                }
                catch (Exception ex)
                {
                    LogHelper.Error(ex, "向Master写入消息失败");
                    throw;
                }
            }

            LogHelper.Debug("双向流写循环结束");
        }

        private async Task StartHeartbeatLoopAsync(CancellationToken cancellationToken)
        {
            LogHelper.Info($"启动心跳上报循环，间隔: {_grpcSettings.HeartbeatIntervalSeconds}秒");
            _heartbeatTimer = new PeriodicTimer(TimeSpan.FromSeconds(_grpcSettings.HeartbeatIntervalSeconds));

            try
            {
                await SendHeartbeatAsync(cancellationToken).ConfigureAwait(false);

                while (await _heartbeatTimer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    await SendHeartbeatAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                LogHelper.Info("心跳上报循环被取消");
            }
            catch (Exception ex)
            {
                LogHelper.Error(ex, "心跳上报循环异常");
                throw;
            }
            finally
            {
                _heartbeatTimer?.Dispose();
                _heartbeatTimer = null;
                LogHelper.Info("心跳上报循环已停止");
            }
        }

        /// <summary>
        /// 原子化校验+写入全流程
        /// </summary>
        public async Task<bool> SendMessageAsync(FromWorker message, CancellationToken cancellationToken = default)
        {
            if (message == null)
            {
                LogHelper.Warn("发送消息失败：消息为空");
                return false;
            }

            ValueTask writeTask;
            lock (_streamLock)
            {
                if (_sendChannel == null
                    || Volatile.Read(ref _isStreamRunning) == 0
                    || _streamCts?.IsCancellationRequested == true
                    || _sendChannel.Reader.Completion.IsCompleted)
                {
                    LogHelper.Warn("发送消息失败：双向流未运行或已关闭");
                    return false;
                }

                try
                {
                    writeTask = _sendChannel.Writer.WriteAsync(message, cancellationToken);

                    if (writeTask.IsCompletedSuccessfully)
                    {
                        LogHelper.Debug($"消息已入队发送队列，类型: {message.MessageCase}");
                        return true;
                    }
                }
                catch (OperationCanceledException)
                {
                    LogHelper.Warn("消息发送被取消");
                    return false;
                }
                catch (Exception ex)
                {
                    LogHelper.Error(ex, "消息入队发送队列失败");
                    return false;
                }
            }

            try
            {
                await writeTask.ConfigureAwait(false);
                LogHelper.Debug($"消息已入队发送队列，类型: {message.MessageCase}");
                return true;
            }
            catch (OperationCanceledException)
            {
                LogHelper.Warn("消息发送被取消");
                return false;
            }
            catch (Exception ex)
            {
                LogHelper.Error(ex, "消息入队发送队列失败");
                return false;
            }
        }

        private async Task SendHeartbeatAsync(CancellationToken cancellationToken)
        {
            var heartbeat = await _nodeInfoCollector.CollectHeartbeatAsync(cancellationToken).ConfigureAwait(false);
            var message = new FromWorker
            {
                Heartbeat = heartbeat
            };

            await SendMessageAsync(message, cancellationToken).ConfigureAwait(false);
        }

        private async Task HandleStreamReconnectAsync(CancellationToken stoppingToken)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                LogHelper.Info("应用正在关闭，跳过重连");
                return;
            }

            _reconnectAttempts++;
            LogHelper.Warn($"双向流断开，开始第{_reconnectAttempts}次重连");

            if (_reconnectAttempts > _grpcSettings.MaxRetryAttempts)
            {
                LogHelper.Fatal("达到最大重连次数，触发重新注册流程");
                _sessionManager.ClearSession();
                _reconnectAttempts = 0;

                try
                {
                    var nodeRegistration = await _nodeInfoCollector.CollectNodeRegistrationAsync(stoppingToken).ConfigureAwait(false);
                    var registrationResponse = await ExecuteRegistrationWithRetryAsync(nodeRegistration, stoppingToken).ConfigureAwait(false);

                    if (registrationResponse != null && registrationResponse.Success)
                    {
                        _sessionManager.SaveSession(registrationResponse);
                        LogHelper.Info($"重新注册成功，SessionId: {registrationResponse.SessionId}");

                        var handler = Volatile.Read(ref _nodeRegistered);
                        if (handler != null)
                        {
                            foreach (Func<NodeRegistration, RegistrationResponse, CancellationToken, Task> subscriber in handler.GetInvocationList())
                            {
                                try
                                {
                                    await subscriber.Invoke(nodeRegistration, registrationResponse, stoppingToken).ConfigureAwait(false);
                                }
                                catch (Exception ex)
                                {
                                    LogHelper.Error(ex, "重新注册事件订阅者执行异常");
                                }
                            }
                        }
                    }
                    else
                    {
                        LogHelper.Fatal("重新注册失败，Worker终止运行");
                        _appLifetime.StopApplication();
                    }
                }
                catch (Exception ex)
                {
                    LogHelper.Fatal(ex, "重新注册流程发生致命异常，Worker终止运行");
                    _appLifetime.StopApplication();
                }
                return;
            }

            await Task.Delay(_grpcSettings.RetryDelayMilliseconds, stoppingToken).ConfigureAwait(false);
            _grpcClientManager.RecreateChannel();
            await StartStreamingAsync(stoppingToken).ConfigureAwait(false);
        }

        /// <summary>
        /// 新任务入队处理
        /// </summary>
        public async Task ProcessNewTaskAsync(CrawlerTask task)
        {
            // 任务入队
            _localTaskQueue.Enqueue(task);
            _sessionManager.IncrementActiveTasks();

            // 初始进度上报
            var initProgress = new TaskProgress
            {
                TaskId = task.TaskId,
                CrawledPages = 0,
                TotalPages = task.MaxPages,
                CurrentStatus = CrawlerCore.Protos.TaskStatus.Pending
            };
            await SendMessageAsync(new FromWorker { TaskProgress = initProgress }).ConfigureAwait(false);

            // DEBUG模式进度模拟
#if DEBUG
            if (_crawlerSettings.EnableProgressSimulation && _globalCts != null)
            {
                var simulateCts = new CancellationTokenSource();
                var linkedCts = _streamCts == null
                    ? CancellationTokenSource.CreateLinkedTokenSource(simulateCts.Token, _globalCts.Token)
                    : CancellationTokenSource.CreateLinkedTokenSource(simulateCts.Token, _streamCts.Token, _globalCts.Token);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await TaskProgressSimulator.StartSimulationAsync(
                            task.TaskId,
                            task.MaxPages,
                            async progress => await SendMessageAsync(new FromWorker { TaskProgress = progress }).ConfigureAwait(false),
                            linkedCts.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        LogHelper.Error(ex, $"模拟后台任务崩溃 TaskId={task.TaskId}");
                    }
                    finally
                    {
                        linkedCts?.Dispose();
                        simulateCts?.Dispose();
                    }
                }, linkedCts.Token);
            }
#endif
        }

        /// <summary>
        /// 尝试从本地队列获取下一个待执行任务
        /// </summary>
        public bool TryGetNextTask(out CrawlerTask? task)
        {
            return _localTaskQueue.TryDequeue(out task);
        }

        private async Task CleanupStreamResourcesAsync()
        {
            LogHelper.Info("开始清理双向流资源");

            _heartbeatTimer?.Dispose();
            _heartbeatTimer = null;

            try { _streamCts?.Cancel(); } catch { }

            lock (_streamLock)
            {
                if (_sendChannel != null)
                {
                    _sendChannel.Writer.TryComplete();
                }
            }

            if (_sendChannel != null)
            {
                try
                {
                    await _sendChannel.Reader.Completion.WaitAsync(TimeSpan.FromMilliseconds(GrpcConstants.DefaultStreamCloseTimeoutMs)).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    LogHelper.Warn("发送队列等待消费超时");
                }
            }

            lock (_streamLock)
            {
                _sendChannel = null;
            }

            _streamCts?.Dispose();
            _streamCts = null;

            LogHelper.Info("双向流资源清理完成");
        }
        #endregion

        #region 注册流程方法
        private bool ValidateNodeRegistration(NodeRegistration registration)
        {
            if (string.IsNullOrWhiteSpace(registration.NodeId))
            {
                LogHelper.Error("节点注册参数校验失败：NodeId不能为空");
                return false;
            }
            if (registration.NodeId.Length > GrpcConstants.MaxNodeIdLength)
            {
                LogHelper.Error($"节点注册参数校验失败：NodeId长度不能超过{GrpcConstants.MaxNodeIdLength}字符");
                return false;
            }
            if (string.IsNullOrWhiteSpace(registration.MachineName))
            {
                LogHelper.Error("节点注册参数校验失败：MachineName不能为空");
                return false;
            }
            if (registration.CpuCores <= 0)
            {
                LogHelper.Error("节点注册参数校验失败：CpuCores必须大于0");
                return false;
            }
            if (registration.TotalMemoryGb < 0)
            {
                LogHelper.Error("节点注册参数校验失败：TotalMemoryGb不能为负数");
                return false;
            }
            if (string.IsNullOrWhiteSpace(registration.IpAddress))
            {
                LogHelper.Warn("节点IP地址为空，使用默认值127.0.0.1");
                registration.IpAddress = "127.0.0.1";
            }
            return true;
        }

        private async Task<RegistrationResponse?> ExecuteRegistrationWithRetryAsync(
            NodeRegistration nodeRegistration,
            CancellationToken cancellationToken)
        {
            var maxRetryAttempts = _grpcSettings.MaxRetryAttempts;
            var retryDelayMs = _grpcSettings.RetryDelayMilliseconds;
            RegistrationResponse? response = null;

            using var totalTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(maxRetryAttempts * 10));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, totalTimeoutCts.Token);

            for (var attempt = 1; attempt <= maxRetryAttempts; attempt++)
            {
                linkedCts.Token.ThrowIfCancellationRequested();

                try
                {
                    LogHelper.Info($"开始节点注册，第{attempt}次尝试，NodeId: {nodeRegistration.NodeId}");

                    var headers = new Metadata
                    {
                        { "authorization", $"Bearer {_grpcSettings.NodeAuthToken}" },
                        { "node-id", nodeRegistration.NodeId }
                    };

                    var deadline = DateTime.UtcNow.AddSeconds(10);
                    response = await _grpcClientManager.CoordinatorClient.RegisterNodeAsync(
                        nodeRegistration,
                        headers,
                        deadline: deadline,
                        cancellationToken: linkedCts.Token).ConfigureAwait(false);

                    if (response.Success)
                    {
                        LogHelper.Info($"节点注册成功，第{attempt}次尝试完成");
                        return response;
                    }

                    LogHelper.Error($"节点注册被拒绝，原因: {response.Message}");
                    _sessionManager.ClearSession();
                    return response;
                }
                catch (RpcException ex) when (ex.StatusCode is StatusCode.Unauthenticated or StatusCode.AlreadyExists or StatusCode.InvalidArgument)
                {
                    LogHelper.Fatal(ex, "节点注册发生不可重试异常，终止重试");
                    _sessionManager.ClearSession();
                    return null;
                }
                catch (Exception ex)
                {
                    LogHelper.Warn($"节点注册第{attempt}次尝试失败");

                    if (attempt >= maxRetryAttempts)
                    {
                        LogHelper.Fatal(ex, "节点注册达到最大重试次数，注册失败");
                        _sessionManager.ClearSession();
                        return null;
                    }

                    await Task.Delay(retryDelayMs, linkedCts.Token).ConfigureAwait(false);
                }
            }

            return response;
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            LogHelper.Info("Worker节点开始优雅关闭");

            // 先停止任务消费循环，取消所有正在执行的任务
            try { _taskExecutionCts?.Cancel(); } catch { }

            // 等待所有任务释放信号量
            for (int i = 0; i < _crawlerSettings.MaxConcurrentTasks; i++)
            {
                try
                {
                    await _taskConcurrencySemaphore.WaitAsync(100, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    LogHelper.Warn("等待信号量超时，继续关闭流程");
                    break;
                }
            }

            // 清理流资源与gRPC客户端
            await CleanupStreamResourcesAsync().ConfigureAwait(false);
            await _grpcClientManager.ShutdownAsync(cancellationToken).ConfigureAwait(false);
            _sessionManager.ClearSession();

            await base.StopAsync(cancellationToken).ConfigureAwait(false);

            LogHelper.Info("Worker节点优雅关闭完成");
        }
        #endregion

        #region Dispose模式
        public void Dispose()
        {
            if (_disposed) return;

            // 先取消爬虫引擎事件订阅，防止在清理过程中触发事件
            _crawlerEngine.ProgressUpdated -= OnCrawlerEngineProgressUpdated;

            lock (_eventLock)
            {
                _nodeRegistered = null;
                _streamConnected = null;
                _streamDisconnected = null;
                _newTaskReceived = null;
                _taskControlReceived = null;
                _systemConfigReceived = null;
            }

            // 清理资源
            CleanupStreamResourcesAsync().Wait();
            _grpcClientManager.Dispose();
            _taskConcurrencySemaphore.Dispose();
            _taskExecutionCts?.Dispose();
            _globalCts?.Dispose();

            _disposed = true;
            GC.SuppressFinalize(this);
        }

        ~WorkerHostedService()
        {
            Dispose();
        }
        #endregion
    }
}