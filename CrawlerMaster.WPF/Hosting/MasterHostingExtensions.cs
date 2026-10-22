using CrawlerCore.Events;
using CrawlerCore.Infrastructure;
using CrawlerCore.Interfaces;
using CrawlerCore.Protos;
using CrawlerMaster.WPF.Data;
using CrawlerMaster.WPF.Infrastructure;
using CrawlerMaster.WPF.Services;
using CrawlerMaster.WPF.Services.Hubs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading.Tasks;

namespace CrawlerMaster.WPF.Hosting;

/// <summary>
/// Master 主机的服务注册与启动步骤。
/// WPF 启动（App.xaml.cs）与集成测试共用同一套注册，保证两边运行的后台服务一致；
/// 界面相关的服务（ViewModel、窗口）不在这里注册，由 App.xaml.cs 自行追加。
/// </summary>
public static class MasterHostingExtensions
{
    /// <summary>
    /// 配置 Kestrel 监听：gRPC 端口取自 <c>GrpcSettings:MasterEndpoint</c>，强制 HTTP/2；
    /// SignalR 端口取自 <c>SignalRSettings:Port</c>，兼容 HTTP/1 与 HTTP/2。均监听所有网卡。
    /// </summary>
    public static IWebHostBuilder UseMasterKestrel(this IWebHostBuilder webBuilder)
    {
        return webBuilder.UseKestrel((context, options) =>
        {
            // 读取强类型配置
            var grpcSettings = context.Configuration
                .GetSection(GrpcSettings.SectionName)
                .Get<GrpcSettings>() ?? new GrpcSettings();
            var signalRSettings = context.Configuration
                .GetSection(SignalRSettings.SectionName)
                .Get<SignalRSettings>() ?? new SignalRSettings();

            // gRPC端口：HTTP协议下强制启用HTTP/2
            var grpcUri = new Uri(grpcSettings.MasterEndpoint);
            options.ListenAnyIP(grpcUri.Port, listenOptions =>
            {
                // HTTP协议下强制HTTP/2，无需TLS
                listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2;
            });

            // SignalR端口：兼容HTTP1/2
            options.ListenAnyIP(signalRSettings.Port, listenOptions =>
            {
                listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1AndHttp2;
            });
        });
    }

    /// <summary>
    /// 注册 gRPC（ASP.NET Core 托管）、SignalR 与 CORS 策略。
    /// </summary>
    public static IServiceCollection AddMasterWebServices(this IServiceCollection services, IConfiguration configuration)
    {
        // 注册gRPC服务，ASP.NET Core托管
        services.AddGrpc(opt =>
        {
            opt.EnableDetailedErrors = true;
            opt.MaxReceiveMessageSize = configuration
                .GetValue<int>($"{GrpcSettings.SectionName}:{nameof(GrpcSettings.MaxMessageSizeMb)}", 64) * 1024 * 1024;
            opt.MaxSendMessageSize = configuration
                .GetValue<int>($"{GrpcSettings.SectionName}:{nameof(GrpcSettings.MaxMessageSizeMb)}", 64) * 1024 * 1024;
        });

        // SignalR服务注册
        services.AddSignalR(opt => opt.EnableDetailedErrors = true);

        // CORS策略（兼容跨域场景）
        services.AddCors(opt =>
        {
            opt.AddPolicy("AllowAll", policy =>
            {
                policy.SetIsOriginAllowed(_ => true)
                      .AllowAnyHeader()
                      .AllowAnyMethod()
                      .AllowCredentials();
            });
        });

        return services;
    }

    /// <summary>
    /// 配置中间件与终结点：gRPC 协调服务、SignalR Hub。
    /// </summary>
    public static IApplicationBuilder UseMasterEndpoints(this IApplicationBuilder app, IConfiguration configuration)
    {
        var signalRSettings = configuration
            .GetSection(SignalRSettings.SectionName)
            .Get<SignalRSettings>() ?? new SignalRSettings();

        app.UseCors("AllowAll");
        app.UseRouting();
        app.UseEndpoints(end =>
        {
            // 映射gRPC服务端点，Worker可正常连接
            end.MapGrpcService<CrawlerCoordinatorService>();
            // 映射SignalR Hub端点
            end.MapHub<CrawlerHub>(signalRSettings.HubPath);
        });

        return app;
    }

