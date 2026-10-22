using CrawlerCore.Infrastructure;
using CrawlerWorker.Communication;
using CrawlerWorker.Services;
using CrawlerWorker.Services.Engine;
using CrawlerWorker.Services.Engine.Robots;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using System;
using System.Net;
using System.Net.Http;
using System.Threading;

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
        services.TryAddSingleton(TimeProvider.System);

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
        // 进程级发件箱：跨越断线重连，结果保留到收到回执（W-03）
        services.AddSingleton<WorkerOutbox>();
        // Worker核心生命周期托管，由.NET主机统一管理
        services.AddHostedService<WorkerHostedService>();

        // 5. 爬虫引擎注册：User-Agent 统一取 CrawlerSettings:DefaultUserAgent（robots.txt 按其中的产品名匹配，W-30）；
        //    超时由引擎按请求控制（覆盖读取正文，W-21），这里不设 HttpClient.Timeout
        services.AddHttpClient(CrawlerEngine.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(sp => CreateCrawlerHttpHandler(sp.GetRequiredService<IOptions<CrawlerSettings>>().Value))
            .ConfigureHttpClient((sp, client) =>
            {
                var settings = sp.GetRequiredService<IOptions<CrawlerSettings>>().Value;
                client.Timeout = Timeout.InfiniteTimeSpan;
                if (!client.DefaultRequestHeaders.UserAgent.TryParseAdd(settings.DefaultUserAgent))
                {
                    LogHelper.Warn($"CrawlerSettings:DefaultUserAgent 格式无效，改用默认值: {settings.DefaultUserAgent}");
                    client.DefaultRequestHeaders.UserAgent.ParseAdd(new CrawlerSettings().DefaultUserAgent);
                }
            });
        services.AddSingleton<IHostRateLimiter, HostRateLimiter>();
        services.AddSingleton<IRobotsPolicy, RobotsPolicy>();
        services.AddSingleton<IPluginManager, PluginManager>();
        services.AddSingleton<ICrawlerEngine, CrawlerEngine>();

        return services;
    }

    /// <summary>
    /// 爬虫请求使用的处理器：手动跟随重定向（引擎逐跳检查 robots 与作用域）、自动解压；
    /// 默认校验证书，只有 <see cref="CrawlerSettings.IgnoreCertificateErrors"/> 打开时才忽略证书错误（W-15）
    /// </summary>
    public static HttpMessageHandler CreateCrawlerHttpHandler(CrawlerSettings settings)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(Math.Max(1, settings.RequestTimeoutSeconds))
        };
        if (settings.IgnoreCertificateErrors)
        {
            LogHelper.Warn("CrawlerSettings:IgnoreCertificateErrors 已打开：不校验被爬网站的证书，页面内容可能被中间人篡改");
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }
        return handler;
    }
}
