using CrawlerCore.Events;
using CrawlerCore.Infrastructure;
using CrawlerCore.Interfaces;
using CrawlerCore.Protos;
using CrawlerMaster.WPF.Data;
using CrawlerMaster.WPF.Infrastructure;
using CrawlerMaster.WPF.Services;
using CrawlerMaster.WPF.Services.Hubs;
using Grpc.AspNetCore.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace CrawlerMaster.WPF.Hosting;

/// <summary>
/// 数据库初始化的结果
/// </summary>
/// <param name="Rebuilt">是否新建或重建了数据库</param>
/// <param name="BackupPath">重建前备份的文件路径；没有备份（库中没有数据或无需重建）时为 null</param>
/// <param name="Reason">重建原因（缺表、缺列、无法读取）；无需重建时为 null</param>
public sealed record DatabaseInitializationResult(bool Rebuilt, string? BackupPath, string? Reason);

/// <summary>
/// Master 主机的服务注册与启动步骤。
/// WPF 启动（App.xaml.cs）与集成测试共用同一套注册，保证两边运行的后台服务一致；
/// 界面相关的服务（ViewModel、窗口）不在这里注册，由 App.xaml.cs 自行追加。
/// </summary>
public static class MasterHostingExtensions
{
    /// <summary>SignalR 跨域策略名</summary>
    public const string SignalRCorsPolicy = "SignalRCors";

    /// <summary>
    /// 配置 Kestrel 监听：gRPC 端口取自 <c>GrpcSettings:MasterEndpoint</c>，监听所有网卡（Worker 可能在其他机器或容器中），强制 HTTP/2；
    /// SignalR 端口取自 <c>SignalRSettings:Port</c>，监听地址取自 <c>SignalRSettings:ListenAddress</c>（默认只监听本机），兼容 HTTP/1 与 HTTP/2。
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

            ConfigureMasterHttp2KeepAlive(options, grpcSettings);

            // gRPC端口：HTTP协议下强制启用HTTP/2，无需TLS
            var grpcUri = new Uri(grpcSettings.MasterEndpoint);
            options.ListenAnyIP(grpcUri.Port, listenOptions => listenOptions.Protocols = HttpProtocols.Http2);

