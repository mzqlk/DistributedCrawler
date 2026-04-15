using CrawlerCore.Common;
using CrawlerCore.Entities;
using CrawlerCore.Events;
using CrawlerCore.Infrastructure;
using CrawlerCore.Interfaces;
using CrawlerCore.Protos;
using CrawlerMaster.WPF.Data;
using CrawlerMaster.WPF.Infrastructure;
using CrawlerMaster.WPF.Services;
using CrawlerMaster.WPF.Services.Hubs;
using CrawlerMaster.WPF.ViewModels;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;
using System;
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
            // 1. 日志初始化（全局唯一）
            LogConfiguration.Configure("Master", Serilog.Events.LogEventLevel.Information);
            LogHelper.Info($"Master 控制端启动，版本: {GetType().Assembly.GetName().Version}");

            // 2. 全局异常拦截
            ConfigureGlobalExceptionHandling();

            // 3. 构建通用主机（WPF + 业务服务 + SignalR Web主机）
            AppHost = Host.CreateDefaultBuilder(e.Args)
                .UseSerilog(Log.Logger)
                .ConfigureServices((context, services) =>
                {
                    // ==================== 配置绑定 ====================
                    services.Configure<GrpcSettings>(context.Configuration.GetSection(GrpcSettings.SectionName));
                    services.Configure<CrawlerSettings>(context.Configuration.GetSection(CrawlerSettings.SectionName));
                    services.Configure<SignalRSettings>(context.Configuration.GetSection(SignalRSettings.SectionName));

                    // ==================== SignalR 核心注册（主容器备用） ====================
                    services.AddSignalR(opt => opt.EnableDetailedErrors = true);
                    services.AddCors(opt =>
                    {
                        opt.AddPolicy("SignalRCors", policy =>
                        {
                            policy.SetIsOriginAllowed(_ => true)
                                  .AllowAnyHeader()
                                  .AllowAnyMethod()
                                  .AllowCredentials();
                        });
                    });

                    // ==================== 独立启动SignalR Web主机（解决WPF架构冲突） ====================
                    services.AddSingleton<IHostedService, SignalRWebHostService>();

                    // ==================== 原有业务服务（完全无修改） ====================
                    services.AddSingleton<IEventAggregator, EventAggregator>();
                    services.AddDbContext<CrawlerDbContext>(opt =>
                        opt.UseSqlite(context.Configuration.GetConnectionString("ConnectionString")));

                    services.AddScoped<ITaskDataService, TaskDataService>();
                    services.AddScoped<INodeManagementService, NodeManagementService>();
                    services.AddSingleton<NodeHealthCheckService>();
                    services.AddSingleton<FailedTaskRecoveryService>();
                    services.AddSingleton<ILoadBalancingScheduler, LoadBalancingScheduler>();
                    services.AddHostedService<GrpcHostedService>();
                    services.AddSingleton<SessionManager>();
                    services.AddSingleton<CrawlerCoordinatorService>();
                    services.AddSingleton<ICrawlerCoordinatorService>(sp => sp.GetRequiredService<CrawlerCoordinatorService>());
                    services.AddSingleton<CrawlerCoordinator.CrawlerCoordinatorBase>(sp => sp.GetRequiredService<CrawlerCoordinatorService>());
                    services.AddSingleton<GrpcAuthenticationInterceptor>();
                    services.AddSingleton<TaskSchedulerHostedService>();
                    services.AddHostedService(sp => sp.GetRequiredService<TaskSchedulerHostedService>());
                    services.AddSingleton<ITaskSchedulerService>(sp => sp.GetRequiredService<TaskSchedulerHostedService>());

                    // ==================== WPF UI服务 ====================
                    services.AddTransient<MainWindowViewModel>();
                    services.AddTransient<MainWindow>(sp => new MainWindow(sp.GetRequiredService<MainWindowViewModel>()));
                })
                .Build();

            // 4. 启动主机
            await AppHost.StartAsync();
            LogHelper.Info("通用主机启动完成，所有服务已注册");

            // 5. 数据库初始化（原有逻辑无修改）
            using var scope = AppHost.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CrawlerDbContext>();
            LogHelper.Info($"数据库连接: {db.Database.GetConnectionString()}");

            bool rebuildDb = false;
            if (await db.Database.CanConnectAsync())
            {
                try { _ = await db.NodeInfos.CountAsync(); }
                catch (SqliteException ex) when (ex.Message.Contains("no such table"))
                {
                    LogHelper.Warn($"表缺失，重建数据库: {ex.Message}");
                    rebuildDb = true;
                }
            }
            else rebuildDb = true;

            if (rebuildDb)
            {
                await db.Database.EnsureDeletedAsync();
                await db.Database.EnsureCreatedAsync();
                await VerifyTableExistsAsync(db, "NodeInfos");
            }

            // 6. 业务初始化（原有逻辑无修改）
            _ = scope.ServiceProvider.GetRequiredService<CrawlerCoordinatorService>();
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

    #region 辅助方法（无任何错误）
    private void ConfigureGlobalExceptionHandling()
    {
        AppDomain.CurrentDomain.UnhandledException += (s, a) =>
        {
            var ex = a.ExceptionObject as Exception;
            LogHelper.Fatal(ex, $"非UI线程异常，终止程序: {a.IsTerminating}");
            Dispatcher.Invoke(() => MessageBox.Show($"系统崩溃，请查看日志: {ex?.Message}", "错误"));
        };

        DispatcherUnhandledException += (s, a) =>
        {
            LogHelper.Error(a.Exception, "UI线程异常");
            MessageBox.Show($"操作失败: {a.Exception.Message}", "错误");
            a.Handled = true;
        };
    }

    private static async Task VerifyTableExistsAsync(CrawlerDbContext db, string table)
    {
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT name FROM sqlite_master WHERE type='table' AND name='{table}'";
        if (await cmd.ExecuteScalarAsync() == null)
            throw new InvalidOperationException($"表 {table} 创建失败");
        LogHelper.Info($"表 {table} 验证通过");
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        LogHelper.Info("Master 正常退出");
        _healthCheckService?.Stop();
        _healthCheckService?.Dispose();

        if (AppHost != null)
        {
            await AppHost.StopAsync(TimeSpan.FromSeconds(5));
            AppHost.Dispose();
        }
        Log.CloseAndFlush();
        base.OnExit(e);
    }
    #endregion
}

