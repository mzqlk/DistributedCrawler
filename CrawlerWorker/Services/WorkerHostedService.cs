using CrawlerCore.Infrastructure;
using CrawlerCore.Protos;
using CrawlerWorker.Communication;
using Grpc.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace CrawlerWorker.Services;

/// <summary>
/// Worker核心生命周期托管服务，严格对齐原项目BackgroundService设计
/// 注册流程严格遵循官方顺序，严守工作日13阶段边界：仅完成注册流程，不实现双向流/业务逻辑
/// </summary>
public class WorkerHostedService : BackgroundService
{
    private readonly IGrpcClientManager _grpcClientManager;
    private readonly INodeInfoCollector _nodeInfoCollector;
    private readonly IWorkerSessionManager _sessionManager;
    private readonly GrpcSettings _grpcSettings;
    private readonly ILogger<WorkerHostedService> _logger;
    private readonly IHostApplicationLifetime _appLifetime;

    // 严格遵循原项目「注册成功→启动双向流」的流程顺序，仅预留扩展入口，不实现逻辑
    // 【关键修复】标准事件定义，线程安全
    private event Func<NodeRegistration, RegistrationResponse, CancellationToken, Task>? _nodeRegistered;
    public event Func<NodeRegistration, RegistrationResponse, CancellationToken, Task> NodeRegistered
    {
        add => _nodeRegistered += value;
        remove => _nodeRegistered -= value;
    }

    public WorkerHostedService(
        IGrpcClientManager grpcClientManager,
        INodeInfoCollector nodeInfoCollector,
        IWorkerSessionManager sessionManager,
        IOptions<GrpcSettings> grpcSettings,
        ILogger<WorkerHostedService> logger,
        IHostApplicationLifetime appLifetime)
    {
        _grpcClientManager = grpcClientManager;
        _nodeInfoCollector = nodeInfoCollector;
        _sessionManager = sessionManager;
        _grpcSettings = grpcSettings.Value;
        _logger = logger;
        _appLifetime = appLifetime;
    }

    /// <summary>
    /// Worker核心执行流程，100%对齐原项目官方启动顺序
    /// 严守阶段边界：仅完成注册流程，不启动服务端监听，不实现双向流
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Worker节点启动，进程ID: {ProcessId}", Process.GetCurrentProcess().Id);

