using CrawlerCore.Infrastructure;
using CrawlerWorker.Protos;
using CrawlerWorker.Services;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;

namespace CrawlerWorker.Communication;

/// <summary>
/// Worker gRPC服务端底座工厂
/// </summary>
public interface IGrpcWorkerServiceFactory
{
    /// <summary>
    /// 创建Worker gRPC服务端实例
    /// </summary>
    Server CreateWorkerServer();
}

public class GrpcWorkerServiceFactory : IGrpcWorkerServiceFactory
{
    private readonly GrpcSettings _grpcSettings;
    private readonly ILogger<GrpcWorkerServiceFactory> _logger;
    private readonly GrpcAuthenticationInterceptor _authInterceptor;
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>
    /// 注入IServiceScopeFactory，解决Scoped服务生命周期问题
    /// </summary>
    public GrpcWorkerServiceFactory(
        IOptions<GrpcSettings> grpcSettings,
        ILogger<GrpcWorkerServiceFactory> logger,
        GrpcAuthenticationInterceptor authInterceptor,
        IServiceScopeFactory scopeFactory)
    {
        _grpcSettings = grpcSettings.Value;
        _logger = logger;
        _authInterceptor = authInterceptor;
        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// 创建Worker gRPC服务端实例，与Master端配置100%对齐
    /// </summary>
    public Server CreateWorkerServer()
    {
        _logger.LogInformation("创建Worker gRPC服务端底座框架，监听地址: {Endpoint}", _grpcSettings.WorkerEndpoint);

        var endpoint = new Uri(_grpcSettings.WorkerEndpoint);
        var listenPort = endpoint.Port;
        var listenAddress = endpoint.Host == "0.0.0.0" ? IPAddress.Any : IPAddress.Parse(endpoint.Host);

        // 创建Scoped作用域，获取Scoped生命周期的WorkerControlService
        var scope = _scopeFactory.CreateScope();
        var workerControlService = scope.ServiceProvider.GetRequiredService<WorkerControlService>();

        var maxMessageBytes = _grpcSettings.MaxMessageSizeMb * 1024 * 1024;
        var channelOptions = new[]
        {
            new ChannelOption("grpc.max_receive_message_length", maxMessageBytes),
            new ChannelOption("grpc.max_send_message_length", maxMessageBytes),
            new ChannelOption("grpc.keepalive_time_ms", (int)TimeSpan.FromSeconds(30).TotalMilliseconds),
            new ChannelOption("grpc.keepalive_timeout_ms", (int)TimeSpan.FromSeconds(5).TotalMilliseconds)
        };

        var server = new Server(channelOptions)
        {
            Services = {
                WorkerControl.BindService(workerControlService)
                    .Intercept(_authInterceptor)
            },
            Ports = {
                new ServerPort(
                    listenAddress.ToString(),
                    listenPort,
                    ServerCredentials.Insecure)
            }
        };

        // 服务端释放时同步释放Scoped作用域
        server.ShutdownTask.ContinueWith(_ => scope.Dispose(), TaskScheduler.Default);

        _logger.LogInformation("Worker gRPC服务端底座框架创建完成，未启动监听，预留后续阶段扩展入口");
        return server;
    }
}