#region 独立SignalR Web主机服务（解决WPF+ASP.NET Core架构冲突，核心修复）
/// <summary>
/// 后台托管服务：独立启动Kestrel+SignalR，不干扰WPF主机
/// </summary>
public class SignalRWebHostService : BackgroundService
{
    private readonly IConfiguration _config;
    private readonly SignalRSettings _signalRSettings;
    private IHost? _webHost;

    public SignalRWebHostService(IConfiguration config, IOptions<SignalRSettings> signalROptions)
    {
        _config = config;
        _signalRSettings = signalROptions.Value;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _webHost = Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseKestrel()
                          .UseUrls($"http://0.0.0.0:{_signalRSettings.Port}")
                          .ConfigureServices(services =>
                          {
                              services.AddSignalR(opt => opt.EnableDetailedErrors = true);
                              services.AddCors(opt =>
                              {
                                  opt.AddPolicy("SignalRCors", policy =>
                                  {
                                      policy.SetIsOriginAllowed(_ => true)
                                            .AllowAnyHeader()
                                            .AllowAnyMethod()
                                            .AllowCredentials();
                                  });
                              });
                          })
                          .Configure(app =>
                          {
                              app.UseCors("SignalRCors");
                              app.UseRouting();
                              app.UseEndpoints(end => end.MapHub<CrawlerHub>(_signalRSettings.HubPath));
                          });
            })
            .Build();

        return _webHost.StartAsync(stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_webHost != null)
        {
            await _webHost.StopAsync(cancellationToken);
            _webHost.Dispose();
            _webHost = null;
        }
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _webHost?.Dispose();
        _webHost = null;
        base.Dispose();
    }
}
#endregion