        try
        {
            // 步骤1：采集节点基础信息
            var nodeRegistration = await _nodeInfoCollector.CollectNodeRegistrationAsync(stoppingToken).ConfigureAwait(false);

            // 步骤2：注册请求前置参数校验，严格对齐proto必填字段
            if (!ValidateNodeRegistration(nodeRegistration))
            {
                _logger.LogCritical("节点注册参数校验失败，Worker启动终止");
                _appLifetime.StopApplication();
                return;
            }

            // 步骤3：Master端口连通性预检测
            var connectivity = await _grpcClientManager.CheckMasterConnectivityAsync(stoppingToken).ConfigureAwait(false);
            if (!connectivity)
            {
                _logger.LogCritical("Master端点不可达，Worker启动失败");
                _appLifetime.StopApplication();
                return;
            }

            // 步骤4：执行节点注册，带官方规范的重试机制
            var registrationResponse = await ExecuteRegistrationWithRetryAsync(nodeRegistration, stoppingToken).ConfigureAwait(false);
            if (registrationResponse == null || !registrationResponse.Success)
            {
                _logger.LogCritical("节点注册失败，Worker启动失败");
                _appLifetime.StopApplication();
                return;
            }

            // 步骤5：保存会话信息
            _sessionManager.SaveSession(registrationResponse);
            _logger.LogInformation("节点注册成功，SessionId: {SessionId}", registrationResponse.SessionId);

            // 步骤6：【关键修复】线程安全触发注册成功事件，仅预留双向流启动入口（工作日14实现）
            // 严守阶段边界：不实现任何双向流逻辑，仅触发事件供后续阶段订阅
            await OnNodeRegisteredAsync(nodeRegistration, registrationResponse, stoppingToken).ConfigureAwait(false);

            // 保持进程运行，监听停止信号，无任何业务逻辑侵入
            _logger.LogInformation("Worker节点正常运行，等待停止信号");
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Worker执行流程被取消，开始优雅关闭");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Worker执行流程发生致命异常");
            _appLifetime.StopApplication();
        }
    }

    /// <summary>
    /// 【关键修复】线程安全的事件触发方法，避免空引用竞态
    /// </summary>
    private async Task OnNodeRegisteredAsync(NodeRegistration registration, RegistrationResponse response, CancellationToken cancellationToken)
    {
        var handler = _nodeRegistered;
        if (handler != null)
        {
            _logger.LogDebug("触发节点注册成功事件");
            await handler.Invoke(registration, response, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 注册请求前置参数校验，严格对齐proto必填字段，符合原项目健壮性要求
    /// </summary>
    private bool ValidateNodeRegistration(NodeRegistration registration)
    {
        if (string.IsNullOrWhiteSpace(registration.NodeId))
        {
            _logger.LogError("节点注册参数校验失败：NodeId不能为空");
            return false;
        }
        if (registration.NodeId.Length > MaxNodeIdLength)
        {
            _logger.LogError("节点注册参数校验失败：NodeId长度不能超过128字符");
            return false;
        }
        if (string.IsNullOrWhiteSpace(registration.MachineName))
        {
            _logger.LogError("节点注册参数校验失败：MachineName不能为空");
            return false;
        }
        if (registration.CpuCores <= 0)
        {
            _logger.LogError("节点注册参数校验失败：CpuCores必须大于0");
            return false;
        }
        if (registration.TotalMemoryGb < 0)
        {
            _logger.LogError("节点注册参数校验失败：TotalMemoryGb不能为负数");
            return false;
        }
        if (string.IsNullOrWhiteSpace(registration.IpAddress))
        {
            _logger.LogWarning("节点IP地址为空，使用默认值127.0.0.1");
            registration.IpAddress = "127.0.0.1";
        }
        return true;
    }

    /// <summary>
    /// 带重试机制的节点注册流程，严格对齐原项目规范
    /// 【关键修复】每次重试前校验取消状态，精准异常分级，总超时控制
    /// </summary>
    private async Task<RegistrationResponse?> ExecuteRegistrationWithRetryAsync(
        NodeRegistration nodeRegistration,
        CancellationToken cancellationToken)
    {
        var maxRetryAttempts = _grpcSettings.MaxRetryAttempts;
        var retryDelayMs = _grpcSettings.RetryDelayMilliseconds;
        RegistrationResponse? response = null;
        // 整个重试流程总超时，避免无限重试
        using var totalTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(maxRetryAttempts * 10));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, totalTimeoutCts.Token);

        for (var attempt = 1; attempt <= maxRetryAttempts; attempt++)
        {
            // 【关键修复】每次重试前强制校验取消状态
            linkedCts.Token.ThrowIfCancellationRequested();

            try
            {
                _logger.LogInformation("开始节点注册，第{Attempt}次尝试，NodeId: {NodeId}", attempt, nodeRegistration.NodeId);

                // 创建注册请求头，携带认证Token，与Master端拦截器完全匹配
                var headers = new Metadata
                {
                    { "authorization", $"Bearer {_grpcSettings.NodeAuthToken}" },
                    { "node-id", nodeRegistration.NodeId }
                };

                // 单次请求deadline：10s，严格控制单次请求超时
                var deadline = DateTime.UtcNow.AddSeconds(10);
                response = await _grpcClientManager.CoordinatorClient.RegisterNodeAsync(
                    nodeRegistration,
                    headers,
                    deadline: deadline,
                    cancellationToken: linkedCts.Token).ConfigureAwait(false);

                if (response.Success)
                {
                    _logger.LogInformation("节点注册成功，第{Attempt}次尝试完成", attempt);
                    return response;
                }

                // 业务逻辑错误，不触发重试（如NodeId重复、参数非法）
                _logger.LogError("节点注册被拒绝，原因: {Message}", response.Message);
                return response;
            }
            // 不可重试异常：直接终止
            catch (RpcException ex) when (ex.StatusCode is StatusCode.Unauthenticated or StatusCode.AlreadyExists or StatusCode.InvalidArgument)
            {
                _logger.LogCritical(ex, "节点注册发生不可重试异常，终止重试");
                return null;
            }
            // 可重试异常：网络问题、服务不可用、超时，触发重试
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "节点注册第{Attempt}次尝试失败", attempt);

                // 最后一次尝试，不再重试
                if (attempt >= maxRetryAttempts)
                {
                    _logger.LogCritical(ex, "节点注册达到最大重试次数，注册失败");
                    return null;
                }

                // 等待重试间隔，支持取消
                await Task.Delay(retryDelayMs, linkedCts.Token).ConfigureAwait(false);
            }
        }

        return response;
    }

    /// <summary>
    /// 优雅关闭顺序：先释放资源，再调用基类StopAsync，确保资源完整释放
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Worker节点开始优雅关闭");

        // 1. 关闭gRPC客户端通道
        await _grpcClientManager.ShutdownAsync(cancellationToken).ConfigureAwait(false);

        // 2. 清除会话信息
        _sessionManager.ClearSession();

        // 3. 最后调用基类StopAsync，取消执行令牌
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Worker节点优雅关闭完成");
    }

    // 常量定义
    private const int MaxNodeIdLength = 128;
}