using CrawlerCore.Common;
using CrawlerCore.Events;
using CrawlerCore.Infrastructure;
using CrawlerCore.Interfaces;
using CrawlerCore.Protos;
using CrawlerMaster.WPF.Data;
using CrawlerMaster.WPF.Infrastructure;
using CrawlerMaster.WPF.Services;
using CrawlerMaster.WPF.Services.Hubs;
using CrawlerMaster.WPF.ViewModels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using System;
using System.Threading.Tasks;
using System.Windows;

namespace CrawlerMaster.WPF;

public partial class App : Application
{
    public static IHost AppHost { get; private set; } = null!;
    private NodeHealthCheckService? _healthCheckService;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            // 1. 全局日志最先初始化
            LogConfiguration.Configure("Master", Serilog.Events.LogEventLevel.Information);
            LogHelper.Info($"Master 控制端启动，版本: {GetType().Assembly.GetName().Version}");

            // 2. 全局异常拦截
            ConfigureGlobalExceptionHandling();

            // 3. 统一通用主机，同时托管gRPC+SignalR，共享IOC容器，无端口冲突
            AppHost = Host.CreateDefaultBuilder(e.Args)
                .UseSerilog(Log.Logger)
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseKestrel((context, options) =>
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
                    })
                    .ConfigureServices((context, services) =>
                    {
                        // 注册gRPC服务，ASP.NET Core托管
                        services.AddGrpc(opt =>
                        {
                            opt.EnableDetailedErrors = true;
                            opt.MaxReceiveMessageSize = context.Configuration
                                .GetValue<int>($"{GrpcSettings.SectionName}:{nameof(GrpcSettings.MaxMessageSizeMb)}", 64) * 1024 * 1024;
                            opt.MaxSendMessageSize = context.Configuration
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
                    })
                    .Configure((context, app) =>
                    {
                        var signalRSettings = context.Configuration
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
                    });
                })
                .ConfigureServices((context, services) =>
                {
                    // ==================== 配置绑定 ====================
                    services.Configure<GrpcSettings>(context.Configuration.GetSection(GrpcSettings.SectionName));
                    services.Configure<CrawlerSettings>(context.Configuration.GetSection(CrawlerSettings.SectionName));
                    services.Configure<SignalRSettings>(context.Configuration.GetSection(SignalRSettings.SectionName));
                    services.Configure<DatabaseSettings>(context.Configuration.GetSection(DatabaseSettings.SectionName));

                    // ==================== 核心基础设施 ====================
                    services.AddSingleton<IEventAggregator, EventAggregator>();

                    // 数据库上下文（Scoped生命周期，对齐EF Core规范）
                    var dbSettings = context.Configuration
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

                    // ==================== WPF UI服务 ====================
                    services.AddSingleton<MainWindowViewModel>();
                    services.AddTransient<MainWindow>(sp => new MainWindow(sp.GetRequiredService<MainWindowViewModel>()));
                })
                .Build();

            // 4. 启动主机（端口占用异常友好捕获）
            try
            {
                await AppHost.StartAsync();
                LogHelper.Info("通用主机启动完成，gRPC+SignalR服务已正常启动");
            }
            catch (System.Net.Sockets.SocketException ex) when (ex.SocketErrorCode == System.Net.Sockets.SocketError.AddressAlreadyInUse)
            {
                throw new InvalidOperationException($"端口被占用，请检查gRPC(5001)/SignalR(5003)端口是否被其他程序占用，或修改appsettings.json配置", ex);
            }

            // 5. 数据库初始化
            using var scope = AppHost.Services.CreateScope();
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

            // 6. 业务初始化
            await scope.ServiceProvider.GetRequiredService<ITaskSchedulerService>().RecoverPendingTasksFromDatabaseAsync();

            _healthCheckService = AppHost.Services.GetRequiredService<NodeHealthCheckService>();
            _healthCheckService.Start();
            _ = AppHost.Services.GetRequiredService<FailedTaskRecoveryService>();

            // 7. 启动WPF主窗口
            AppHost.Services.GetRequiredService<MainWindow>().Show();
        }
        catch (Exception ex)
        {
            LogHelper.Fatal(ex, "程序启动崩溃");
            MessageBox.Show($"启动失败: {ex.Message}", "致命错误", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    #region 辅助方法（修复SQL注入+全异常隔离）
    private void ConfigureGlobalExceptionHandling()
    {
        AppDomain.CurrentDomain.UnhandledException += (s, a) =>
        {
            var ex = a.ExceptionObject as Exception;
            LogHelper.Fatal(ex, $"非UI线程未捕获异常，终止程序: {a.IsTerminating}");
            Dispatcher.Invoke(() => MessageBox.Show($"系统崩溃，请查看日志: {ex?.Message}", "致命错误"));
        };

        DispatcherUnhandledException += (s, a) =>
        {
            LogHelper.Error(a.Exception, "UI线程未捕获异常");
            MessageBox.Show($"操作失败: {a.Exception.Message}", "错误");
            a.Handled = true;
        };
    }

    /// <summary>
    /// 参数化查询
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

    /// <summary>
    /// 等待异步资源释放完成再退出，避免进程残留
    /// </summary>
    protected override async void OnExit(ExitEventArgs e)
    {
        LogHelper.Info("Master 控制端开始正常退出流程");

        // 停止健康检查服务
        _healthCheckService?.Stop();
        _healthCheckService?.Dispose();

        // 释放ViewModel资源
        if (AppHost != null)
        {
            try
            {
                var viewModel = AppHost.Services.GetService<MainWindowViewModel>();
                if (viewModel != null)
                {
                    await viewModel.DisposeAsync();
                }
                await AppHost.StopAsync(TimeSpan.FromSeconds(5));
                AppHost.Dispose();
            }
            catch (Exception ex)
            {
                LogHelper.Warn("退出时资源释放异常");
            }
        }

        Log.CloseAndFlush();
        base.OnExit(e);
    }
    #endregion
}