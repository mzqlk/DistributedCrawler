using CrawlerCore.Infrastructure;
using CrawlerWorker.Communication;
using CrawlerWorker.Services;
using CrawlerWorker.Services.Engine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Net.Http;

namespace CrawlerWorker.Hosting;

/// <summary>
/// Worker 主机的服务注册。Program.cs 与集成测试共用同一套注册，保证测试中运行的是真实 Worker。
/// </summary>
public static class WorkerServiceCollectionExtensions
{
    /// <summary>
    /// 注册 Worker 的配置绑定、gRPC 客户端、会话与节点信息采集、核心托管服务和爬虫引擎。
    /// </summary>
    public static IServiceCollection AddCrawlerWorker(this IServiceCollection services, IConfiguration configuration)
    {
        // 1. 强类型配置绑定
        services.Configure<GrpcSettings>(configuration.GetSection("GrpcSettings"));
        services.Configure<CrawlerSettings>(configuration.GetSection("CrawlerSettings"));
        // 进度上报节流间隔（TaskProgressThrottleSeconds）；未配置该节时保持默认值
        services.Configure<SignalRSettings>(configuration.GetSection(SignalRSettings.SectionName));

        // 2. gRPC核心服务注册
        services.AddSingleton<IGrpcClientManager, GrpcClientManager>();
        services.AddSingleton<GrpcClientAuthenticationInterceptor>();
        services.AddSingleton<GrpcAuthenticationInterceptor>();
        services.AddSingleton<IGrpcWorkerServiceFactory, GrpcWorkerServiceFactory>();

        // WorkerControlService：Scoped生命周期
        services.AddScoped<WorkerControlService>();

        // 3. gRPC服务全局配置，与Master端对齐
        services.AddGrpc(options =>
        {
            options.Interceptors.Add<GrpcAuthenticationInterceptor>();
            options.MaxReceiveMessageSize = configuration.GetValue<int>("GrpcSettings:MaxMessageSizeMb") * 1024 * 1024;
            options.MaxSendMessageSize = configuration.GetValue<int>("GrpcSettings:MaxMessageSizeMb") * 1024 * 1024;
            options.EnableDetailedErrors = true;
        });

        // 4. 核心业务服务注册
        services.AddSingleton<INodeInfoCollector, NodeInfoCollector>();
        services.AddSingleton<IWorkerSessionManager, WorkerSessionManager>();
        // Worker核心生命周期托管，由.NET主机统一管理
        services.AddHostedService<WorkerHostedService>();

        // 5. 爬虫引擎注册
        services.AddHttpClient("CrawlerEngineClient")
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
                MaxAutomaticRedirections = 10,
                ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
            })
            .ConfigureHttpClient(client =>
            {
                client.DefaultRequestHeaders.Add("User-Agent", "CrawlerWorker/1.0");
                client.Timeout = TimeSpan.FromSeconds(30);
            });
        services.AddSingleton<IPluginManager, PluginManager>();
        services.AddSingleton<ICrawlerEngine, CrawlerEngine>();

        return services;
    }
}