            // SignalR端口：兼容HTTP1/2
            var signalRAddress = ResolveListenAddress(signalRSettings.ListenAddress);
            if (signalRAddress == null)
            {
                options.ListenAnyIP(signalRSettings.Port, listenOptions => listenOptions.Protocols = HttpProtocols.Http1AndHttp2);
            }
            else
            {
                options.Listen(signalRAddress, signalRSettings.Port, listenOptions => listenOptions.Protocols = HttpProtocols.Http1AndHttp2);
            }
        });
    }

    /// <summary>
    /// 开启 HTTP/2 keepalive：连接上一段时间没有收到任何帧就发送 PING，超时未回应即关闭连接。
    /// gRPC 流式调用不受最小数据速率限制，不开启时 Master 发现不了半开连接（Worker 断网、休眠）。
    /// </summary>
    public static void ConfigureMasterHttp2KeepAlive(KestrelServerOptions options, GrpcSettings grpcSettings)
    {
        var defaults = new GrpcSettings();
        var delay = grpcSettings.KeepAlivePingDelaySeconds > 0 ? grpcSettings.KeepAlivePingDelaySeconds : defaults.KeepAlivePingDelaySeconds;
        var timeout = grpcSettings.KeepAlivePingTimeoutSeconds > 0 ? grpcSettings.KeepAlivePingTimeoutSeconds : defaults.KeepAlivePingTimeoutSeconds;
        options.Limits.Http2.KeepAlivePingDelay = TimeSpan.FromSeconds(delay);
        options.Limits.Http2.KeepAlivePingTimeout = TimeSpan.FromSeconds(timeout);
    }

    /// <summary>
    /// 解析监听地址：空、"*"、"0.0.0.0" 表示所有网卡（返回 null）；无法解析时退回本机环回地址
    /// </summary>
    public static IPAddress? ResolveListenAddress(string? listenAddress)
    {
        if (string.IsNullOrWhiteSpace(listenAddress) || listenAddress.Trim() is "*" or "0.0.0.0")
        {
            return null;
        }
        if (listenAddress.Trim().Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return IPAddress.Loopback;
        }
        return IPAddress.TryParse(listenAddress.Trim(), out var address) ? address : IPAddress.Loopback;
    }

    /// <summary>
    /// 注册 gRPC（ASP.NET Core 托管，启用节点令牌认证拦截器）、SignalR 与 CORS 策略。
    /// 详细错误信息只在 Development 环境返回给客户端。
    /// </summary>
    public static IServiceCollection AddMasterWebServices(this IServiceCollection services, IConfiguration configuration)
    {
        var maxMessageSize = configuration
            .GetValue<int>($"{GrpcSettings.SectionName}:{nameof(GrpcSettings.MaxMessageSizeMb)}", 64) * 1024 * 1024;

        services.AddGrpc(opt =>
        {
            // 节点令牌校验：RegisterNode 与 StartStreaming 都经过它
            opt.Interceptors.Add<GrpcAuthenticationInterceptor>();
            opt.MaxReceiveMessageSize = maxMessageSize;
            opt.MaxSendMessageSize = maxMessageSize;
        });
        services.AddOptions<GrpcServiceOptions>()
            .Configure<IHostEnvironment>((opt, env) => opt.EnableDetailedErrors = env.IsDevelopment());

        services.AddSignalR();
        services.AddOptions<HubOptions>()
            .Configure<IHostEnvironment>((opt, env) => opt.EnableDetailedErrors = env.IsDevelopment());

        // CORS：只允许配置中的来源（默认没有任何来源被允许）
        var allowedOrigins = configuration
            .GetSection($"{SignalRSettings.SectionName}:{nameof(SignalRSettings.AllowedOrigins)}")
            .Get<string[]>() ?? Array.Empty<string>();
        services.AddCors(opt =>
        {
            opt.AddPolicy(SignalRCorsPolicy, policy =>
            {
                policy.WithOrigins(allowedOrigins)
                      .AllowAnyHeader()
                      .AllowAnyMethod();
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

        app.UseCors(SignalRCorsPolicy);
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
        services.Configure<LoadBalancingSettings>(configuration.GetSection(LoadBalancingSettings.SectionName));

        // ==================== 核心基础设施 ====================
        services.AddSingleton<IEventAggregator, EventAggregator>();

        // 数据库上下文（Scoped生命周期，对齐EF Core规范）
        // 相对路径的数据库文件放在程序所在目录，不随启动时的工作目录变化
        var dbSettings = configuration
            .GetSection(DatabaseSettings.SectionName)
            .Get<DatabaseSettings>() ?? new DatabaseSettings();
        var connectionString = ResolveSqliteConnectionString(dbSettings.ConnectionString, AppContext.BaseDirectory);
        services.AddDbContext<CrawlerDbContext>(opt =>
            opt.UseSqlite(connectionString), ServiceLifetime.Scoped);

        // ==================== 业务服务注册 ====================
        services.AddScoped<ITaskDataService, TaskDataService>();
        services.AddScoped<INodeManagementService, NodeManagementService>();
        services.AddSingleton<NodeHealthCheckService>();
        services.AddSingleton<FailedTaskRecoveryService>();
        services.AddSingleton<ILoadBalancingScheduler, LoadBalancingScheduler>();

        // 会话管理同时是托管服务：定期清理过期且没有活跃流的会话
        services.AddSingleton<SessionManager>();
        services.AddHostedService(sp => sp.GetRequiredService<SessionManager>());
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
    /// 把 SQLite 连接字符串中相对路径的 Data Source 解析到 <paramref name="baseDirectory"/> 下。
    /// 绝对路径、内存库、URI 形式保持不变。
    /// </summary>
    public static string ResolveSqliteConnectionString(string connectionString, string baseDirectory)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        var dataSource = builder.DataSource;
        if (string.IsNullOrWhiteSpace(dataSource)
            || builder.Mode == SqliteOpenMode.Memory
            || dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase)
            || dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            || Path.IsPathRooted(dataSource))
        {
            return connectionString;
        }

        builder.DataSource = Path.GetFullPath(Path.Combine(baseDirectory, dataSource));
        return builder.ToString();
    }

    /// <summary>
    /// 数据库初始化：表和列与当前实体模型一致时直接使用；结构不符（缺表、缺列）或无法读取时重建（EnsureCreated，不使用迁移）。
    /// 重建前，库里有数据就先备份为 <c>crawler.db.bak-yyyyMMddTHHmmssZ</c>（UTC），库里没有数据则直接重建。
    /// 只检查表与列，不检查索引：旧库缺少新增的索引（例如 CrawlResults.TaskId 唯一索引）时照常使用。
    /// </summary>
    public static async Task<DatabaseInitializationResult> InitializeMasterDatabaseAsync(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CrawlerDbContext>();
        var dataSource = new SqliteConnectionStringBuilder(db.Database.GetConnectionString()).DataSource;
        var fileExisted = File.Exists(dataSource);

        List<string> problems;
        var readable = true;
        try
        {
            problems = await FindSchemaProblemsAsync(db);
        }
        catch (SqliteException ex)
        {
            readable = false;
            problems = new List<string> { $"无法读取数据库: {ex.Message}" };
        }

        if (problems.Count == 0)
        {
            LogHelper.Info($"数据库结构校验通过: {dataSource}");
            return new DatabaseInitializationResult(false, null, null);
        }

        var reason = string.Join("；", problems);
        string? backupPath = null;
        if (fileExisted && (!readable || await HasAnyRowsAsync(db)))
        {
            backupPath = await BackupDatabaseAsync(db, dataSource, readable);
            LogHelper.Warn($"数据库结构与程序不一致（{reason}），已备份到 {backupPath}，开始重建");
        }
        else if (fileExisted)
        {
            LogHelper.Warn($"数据库结构与程序不一致（{reason}），库中没有数据，直接重建");
        }
        else
        {
            LogHelper.Info($"数据库不存在，开始创建: {dataSource}");
        }

        SqliteConnection.ClearAllPools();
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var remaining = await FindSchemaProblemsAsync(db);
        if (remaining.Count > 0)
        {
            throw new InvalidOperationException($"数据库重建后结构仍不完整：{string.Join("；", remaining)}");
        }

        LogHelper.Info("数据库创建完成，数据表验证通过");
        return new DatabaseInitializationResult(true, backupPath, fileExisted ? reason : "数据库不存在");
    }

    /// <summary>
    /// 对照 EF 模型检查每张表与每一列是否存在（PRAGMA table_info）。使用 DbContext 自己的连接，不释放它
    /// </summary>
    private static async Task<List<string>> FindSchemaProblemsAsync(CrawlerDbContext db)
    {
        var problems = new List<string>();
        await db.Database.OpenConnectionAsync();
        try
        {
            var connection = db.Database.GetDbConnection();
            foreach (var entityType in db.Model.GetEntityTypes())
            {
                var table = entityType.GetTableName();
                if (string.IsNullOrEmpty(table)) continue;

                var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                await using (var command = connection.CreateCommand())
                {
                    command.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"")}\")";
                    await using var reader = await command.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        existingColumns.Add(reader.GetString(1));
                    }
                }

                if (existingColumns.Count == 0)
                {
                    problems.Add($"缺少表 {table}");
                    continue;
                }

                var storeObject = StoreObjectIdentifier.Table(table, entityType.GetSchema());
                var missingColumns = entityType.GetProperties()
                    .Select(p => p.GetColumnName(storeObject))
                    .Where(name => !string.IsNullOrEmpty(name) && !existingColumns.Contains(name!))
                    .ToList();
                if (missingColumns.Count > 0)
                {
                    problems.Add($"表 {table} 缺少列 {string.Join(", ", missingColumns)}");
                }
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
        return problems;
    }

    /// <summary>
    /// 库中任意一张用户表是否有数据
    /// </summary>
    private static async Task<bool> HasAnyRowsAsync(CrawlerDbContext db)
    {
        await db.Database.OpenConnectionAsync();
        try
        {
            var connection = db.Database.GetDbConnection();
            var tables = new List<string>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    tables.Add(reader.GetString(0));
                }
            }

            foreach (var table in tables)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"SELECT EXISTS(SELECT 1 FROM \"{table.Replace("\"", "\"\"")}\")";
                if (Convert.ToInt64(await command.ExecuteScalarAsync()) != 0)
                {
                    return true;
                }
            }
            return false;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// 备份数据库：能读取时用 SQLite 在线备份接口（得到一致的副本），读不了时直接复制原文件
    /// </summary>
    private static async Task<string> BackupDatabaseAsync(CrawlerDbContext db, string dataSource, bool readable)
    {
        var backupPath = $"{dataSource}.bak-{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}";
        for (var i = 1; File.Exists(backupPath); i++)
        {
            backupPath = $"{dataSource}.bak-{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}-{i}";
        }

        if (readable)
        {
            await db.Database.OpenConnectionAsync();
            try
            {
                var source = (SqliteConnection)db.Database.GetDbConnection();
                using var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupPath, Pooling = false }.ToString());
                destination.Open();
                source.BackupDatabase(destination);
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }
        }
        else
        {
            SqliteConnection.ClearAllPools();
            File.Copy(dataSource, backupPath);
        }
        return backupPath;
    }

    /// <summary>
    /// 启动业务后台组件：从数据库恢复任务、启动节点健康检查、激活故障任务恢复服务（构造时订阅节点离线事件），
    /// 并提前创建认证拦截器（令牌不可用时启动即记录错误日志）。
    /// 必须在主机启动、数据库初始化之后调用。
    /// </summary>
    /// <returns>已启动的健康检查服务，调用方在退出时负责停止</returns>
    public static async Task<NodeHealthCheckService> StartMasterBackgroundServicesAsync(this IServiceProvider services)
    {
        _ = services.GetRequiredService<GrpcAuthenticationInterceptor>();

        // 先激活故障恢复，再恢复任务：恢复期间节点离线事件也不会漏掉
        _ = services.GetRequiredService<FailedTaskRecoveryService>();
        await services.GetRequiredService<ITaskSchedulerService>().RecoverPendingTasksFromDatabaseAsync();

        var healthCheckService = services.GetRequiredService<NodeHealthCheckService>();
        healthCheckService.Start();

        return healthCheckService;
    }
}
