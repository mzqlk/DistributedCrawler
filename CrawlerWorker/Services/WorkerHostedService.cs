using CrawlerCore.Common;
using CrawlerCore.Events;
using CrawlerCore.Infrastructure;
using CrawlerCore.Infrastructure.Constants;
using CrawlerCore.Protos;
using CrawlerWorker.Communication;
using CrawlerWorker.Services.Engine;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using CrawlerTask = CrawlerCore.Entities.CrawlerTask;
using TaskStatus = CrawlerCore.Entities.TaskStatus;

namespace CrawlerWorker.Services
{
    /// <summary>
    /// Worker 核心生命周期托管服务（T5 重写，W-01、W-02、W-03、W-09 – W-11、W-22、W-23）。
    /// <list type="bullet">
    /// <item><b>连接</b>：一个长期运行的连接循环（<see cref="RunConnectionLoopAsync"/>）：确保已注册 → 建流 → 等 Master 确认（响应头）→
    /// 读、写、心跳三个循环 → 流结束后按状态码决定退避重连、立即重新注册或退出。每条流的资源都是本轮循环的局部变量。</item>
    /// <item><b>发送</b>：所有消息经进程级发件箱（<see cref="WorkerOutbox"/>）由当前流的单一写循环写出；任务结果保留到收到回执，
    /// 断线重连后重发。Master 的消息在读循环中按到达顺序处理。</item>
    /// <item><b>任务</b>：收到任务时按 TaskId 去重后进入本地队列（不再上报 Pending），由消费循环按并发上限执行；
    /// 结果先于 Completed 进度发送。</item>
    /// <item><b>停止</b>：不再接收任务、不再重连 → 取消执行中的任务（不上报任何终态，任务在 Master 上保持 Running，
    /// 节点离线后由故障恢复交给其他节点）→ 等任务结束 → 等已完成任务的回执 → 写完发件箱、结束请求流 → 关闭通道。</item>
    /// </list>
    /// </summary>
    public sealed class WorkerHostedService : BackgroundService
    {
        private static readonly TimeSpan TaskShutdownTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan AckShutdownTimeout = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan StreamCloseTimeout = TimeSpan.FromMilliseconds(GrpcConstants.DefaultStreamCloseTimeoutMs);

        #region 依赖注入
        private readonly IGrpcClientManager _grpcClientManager;
        private readonly INodeInfoCollector _nodeInfoCollector;
        private readonly IWorkerSessionManager _sessionManager;
        private readonly GrpcSettings _grpcSettings;
        private readonly CrawlerSettings _crawlerSettings;
        private readonly SignalRSettings _signalRSettings;
        private readonly IHostApplicationLifetime _appLifetime;
        private readonly ICrawlerEngine _crawlerEngine;
        private readonly WorkerOutbox _outbox;
        #endregion

        #region 任务
        private readonly Channel<CrawlerTask> _taskQueue = System.Threading.Channels.Channel.CreateUnbounded<CrawlerTask>(new UnboundedChannelOptions { SingleReader = true });
        /// <summary>已接收的任务（排队中与执行中），按 TaskId 去重（W-23）；T9 的暂停/取消也用它查找任务</summary>
        private readonly ConcurrentDictionary<string, TaskExecutionState> _acceptedTasks = new();
        private readonly ConcurrentDictionary<string, Task> _executions = new();
        private readonly SemaphoreSlim _taskConcurrency;
        private readonly CancellationTokenSource _taskExecutionCts = new();
        private Task? _consumerTask;
        #endregion

        #region 停止
        private readonly CancellationTokenSource _noReconnectCts = new();
        private readonly TaskCompletionSource _shutdownRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _stopping;
        #endregion

        public WorkerHostedService(
            IGrpcClientManager grpcClientManager,
            INodeInfoCollector nodeInfoCollector,
            IWorkerSessionManager sessionManager,
            IOptions<GrpcSettings> grpcSettings,
            IOptions<CrawlerSettings> crawlerSettings,
            IOptions<SignalRSettings> signalRSettings,
            IHostApplicationLifetime appLifetime,
            ICrawlerEngine crawlerEngine,
            WorkerOutbox outbox)
        {
            _grpcClientManager = grpcClientManager ?? throw new ArgumentNullException(nameof(grpcClientManager));
            _nodeInfoCollector = nodeInfoCollector ?? throw new ArgumentNullException(nameof(nodeInfoCollector));
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
            _grpcSettings = grpcSettings.Value ?? throw new ArgumentNullException(nameof(grpcSettings));
            _crawlerSettings = crawlerSettings.Value ?? throw new ArgumentNullException(nameof(crawlerSettings));
            _signalRSettings = signalRSettings.Value ?? throw new ArgumentNullException(nameof(signalRSettings));
            _appLifetime = appLifetime ?? throw new ArgumentNullException(nameof(appLifetime));
            _crawlerEngine = crawlerEngine ?? throw new ArgumentNullException(nameof(crawlerEngine));
            _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));