    /// <summary>
    /// 注册配置绑定、数据库上下文与全部后台业务服务（不含 WPF 界面服务）。
    /// </summary>
    public static IServiceCollection AddMasterCoreServices(this IServiceCollection services, IConfiguration configuration)
    {
        // ==================== 配置绑定 ====================
        services.Configure<GrpcSettings>(configuration.GetSection(GrpcSettings.SectionName));
        services.Configure<CrawlerSettings>(configuration.GetSection(CrawlerSettings.SectionName));
        services.Configure<SignalRSettings>(configuration.GetSection(SignalRSettings.SectionName));
        services.Configure<DatabaseSettings>(configuration.GetSection(DatabaseSettings.SectionName));
        services.Configure<HealthCheckSettings>(configuration.GetSection(HealthCheckSettings.SectionName));

        // ==================== 核心基础设施 ====================
        services.AddSingleton<IEventAggregator, EventAggregator>();

        // 数据库上下文（Scoped生命周期，对齐EF Core规范）
        var dbSettings = configuration
            .GetSection(DatabaseSettings.SectionName)
            .Get<DatabaseSettings>() ?? new DatabaseSettings();
        services.AddDbContext<CrawlerDbContext>(opt =>
            opt.UseSqlite(dbSettings.ConnectionString), ServiceLifetime.Scoped);

        // ==================== 业务服务注册 ====================
        services.AddScoped<ITaskDataService, TaskDataService>();
        services.AddScoped<INodeManagementService, NodeManagementService>();
        services.AddSingleton<NodeHealthCheckService>();
        services.AddSingleton<FailedTaskRecoveryService>();
        services.AddSingleton<ILoadBalancingScheduler, LoadBalancingScheduler>();

        services.AddSingleton<SessionManager>();
        services.AddSingleton<GrpcAuthenticationInterceptor>();

        services.AddSingleton<CrawlerCoordinatorService>();
        services.AddSingleton<ICrawlerCoordinatorService>(sp => sp.GetRequiredService<CrawlerCoordinatorService>());
        services.AddSingleton<CrawlerCoordinator.CrawlerCoordinatorBase>(sp => sp.GetRequiredService<CrawlerCoordinatorService>());

        // 任务调度服务
        services.AddSingleton<TaskSchedulerHostedService>();
        services.AddHostedService(sp => sp.GetRequiredService<TaskSchedulerHostedService>());
        services.AddSingleton<ITaskSchedulerService>(sp => sp.GetRequiredService<TaskSchedulerHostedService>());

        // 实时同步推送服务
        services.AddSingleton<RealtimeDataSyncService>();
        services.AddHostedService(sp => sp.GetRequiredService<RealtimeDataSyncService>());

        return services;
    }

    /// <summary>
    /// 数据库初始化：能连接且表齐全则直接使用；缺表时删除并重建（EnsureCreated，不使用迁移）。
    /// </summary>
    public static async Task InitializeMasterDatabaseAsync(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CrawlerDbContext>();
        LogHelper.Info($"数据库连接初始化完成");

        bool rebuildDb = false;
        if (await db.Database.CanConnectAsync())
        {
            try { _ = await db.NodeInfos.CountAsync(); }
            catch (SqliteException ex) when (ex.Message.Contains("no such table"))
            {
                LogHelper.Warn($"数据表缺失，触发数据库重建: {ex.Message}");
                rebuildDb = true;
            }
        }
        else rebuildDb = true;

        if (rebuildDb)
        {
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            await VerifyTableExistsAsync(db, "NodeInfos");
            await VerifyTableExistsAsync(db, "CrawlerTasks");
            await VerifyTableExistsAsync(db, "CrawlResults");
        }
    }

    /// <summary>
    /// 启动业务后台组件：从数据库恢复 Pending 任务、启动节点健康检查、
    /// 激活故障任务恢复服务（构造时订阅节点离线事件）。
    /// 必须在主机启动、数据库初始化之后调用。
    /// </summary>
    /// <returns>已启动的健康检查服务，调用方在退出时负责停止</returns>
    public static async Task<NodeHealthCheckService> StartMasterBackgroundServicesAsync(this IServiceProvider services)
    {
        await services.GetRequiredService<ITaskSchedulerService>().RecoverPendingTasksFromDatabaseAsync();

        var healthCheckService = services.GetRequiredService<NodeHealthCheckService>();
        healthCheckService.Start();
        _ = services.GetRequiredService<FailedTaskRecoveryService>();

        return healthCheckService;
    }

    /// <summary>
    /// 参数化查询校验数据表是否存在
    /// </summary>
    private static async Task VerifyTableExistsAsync(CrawlerDbContext db, string tableName)
    {
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=@TableName";

        var param = cmd.CreateParameter();
        param.ParameterName = "@TableName";
        param.Value = tableName;
        cmd.Parameters.Add(param);

        var result = await cmd.ExecuteScalarAsync();
        if (result == null)
            throw new InvalidOperationException($"数据表 {tableName} 创建失败");
        LogHelper.Info($"数据表 {tableName} 验证通过");
    }
}
