using CrawlerCore.Infrastructure;
using CrawlerWorker.Communication;
using CrawlerWorker.Services;
using CrawlerWorker.Services.Engine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http3UnencryptedSupport", true);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
    .CreateBootstrapLogger();

try
{
    Log.Information("Worker节点应用启动，开始加载配置");

    var configuration = new ConfigurationBuilder()
        .SetBasePath(Directory.GetCurrentDirectory())
        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
        // 环境变量映射，优先级最高
        .AddInMemoryCollection(GetOfficialEnvironmentVariableMappings())
        // 通用环境变量支持，双下划线配置映射
        .AddEnvironmentVariables()
        .Build();

    // 重载Serilog，使用正式配置
    Log.Logger = new LoggerConfiguration()
        .ReadFrom.Configuration(configuration)
        .CreateLogger();

    Log.Information("配置加载完成，开始构建主机");

    // 构建.NET通用主机
    var host = Host.CreateDefaultBuilder(args)
        .ConfigureHostConfiguration(config =>
        {
            config.AddConfiguration(configuration);
        })
        .UseSerilog(Log.Logger)
        // 依赖注入配置
        .ConfigureServices((context, services) =>
        {
            // 1. 强类型配置绑定
            services.Configure<GrpcSettings>(context.Configuration.GetSection("GrpcSettings"));
            services.Configure<CrawlerSettings>(context.Configuration.GetSection("CrawlerSettings"));

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
                options.MaxReceiveMessageSize = context.Configuration.GetValue<int>("GrpcSettings:MaxMessageSizeMb") * 1024 * 1024;
                options.MaxSendMessageSize = context.Configuration.GetValue<int>("GrpcSettings:MaxMessageSizeMb") * 1024 * 1024;
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
            services.AddSingleton<CrawlerEngine>();
        })
        .Build();

    Log.Information("主机构建完成，开始启动");
    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Worker节点应用启动失败，致命异常");
}
finally
{
    Log.CloseAndFlush();
}

Dictionary<string, string> GetOfficialEnvironmentVariableMappings()
{
    var mappings = new Dictionary<string, string>();

    var masterEndpoint = Environment.GetEnvironmentVariable("MASTER_ENDPOINT");
    if (!string.IsNullOrWhiteSpace(masterEndpoint))
    {
        mappings["GrpcSettings:MasterEndpoint"] = masterEndpoint;
        Log.Information("使用官方环境变量MASTER_ENDPOINT: {Endpoint}", masterEndpoint);
    }

    var masterNodeId = Environment.GetEnvironmentVariable("MASTER_NODE_ID");
    if (!string.IsNullOrWhiteSpace(masterNodeId))
    {
        Log.Information("使用官方环境变量MASTER_NODE_ID: {NodeId}", masterNodeId);
    }

    var nodeAuthToken = Environment.GetEnvironmentVariable("NODE_AUTH_TOKEN");
    if (!string.IsNullOrWhiteSpace(nodeAuthToken))
    {
        mappings["GrpcSettings:NodeAuthToken"] = nodeAuthToken;
        Log.Information("使用官方环境变量NODE_AUTH_TOKEN: {Token}", nodeAuthToken);
    }

    var workerEndpoint = Environment.GetEnvironmentVariable("WORKER_ENDPOINT");
    if (!string.IsNullOrWhiteSpace(workerEndpoint))
    {
        mappings["GrpcSettings:WorkerEndpoint"] = workerEndpoint;
        Log.Information("使用官方环境变量WORKER_ENDPOINT: {Endpoint}", workerEndpoint);
    }

    return mappings;
}