            var maxConcurrentTasks = Math.Max(1, _crawlerSettings.MaxConcurrentTasks);
            _taskConcurrency = new SemaphoreSlim(maxConcurrentTasks, maxConcurrentTasks);
            _crawlerEngine.ProgressUpdated += OnCrawlerEngineProgressUpdated;
        }

        /// <summary>已接收（排队中或执行中）的任务数</summary>
        public int AcceptedTaskCount => _acceptedTasks.Count;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            LogHelper.Info($"Worker节点启动，进程ID: {Environment.ProcessId}，NodeId: {_nodeInfoCollector.GetNodeId()}，最大并发任务数: {_taskConcurrency.CurrentCount}");
            _consumerTask = Task.Run(() => RunTaskConsumerAsync(_taskExecutionCts.Token), CancellationToken.None);

            try
            {
                await RunConnectionLoopAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested || _stopping)
            {
                // 正常停止
            }
            catch (Exception ex)
            {
                LogHelper.Fatal(ex, "Worker 连接循环发生未预期的异常，Worker 退出");
                ExitWithError();
            }
            LogHelper.Info("Worker 连接循环已结束");
        }

        #region 连接循环
        /// <summary>
        /// 连接循环：确保已注册 → 建流 → 等流结束 → 决定退避重连、重新注册或退出（W-02、W-10、W-11）。
        /// 启动时 Master 不可达、注册被拒绝都只是退避后重试；只有令牌错误等配置问题才退出（退出码 1）
        /// </summary>
        private async Task RunConnectionLoopAsync(CancellationToken stoppingToken)
        {
            using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _noReconnectCts.Token);
            var loopToken = loopCts.Token;
            var failures = 0;

            while (!loopToken.IsCancellationRequested)
            {
                var freshlyRegistered = false;
                if (!_sessionManager.IsRegistered)
                {
                    var registration = await RegisterOnceAsync(loopToken).ConfigureAwait(false);
                    if (registration == RegistrationOutcome.Fatal)
                    {
                        ExitWithError();
                        return;
                    }
                    if (registration != RegistrationOutcome.Registered)
                    {
                        failures++;
                        await DelayBeforeRetryAsync(failures, loopToken).ConfigureAwait(false);
                        continue;
                    }
                    freshlyRegistered = true;
                }

                var outcome = await RunStreamAsync(stoppingToken, loopToken).ConfigureAwait(false);
                if (outcome.Accepted)
                {
                    // 只有 Master 确认接受了流，才认为连接恢复正常
                    failures = 0;
                }
                if (loopToken.IsCancellationRequested || _stopping)
                {
                    return;
                }

                switch (ConnectionPolicy.ClassifyStreamEnd(outcome.Status))
                {
                    case StreamEndAction.Exit:
                        LogHelper.Fatal($"Master 拒绝了节点令牌（{outcome.Status}），请检查 NODE_AUTH_TOKEN / GrpcSettings:NodeAuthToken 是否与 Master 一致");
                        ExitWithError();
                        return;

                    // 会话失效（Master 重启后旧会话不存在）：立即重新注册，不计入失败次数。
                    // 刚注册的会话马上又被拒绝说明 Master 状态异常，按普通失败退避，避免空转
                    case StreamEndAction.ReRegister when !freshlyRegistered || outcome.Accepted:
                        LogHelper.Warn($"会话已失效（{outcome.Status}，例如 Master 重启过），立即重新注册");
                        _sessionManager.ClearSession();
                        continue;

                    default:
                        failures++;
                        if (failures > _grpcSettings.MaxRetryAttempts && _sessionManager.IsRegistered)
                        {
                            LogHelper.Warn($"连续 {failures} 次未能建立双向流，清除会话后重新注册");
                            _sessionManager.ClearSession();
                        }
                        RecreateChannelSafely();
                        await DelayBeforeRetryAsync(failures, loopToken).ConfigureAwait(false);
                        break;
                }
            }
        }

        private enum RegistrationOutcome { Registered, Retry, Fatal }

        /// <summary>注册一次：成功保存会话；令牌错误、参数非法返回 Fatal；其余失败（包括 Master 不可达）返回 Retry</summary>
        private async Task<RegistrationOutcome> RegisterOnceAsync(CancellationToken cancellationToken)
        {
            NodeRegistration registration;
            try
            {
                registration = await _nodeInfoCollector.CollectNodeRegistrationAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return RegistrationOutcome.Retry;
            }

            if (!ValidateNodeRegistration(registration))
            {
                LogHelper.Fatal("节点注册参数校验失败，Worker 退出");
                return RegistrationOutcome.Fatal;
            }

            try
            {
                var headers = new Metadata
                {
                    { "authorization", $"Bearer {_grpcSettings.NodeAuthToken}" },
                    { "node-id", registration.NodeId }
                };
                var response = await _grpcClientManager.CoordinatorClient.RegisterNodeAsync(
                    registration,
                    headers,
                    deadline: DateTime.UtcNow.AddSeconds(Math.Max(1, _grpcSettings.MasterResponseTimeoutSeconds)),
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (response.Success)
                {
                    _sessionManager.SaveSession(response);
                    LogHelper.Info($"节点注册成功：NodeId={registration.NodeId}，并发上限={registration.MaxConcurrentTasks}");
                    return RegistrationOutcome.Registered;
                }

                // 例如同一 NodeId 的另一个 Worker 仍在线：稍后重试，不退出
                LogHelper.Warn($"节点注册被 Master 拒绝：{response.Message}，稍后重试");
                return RegistrationOutcome.Retry;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return RegistrationOutcome.Retry;
            }
            catch (RpcException) when (cancellationToken.IsCancellationRequested)
            {
                return RegistrationOutcome.Retry;
            }
            catch (RpcException ex) when (ConnectionPolicy.IsFatalRegistrationError(ex.StatusCode))
            {
                LogHelper.Fatal($"节点注册失败（{ex.StatusCode}: {ex.Status.Detail}），这是配置错误，重试没有意义。"
                    + "请检查 NODE_AUTH_TOKEN / GrpcSettings:NodeAuthToken 是否与 Master 一致");
                return RegistrationOutcome.Fatal;
            }
            catch (Exception ex)
            {
                LogHelper.Warn($"节点注册失败（{DescribeError(ex)}），Master 地址 {_grpcSettings.MasterEndpoint}，稍后重试");
                return RegistrationOutcome.Retry;
            }
        }

        private readonly record struct StreamOutcome(bool Accepted, StatusCode? Status);

        /// <summary>
        /// 运行一条双向流直到它结束。<paramref name="stoppingToken"/> 硬性终止流；<paramref name="loopToken"/>（停止时取消）
        /// 只打断建流等待，已建立的流收到停止信号后先写完发件箱再关闭
        /// </summary>
        private async Task<StreamOutcome> RunStreamAsync(CancellationToken stoppingToken, CancellationToken loopToken)
        {
            using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var headers = new Metadata
            {
                { "node-id", _nodeInfoCollector.GetNodeId() },
                { "session-id", _sessionManager.SessionId ?? string.Empty },
                { "authorization", $"Bearer {_grpcSettings.NodeAuthToken}" }
            };

            AsyncDuplexStreamingCall<FromWorker, FromMaster> call;
            try
            {
                call = _grpcClientManager.CoordinatorClient.StartStreaming(headers, cancellationToken: streamCts.Token);
            }
            catch (Exception ex)
            {
                LogHelper.Warn($"建立双向流失败: {DescribeError(ex)}");
                return new StreamOutcome(false, StatusCode.Unavailable);
            }

            using (call)
            {
                // 1. 等 Master 确认（校验会话并登记流后立即发送响应头）
                try
                {
                    await call.ResponseHeadersAsync
                        .WaitAsync(TimeSpan.FromSeconds(Math.Max(1, _grpcSettings.MasterResponseTimeoutSeconds)), loopToken)
                        .ConfigureAwait(false);
                }
                catch (RpcException ex)
                {
                    LogHelper.Warn($"Master 拒绝了双向流: {ex.StatusCode} {ex.Status.Detail}");
                    return new StreamOutcome(false, ex.StatusCode);
                }
                catch (TimeoutException)
                {
                    LogHelper.Warn($"Master 在 {_grpcSettings.MasterResponseTimeoutSeconds} 秒内没有确认双向流，放弃这次连接");
                    return new StreamOutcome(false, StatusCode.DeadlineExceeded);
                }
                catch (OperationCanceledException)
                {
                    return new StreamOutcome(false, null);
                }
                catch (Exception ex)
                {
                    LogHelper.Warn($"等待 Master 确认双向流时出错: {DescribeError(ex)}");
                    return new StreamOutcome(false, StatusCode.Unavailable);
                }

                LogHelper.Info("双向流已建立（Master 已确认）");
                _outbox.OnStreamAccepted();

                // 2. 读、写、心跳三个循环，任一结束即关闭整条流
                var readTask = ReadLoopAsync(call.ResponseStream, streamCts.Token);
                var writeTask = WriteLoopAsync(call.RequestStream, streamCts.Token);
                var heartbeatTask = HeartbeatLoopAsync(streamCts.Token);

                var first = await Task.WhenAny(readTask, writeTask, heartbeatTask, _shutdownRequested.Task).ConfigureAwait(false);
                if (first == _shutdownRequested.Task)
                {
                    await CloseStreamGracefullyAsync(call, readTask, writeTask).ConfigureAwait(false);
                }

                _outbox.OnStreamClosed();
                streamCts.Cancel();

                var status = await ObserveStreamEndAsync(readTask, writeTask, heartbeatTask).ConfigureAwait(false);
                if (!_stopping)
                {
                    LogHelper.Warn($"双向流已断开（{status?.ToString() ?? "Master 关闭了流"}）");
                }
                return new StreamOutcome(true, status);
            }
        }

        /// <summary>
        /// 停止时关闭流：写完发件箱中已有的消息（发件箱已在 StopAsync 中 Complete），结束请求流，
        /// 等 Master 处理完并关闭流（Master 随后把节点判为离线并重新调度它的任务）
        /// </summary>
        private static async Task CloseStreamGracefullyAsync(AsyncDuplexStreamingCall<FromWorker, FromMaster> call, Task readTask, Task writeTask)
        {
            if (!await CompletesWithinAsync(writeTask, StreamCloseTimeout).ConfigureAwait(false))
            {
                LogHelper.Warn("停止时发件箱未能在限定时间内写完，剩余消息丢弃");
                return;
            }
            if (writeTask.IsFaulted || writeTask.IsCanceled)
            {
                return;
            }

            try
            {
                await call.RequestStream.CompleteAsync().WaitAsync(StreamCloseTimeout).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogHelper.Debug($"结束请求流失败: {DescribeError(ex)}");
                return;
            }

            if (!await CompletesWithinAsync(readTask, StreamCloseTimeout).ConfigureAwait(false))
            {
                LogHelper.Debug("Master 未在限定时间内关闭双向流，直接取消");
            }
        }

        /// <summary>等待三个循环全部结束，返回流结束的原因（第一个有意义的状态码）</summary>
        private static async Task<StatusCode?> ObserveStreamEndAsync(params Task[] loops)
        {
            StatusCode? status = null;
            foreach (var loop in loops)
            {
                try
                {
                    await loop.ConfigureAwait(false);
                }
                catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
                {
                    // 本端取消，不是断开原因
                }
                catch (RpcException ex)
                {
                    status ??= ex.StatusCode;
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidOperationException or ObjectDisposedException)
                {
                    status ??= StatusCode.Unavailable;
                }
                catch (Exception ex)
                {
                    LogHelper.Error(ex, "双向流循环异常结束");
                    status ??= StatusCode.Unknown;
                }
            }
            return status;
        }

        private async Task DelayBeforeRetryAsync(int failures, CancellationToken cancellationToken)
        {
            var delay = ConnectionPolicy.ComputeBackoff(
                failures,
                TimeSpan.FromMilliseconds(Math.Max(1, _grpcSettings.RetryDelayMilliseconds)),
                TimeSpan.FromSeconds(Math.Max(1, _grpcSettings.MaxRetryDelaySeconds)),
                Random.Shared.NextDouble());
            LogHelper.Info($"{delay.TotalSeconds:0.#} 秒后重试连接 Master（连续失败 {failures} 次）");
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        private void RecreateChannelSafely()
        {
            try
            {
                _grpcClientManager.RecreateChannel();
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
            {
                // 正在停止
            }
        }

        private void ExitWithError()
        {
            Environment.ExitCode = 1;
            _appLifetime.StopApplication();
        }
        #endregion

        #region 读、写、心跳循环
        /// <summary>读循环：Master 的消息按到达顺序逐条处理，单条消息处理出错不影响流</summary>
        private async Task ReadLoopAsync(IAsyncStreamReader<FromMaster> responseStream, CancellationToken cancellationToken)
        {
            await foreach (var message in responseStream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    HandleMasterMessage(message);
                }
                catch (Exception ex)
                {
                    LogHelper.Error(ex, $"处理 Master 消息失败: {message.MessageCase}");
                }
            }
        }

        private void HandleMasterMessage(FromMaster message)
        {
            switch (message.MessageCase)
            {
                case FromMaster.MessageOneofCase.NewTask:
                    ProcessNewTask(ToEntity(message.NewTask));
                    break;

                case FromMaster.MessageOneofCase.ResultAck:
                    var ack = message.ResultAck;
                    if (_outbox.Acknowledge(ack.TaskId))
                    {
                        if (ack.Accepted)
                            LogHelper.Info($"Master 已确认任务 {ack.TaskId} 的结果");
                        else
                            LogHelper.Warn($"Master 丢弃了任务 {ack.TaskId} 的结果（任务已改派或不存在），不再重发");
                    }
                    break;

                case FromMaster.MessageOneofCase.TaskControl:
                    // 暂停/取消由路线图 T9 实现
                    LogHelper.Warn($"收到任务控制指令（尚未支持，忽略）: TaskId={message.TaskControl.TaskId}, Command={message.TaskControl.Command}");
                    break;

                case FromMaster.MessageOneofCase.SystemConfig:
                    LogHelper.Info("收到 Master 下发的系统配置（Worker 以本地配置为准）");
                    break;

                default:
                    LogHelper.Warn($"收到 Master 未知类型消息: {message.MessageCase}");
                    break;
            }
        }

        /// <summary>写循环：从发件箱依次取消息写入流（所有写入都经过这里，保证串行）</summary>
        private async Task WriteLoopAsync(IClientStreamWriter<FromWorker> requestStream, CancellationToken cancellationToken)
        {
            while (true)
            {
                var item = await _outbox.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (item == null)
                {
                    return; // 发件箱已关闭（停止中）且已写完
                }

                try
                {
                    await requestStream.WriteAsync(item.Message, cancellationToken).ConfigureAwait(false);
                    _outbox.OnWritten(item);
                }
                catch
                {
                    _outbox.OnWriteFailed(item);
                    throw;
                }
            }
        }

        /// <summary>心跳循环：按间隔上报心跳，并重发超过回执超时的结果</summary>
        private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, _grpcSettings.HeartbeatIntervalSeconds)));
            var ackTimeout = TimeSpan.FromSeconds(Math.Max(1, _grpcSettings.ResultAckTimeoutSeconds));
            do
            {
                try
                {
                    var heartbeat = await _nodeInfoCollector.CollectHeartbeatAsync(cancellationToken).ConfigureAwait(false);
                    _outbox.TrySendTransient(new FromWorker { Heartbeat = heartbeat });
                    _outbox.ResendUnacknowledged(ackTimeout);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // 单次采集失败不中断流
                    LogHelper.Warn($"心跳采集失败: {ex.Message}");
                }
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        #endregion

        #region 任务接收与执行
        /// <summary>
        /// 收到新任务：按 TaskId 去重（W-23）后放入本地队列。不上报 Pending（W-22）：Master 下发前已把任务写为 Running
        /// </summary>
        internal void ProcessNewTask(CrawlerTask task)
        {
            if (string.IsNullOrWhiteSpace(task.TaskId))
            {
                LogHelper.Warn("收到没有 TaskId 的任务，忽略");
                return;
            }
            if (_stopping)
            {
                LogHelper.Warn($"Worker 正在停止，不接收任务 {task.TaskId}（Master 判定节点离线后会重新调度）");
                return;
            }

            if (_acceptedTasks.TryGetValue(task.TaskId, out var existing))
            {
                LogHelper.Warn($"任务 {task.TaskId} 已在本节点{(existing.Started ? "执行" : "排队")}，忽略重复下发");
                if (existing.Started)
                {
                    // 让 Master 的状态与实际一致（例如任务被重置后又下发回本节点）
                    SendRunningProgress(existing, force: true);
                }
                return;
            }
            if (_outbox.TryResendResult(task.TaskId))
            {
                LogHelper.Warn($"任务 {task.TaskId} 已在本节点执行完毕、结果待确认，补发结果而不是重新执行");
                return;
            }

            var state = new TaskExecutionState(task);
            if (!_acceptedTasks.TryAdd(task.TaskId, state))
            {
                return;
            }
            _sessionManager.IncrementActiveTasks();
            if (!_taskQueue.Writer.TryWrite(task))
            {
                _acceptedTasks.TryRemove(task.TaskId, out _);
                _sessionManager.DecrementActiveTasks();
                return;
            }
            LogHelper.Info($"收到任务: TaskId={task.TaskId}, 名称={task.TaskName}, 插件={task.PluginName}");
        }

        /// <summary>消费循环：按并发上限依次启动任务</summary>
        private async Task RunTaskConsumerAsync(CancellationToken cancellationToken)
        {
            try
            {
                await foreach (var task in _taskQueue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    await _taskConcurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
                    var execution = ExecuteTaskAsync(task, cancellationToken);
                    _executions[task.TaskId] = execution;
                    _ = execution.ContinueWith(_ => _executions.TryRemove(task.TaskId, out var _), TaskScheduler.Default);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 停止：队列中尚未开始的任务不上报，Master 判定节点离线后会重新调度
            }
        }

        /// <summary>
        /// 执行一个任务。Worker 停止导致的取消不上报任何终态（W-01）：任务在 Master 上保持 Running，
        /// 节点离线后由故障恢复重新调度。T9 实现用户取消时再上报专门的状态
        /// </summary>
        private async Task ExecuteTaskAsync(CrawlerTask task, CancellationToken stoppingToken)
        {
            await Task.Yield();
            var state = _acceptedTasks.GetValueOrDefault(task.TaskId) ?? new TaskExecutionState(task);
            using var taskCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            state.Cancellation = taskCts;
            try
            {
                LogHelper.Info($"开始执行任务，TaskId: {task.TaskId}, 任务名称: {task.TaskName}, 插件: {task.PluginName}");
                task.Status = TaskStatus.Running;
                task.AssignedNode = _nodeInfoCollector.GetNodeId();
                state.Started = true;
                SendRunningProgress(state, force: true);

                var crawlResult = await _crawlerEngine.ExecuteAsync(task, taskCts.Token).ConfigureAwait(false);

                LogHelper.Info($"任务执行完成，TaskId: {task.TaskId}, 总爬取页面数: {crawlResult.PageResults.Count}");
                EnqueueCompletedResult(task, crawlResult);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                LogHelper.Info($"Worker 正在停止，任务 {task.TaskId} 中止，不上报终态（Master 判定节点离线后会重新调度）");
            }
            catch (Exception ex)
            {
                LogHelper.Error(ex, $"任务执行失败，TaskId: {task.TaskId}");
                EnqueueFailedResult(task, state, ex.Message);
            }
            finally
            {
                state.Cancellation = null;
                _acceptedTasks.TryRemove(task.TaskId, out _);
                _sessionManager.DecrementActiveTasks();
                try
                {
                    _taskConcurrency.Release();
                }
                catch (ObjectDisposedException)
                {
                    // 服务已释放（停止超时后仍在运行的任务）
                }
            }
        }

        /// <summary>完成：先结果、后 Completed 进度（W-27），保留到收到回执</summary>
        private void EnqueueCompletedResult(CrawlerTask task, CrawlerCore.Entities.CrawlResult crawlResult)
        {
            var result = new CrawlerCore.Protos.CrawlResult
            {
                TaskId = task.TaskId,
                CompletedAt = Timestamp.FromDateTime((crawlResult.CompletedAt ?? DateTime.UtcNow).ToUniversalTime())
            };
            foreach (var page in crawlResult.PageResults)
            {
                var protoPage = new CrawlerCore.Protos.PageResult
                {
                    Url = page.Url ?? string.Empty,
                    Finalurl = page.FinalUrl ?? string.Empty,
                    StatusCode = page.StatusCode
                };
                protoPage.ParsedData.Add(ParsedDataConverter.ToProtoMap(page.ParsedData));
                if (page.Links is { Count: > 0 })
                {
                    protoPage.Links.Add(page.Links);
                }
                result.PageResults.Add(protoPage);
            }

            var pages = crawlResult.PageResults.Count;
            _outbox.EnqueueResult(task.TaskId,
                [new FromWorker { CrawlResult = result }, new FromWorker { TaskProgress = BuildProgress(task, pages, pages, TaskStatus.Completed) }],
                requiresAck: true);
        }

        /// <summary>失败：Failed 进度（Master 不回执，写出后即完成）</summary>
        private void EnqueueFailedResult(CrawlerTask task, TaskExecutionState state, string errorMessage)
        {
            var progress = BuildProgress(task, state.CrawledPages, task.MaxPages, TaskStatus.Failed);
            progress.ErrorMessage = errorMessage ?? string.Empty;
            _outbox.EnqueueResult(task.TaskId, [new FromWorker { TaskProgress = progress }], requiresAck: false);
        }
        #endregion

        #region 进度
        /// <summary>
        /// 引擎进度：运行中的进度按间隔节流（每个任务的第一条总会上报，W-26），作为即时消息发送；
        /// 最终状态由 <see cref="ExecuteTaskAsync"/> 可靠上报，这里不发
        /// </summary>
        private void OnCrawlerEngineProgressUpdated(object? sender, TaskProgressUpdatedEventArgs e)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(e.TaskId) || e.CurrentStatus is TaskStatus.Completed or TaskStatus.Failed)
                {
                    return;
                }
                if (!_acceptedTasks.TryGetValue(e.TaskId, out var state))
                {
                    return;
                }
                state.CrawledPages = e.CrawledPages;
                state.TotalPages = e.TotalPages;
                SendRunningProgress(state, force: false);
            }
            catch (Exception ex)
            {
                LogHelper.Error(ex, "进度上报事件处理异常");
            }
        }

        private void SendRunningProgress(TaskExecutionState state, bool force)
        {
            var throttle = TimeSpan.FromSeconds(Math.Max(0, _signalRSettings.TaskProgressThrottleSeconds));
            if (!state.TryBeginReport(DateTime.UtcNow, throttle, force))
            {
                return;
            }
            var total = state.TotalPages > 0 ? state.TotalPages : state.Task.MaxPages;
            _outbox.TrySendTransient(new FromWorker { TaskProgress = BuildProgress(state.Task, state.CrawledPages, total, TaskStatus.Running) });
        }

        private TaskProgress BuildProgress(CrawlerTask task, int crawledPages, int totalPages, TaskStatus status) => new()
        {
            TaskId = task.TaskId,
            TaskName = task.TaskName,
            CrawledPages = crawledPages,
            TotalPages = Math.Max(0, totalPages),
            CurrentStatus = (CrawlerCore.Protos.TaskStatus)status,
            AssignedNodeId = _nodeInfoCollector.GetNodeId()
        };
        #endregion

        #region 辅助方法
        private static CrawlerTask ToEntity(CrawlerCore.Protos.CrawlerTask protoTask) => new()
        {
            TaskId = protoTask.TaskId,
            TaskName = protoTask.TaskName,
            PluginName = protoTask.PluginName,
            MaxDepth = protoTask.MaxDepth,
            MaxPages = protoTask.MaxPages,
            Priority = protoTask.Priority,
            Status = TaskStatus.Pending,
            StartUrls = protoTask.StartUrls.ToList(),
            Configuration = protoTask.Configuration.ToDictionary(kv => kv.Key, kv => kv.Value)
        };

        private static bool ValidateNodeRegistration(NodeRegistration registration)
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

        private static string DescribeError(Exception ex) => ex switch
        {
            RpcException rpc => $"{rpc.StatusCode}: {rpc.Status.Detail}",
            _ => $"{ex.GetType().Name}: {ex.Message}"
        };

        private static async Task<bool> CompletesWithinAsync(Task task, TimeSpan timeout)
        {
            var finished = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
            return finished == task;
        }

        /// <summary>等待任务结束，超时或停止令牌取消时放弃等待；任务自身的异常只记录</summary>
        private static async Task WaitSafelyAsync(Task task, TimeSpan timeout, CancellationToken cancellationToken, string description)
        {
            try
            {
                var finished = await Task.WhenAny(task, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);
                if (finished != task)
                {
                    LogHelper.Warn($"{description}超时，继续停止流程");
                    return;
                }
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                LogHelper.Warn($"{description}时出错: {ex.Message}");
            }
        }
        #endregion

        #region 停止与释放
        /// <summary>
        /// 停止顺序（W-01 ③、W-09）：不再接收任务、不再重连 → 取消执行中的任务并等它们结束 → 等已完成任务的结果回执 →
        /// 写完发件箱、结束请求流、等 Master 关闭流 → 停止后台循环 → 关闭 gRPC 通道。每一步各自限时并捕获异常
        /// </summary>
        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            LogHelper.Info("Worker节点开始优雅关闭");
            _stopping = true;
            _taskQueue.Writer.TryComplete();

            try
            {
                // 1. 取消执行中的任务（不上报终态），等它们结束
                _taskExecutionCts.Cancel();
                var running = _executions.Values.Append(_consumerTask ?? Task.CompletedTask).ToArray();
                await WaitSafelyAsync(Task.WhenAll(running), TaskShutdownTimeout, cancellationToken, "等待任务结束").ConfigureAwait(false);

                // 2. 已完成任务的结果尽量等到回执，避免重启后重做
                if (_outbox.PendingResultCount > 0 && _outbox.IsConnected)
                {
                    var acknowledged = await _outbox.WaitForAcknowledgementsAsync(AckShutdownTimeout, cancellationToken).ConfigureAwait(false);
                    if (!acknowledged)
                    {
                        LogHelper.Warn($"仍有 {_outbox.PendingResultCount} 个任务结果未收到回执，Master 会在节点离线后重新调度这些任务");
                    }
                }

                // 3. 不再重连；当前流写完发件箱后关闭
                _outbox.Complete();
                _noReconnectCts.Cancel();
                _shutdownRequested.TrySetResult();
                await WaitSafelyAsync(ExecuteTask ?? Task.CompletedTask, StreamCloseTimeout * 3, cancellationToken, "等待双向流关闭").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogHelper.Error(ex, "Worker 停止过程中出错，继续关闭");
            }
            finally
            {
                // 4. 停止后台循环（取消 stoppingToken 并等待 ExecuteAsync 结束）
                try
                {
                    await base.StopAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogHelper.Warn($"停止后台循环时出错: {ex.Message}");
                }

                // 5. 关闭 gRPC 通道
                try
                {
                    await _grpcClientManager.ShutdownAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogHelper.Warn($"关闭 gRPC 通道时出错: {ex.Message}");
                }
                _sessionManager.ClearSession();
                LogHelper.Info("Worker节点优雅关闭完成");
            }
        }

        /// <summary>只做同步释放；异步清理都在 <see cref="StopAsync"/> 中完成（W-09）</summary>
        public override void Dispose()
        {
            _crawlerEngine.ProgressUpdated -= OnCrawlerEngineProgressUpdated;
            _taskExecutionCts.Cancel();
            _noReconnectCts.Cancel();
            _taskExecutionCts.Dispose();
            _noReconnectCts.Dispose();
            _taskConcurrency.Dispose();
            base.Dispose();
        }
        #endregion

        /// <summary>一个已接收任务的执行状态</summary>
        private sealed class TaskExecutionState
        {
            private readonly object _lock = new();
            private DateTime? _lastReport;
            private volatile bool _started;
            private volatile int _crawledPages;
            private volatile int _totalPages;

            public TaskExecutionState(CrawlerTask task)
            {
                Task = task;
            }

            public CrawlerTask Task { get; }
            public bool Started { get => _started; set => _started = value; }
            public int CrawledPages { get => _crawledPages; set => _crawledPages = value; }
            public int TotalPages { get => _totalPages; set => _totalPages = value; }

            /// <summary>任务自己的取消令牌源（与停止令牌联动），供 T9 的暂停/取消使用</summary>
            public CancellationTokenSource? Cancellation { get; set; }

            /// <summary>节流：第一次、强制、或距上次上报超过间隔时返回 true 并记下时间</summary>
            public bool TryBeginReport(DateTime now, TimeSpan throttle, bool force)
            {
                lock (_lock)
                {
                    if (!force && _lastReport.HasValue && now - _lastReport.Value < throttle)
                    {
                        return false;
                    }
                    _lastReport = now;
                    return true;
                }
            }
        }
    }
}
