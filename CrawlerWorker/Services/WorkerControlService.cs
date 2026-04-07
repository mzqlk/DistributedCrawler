using CrawlerWorker.Protos;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace CrawlerWorker.Services;

/// <summary>
/// Worker控制服务底座实现，严格遵循原项目要求：
/// 仅搭建框架、定义接口结构，不实现任何业务逻辑，不返回有效响应
/// 业务逻辑100%移交后续子阶段实现
/// </summary>
public class WorkerControlService : WorkerControl.WorkerControlBase
{
    private readonly ILogger<WorkerControlService> _logger;

    public WorkerControlService(ILogger<WorkerControlService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 预留接口，仅抛出NotImplementedException，不实现任何业务逻辑
    /// </summary>
    public override Task<WorkerStatusResponse> GetWorkerStatus(WorkerStatusRequest request, ServerCallContext context)
    {
        _logger.LogDebug("收到GetWorkerStatus预留接口请求，业务逻辑后续阶段实现");
        throw new NotImplementedException("WorkerControl服务底座预留接口，业务逻辑待后续阶段实现");
    }

    /// <summary>
    /// 预留接口，仅抛出NotImplementedException，不实现任何业务逻辑
    /// </summary>
    public override Task<TaskCommandResponse> ExecuteTaskCommand(TaskCommandRequest request, ServerCallContext context)
    {
        _logger.LogDebug("收到ExecuteTaskCommand预留接口请求，业务逻辑后续阶段实现");
        throw new NotImplementedException("WorkerControl服务底座预留接口，业务逻辑待后续阶段实现");
    }
}