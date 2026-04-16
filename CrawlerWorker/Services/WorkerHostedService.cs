using CrawlerCore.Infrastructure;
using CrawlerCore.Infrastructure.Constants;
using CrawlerCore.Protos;
using CrawlerWorker.Communication;
using CrawlerWorker.Services.Simulation;
using Grpc.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Channel = System.Threading.Channels.Channel;

namespace CrawlerWorker.Services
{
    /// <summary>
    /// Worker核心生命周期托管服务
    /// </summary>
    public class WorkerHostedService : BackgroundService, IDisposable
    {
        private readonly IGrpcClientManager _grpcClientManager;
        private readonly INodeInfoCollector _nodeInfoCollector;
        private readonly IWorkerSessionManager _sessionManager;
        private readonly GrpcSettings _grpcSettings;
        private readonly CrawlerSettings _crawlerSettings;
        private readonly IHostApplicationLifetime _appLifetime;
        private readonly object _streamLock = new();
        private readonly object _eventLock = new();
        private bool _disposed = false;
        private readonly ConcurrentQueue<CrawlerCore.Entities.CrawlerTask> _localTaskQueue = new();

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
            IHostApplicationLifetime appLifetime)
        {
            _grpcClientManager = grpcClientManager ?? throw new ArgumentNullException(nameof(grpcClientManager));
            _nodeInfoCollector = nodeInfoCollector ?? throw new ArgumentNullException(nameof(nodeInfoCollector));
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
            _grpcSettings = grpcSettings.Value ?? throw new ArgumentNullException(nameof(grpcSettings));
            _crawlerSettings = crawlerSettings.Value ?? throw new ArgumentNullException(nameof(crawlerSettings));
            _appLifetime = appLifetime ?? throw new ArgumentNullException(nameof(appLifetime));

            _nodeRegistered += OnNodeRegistered_StartStreamingAsync;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // 绑定全局停止令牌：所有后台任务统一绑定
            _globalCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

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

        #region 双向流核心实现
        private async Task OnNodeRegistered_StartStreamingAsync(
            NodeRegistration registration,
            RegistrationResponse response,
            CancellationToken cancellationToken)
        {
            LogHelper.Debug("节点注册成功事件触发，准备启动双向流");
            _reconnectAttempts = 0;
            await StartStreamingAsync(cancellationToken).ConfigureAwait(false);
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
                _sendChannel = Channel.CreateUnbounded<FromWorker>(new UnboundedChannelOptions
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

                            var crawlerTask = new CrawlerCore.Entities.CrawlerTask
                            {
                                TaskId = protoTask.TaskId,
                                TaskName = protoTask.TaskName,
                                PluginName = protoTask.PluginName,
                                MaxDepth = protoTask.MaxDepth,
                                MaxPages = protoTask.MaxPages,
                                Priority = protoTask.Priority,
                                Status = (CrawlerCore.Entities.TaskStatus)protoTask.Status,
                                StartUrls = protoTask.StartUrls.ToList(),
                                Configuration = protoTask.Configuration.ToDictionary(kv => kv.Key, kv => kv.Value)
                            };

                            // 启动任务处理（不阻塞读循环）
                            _ = ProcessNewTaskAsync(crawlerTask);

                            // 触发外部事件（仅用于扩展）
                            var taskHandler = Volatile.Read(ref _newTaskReceived);
                            if (taskHandler != null)
                            {
                                foreach (Func<CrawlerTask, CancellationToken, Task> subscriber in taskHandler.GetInvocationList())
                                {
                                    try
                                    {
                                        await subscriber.Invoke(protoTask, cancellationToken).ConfigureAwait(false);
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

                    // 如果同步完成，立即返回 true（不会在 await 点释放锁之外的逻辑）
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

            // 在锁外等待异步完成，避免将锁保持到 await 点
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
            // 应用关闭时立即终止重连
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
        /// 处理新任务：入队 + 进度上报 + DEBUG模拟（开关控制）
        /// </summary>
        public async Task ProcessNewTaskAsync(CrawlerCore.Entities.CrawlerTask task)
        {
            _localTaskQueue.Enqueue(task);
            _sessionManager.IncrementActiveTasks();

            // 统一进度上报方法（复用SendMessageAsync日志）
            async Task SendProgressAsync(TaskProgress progress)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var msg = new FromWorker { TaskProgress = progress };
                await SendMessageAsync(msg, cts.Token).ConfigureAwait(false);
            }

            // 初始进度上报
            var initProgress = new TaskProgress
            {
                TaskId = task.TaskId,
                CrawledPages = 0,
                TotalPages = task.MaxPages,
                CurrentStatus = CrawlerCore.Protos.TaskStatus.Running
            };
            await SendProgressAsync(initProgress).ConfigureAwait(false);

//#if DEBUG
            // 开关控制 + 空值保护 + 绑定三重取消令牌（模拟自身+流关闭+Worker全局停止）
            if (_crawlerSettings.EnableProgressSimulation && _globalCts != null)
            {
                // 【核心修复】移除 using，避免方法结束时提前释放 CTS
                var simulateCts = new CancellationTokenSource();
                var linkedCts = _streamCts == null
                    ? CancellationTokenSource.CreateLinkedTokenSource(simulateCts.Token, _globalCts.Token)
                    : CancellationTokenSource.CreateLinkedTokenSource(simulateCts.Token, _streamCts.Token, _globalCts.Token);

                // 后台异步模拟，全异常捕获 + 手动释放CTS
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await TaskProgressSimulator.StartSimulationAsync(
                            task.TaskId,
                            task.MaxPages,
                            SendProgressAsync,
                            linkedCts.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        LogHelper.Error(ex, $"模拟后台任务崩溃 TaskId={task.TaskId}");
                    }
                    finally
                    {
                        // 【核心修复】在后台任务结束时手动释放 CTS，避免资源泄漏
                        linkedCts?.Dispose();
                        simulateCts?.Dispose();
                    }
                }, linkedCts.Token);
            }
//#endif
        }

        /// <summary>
        /// 尝试从本地队列获取下一个待执行任务
        /// </summary>
        public bool TryGetNextTask(out CrawlerCore.Entities.CrawlerTask? task)
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

            await CleanupStreamResourcesAsync().ConfigureAwait(false);
            await _grpcClientManager.ShutdownAsync(cancellationToken).ConfigureAwait(false);
            _sessionManager.ClearSession();
            await base.StopAsync(cancellationToken).ConfigureAwait(false);

            LogHelper.Info("Worker节点优雅关闭完成");
        }
        #endregion

        public void Dispose()
        {
            if (_disposed) return;

            lock (_eventLock)
            {
                _nodeRegistered = null;
                _streamConnected = null;
                _streamDisconnected = null;
                _newTaskReceived = null;
                _taskControlReceived = null;
                _systemConfigReceived = null;
            }

            CleanupStreamResourcesAsync().Wait();
            _grpcClientManager.Dispose();

            // 释放全局停止令牌
            _globalCts?.Dispose();
            _globalCts = null;

            _disposed = true;
            GC.SuppressFinalize(this);
        }

        ~WorkerHostedService()
        {
            Dispose();
        }
    }
}