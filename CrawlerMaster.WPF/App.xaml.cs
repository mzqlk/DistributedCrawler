using CrawlerCore.Entities;
using CrawlerCore.Events;
using CrawlerCore.Infrastructure;
using CrawlerCore.Interfaces;
using CrawlerCore.Protos;
using CrawlerMaster.WPF.Data;
using CrawlerMaster.WPF.Infrastructure;
using CrawlerMaster.WPF.Services;
using CrawlerMaster.WPF.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Serilog;
using System;
using System.Configuration;
using System.Windows;

namespace CrawlerMaster.WPF
{
    public partial class App : Application
    {
        public static IHost Host { get; private set; } = null!;
        private NodeHealthCheckService? _healthCheckService;

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            try
            {
                // 1. 优先初始化日志（确保启动过程全链路可追溯）
                LogConfiguration.Configure("Master", Serilog.Events.LogEventLevel.Information);
                Log.Information("Master 控制端启动，版本: {Version}", GetType().Assembly.GetName().Version);

                // 2. 注册全局异常处理
                ConfigureGlobalExceptionHandling();

                // 3. 构建通用主机，传入已初始化的 Logger
                Host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder(e.Args)
                    .UseSerilog(Log.Logger)          // 复用已有 Logger
                    .ConfigureServices(ConfigureServices)
                    .Build();

                await Host.StartAsync();

                // 4. 数据库初始化
                using (var scope = Host.Services.CreateScope())
                {
                    var dbContext = scope.ServiceProvider.GetRequiredService<CrawlerDbContext>();

                    // 1. 记录数据库文件路径（便于排查）
                    var connString = dbContext.Database.GetConnectionString();
                    Log.Information("数据库连接字符串: {ConnectionString}", connString);

                    // 2. 尝试连接并验证表结构
                    bool needRecreate = false;
                    if (await dbContext.Database.CanConnectAsync())
                    {
                        try
                        {
                            _ = await dbContext.NodeInfos.CountAsync();
                            Log.Information("数据库表结构验证通过");
                        }
                        catch (SqliteException ex) when (ex.Message.Contains("no such table"))
                        {
                            Log.Warning("数据库表结构缺失，准备重建: {Error}", ex.Message);
                            needRecreate = true;
                        }
                    }
                    else
                    {
                        needRecreate = true;
                    }

                    if (needRecreate)
                    {
                        await dbContext.Database.EnsureDeletedAsync();
                        await dbContext.Database.EnsureCreatedAsync();

                        // 关键：再次验证 NodeInfos 表是否真的被创建
                        await VerifyTableExistsAsync(dbContext, "NodeInfos");
                    }
                }

                // 从容器解析 CrawlerCoordinatorService（确保服务实例被创建）
                using (var scope = Host.Services.CreateScope())
                {
                    var coordinator = scope.ServiceProvider.GetRequiredService<CrawlerCoordinatorService>();
                    // 可选：记录日志或做其他初始化
                    Log.Information("CrawlerCoordinatorService 已解析");
                }

                // 5. 启动节点健康检查定时器（从根容器获取单例服务）
                _healthCheckService = Host.Services.GetRequiredService<NodeHealthCheckService>();
                _healthCheckService.Start();

                // 6. 启动主窗口
                var mainWindow = Host.Services.GetRequiredService<MainWindow>();
                mainWindow.Show();
            }
            catch (Exception ex)
            {
                // 启动阶段致命错误兜底（日志未初始化时也能提示）
                var errorMsg = $"启动失败：{ex.Message}\n{ex.InnerException?.Message}";
                if (Log.Logger != null) Log.Fatal(ex, "应用程序启动异常");

                MessageBox.Show(errorMsg, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
            }
        }

        /// <summary>
        /// 配置全局异常处理（UI线程 + 非UI线程）
        /// </summary>
        private void ConfigureGlobalExceptionHandling()
        {
            // 非UI线程未处理异常
            AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
            {
                var ex = args.ExceptionObject as Exception;
                Log.Fatal(ex, "应用程序域未处理异常 (IsTerminating={IsTerminating})", args.IsTerminating);

                if (Application.Current != null)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                        MessageBox.Show($"发生严重错误，请查看日志文件。\n{ex?.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error));
                }
                else
                {
                    Console.Error.WriteLine($"致命错误: {ex?.Message}");
                }
            };

            // UI线程未处理异常
            DispatcherUnhandledException += (sender, args) =>
            {
                Log.Error(args.Exception, "UI线程未处理异常");
                MessageBox.Show($"操作出错：{args.Exception.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };
        }

        // 辅助验证方法
        private static async Task VerifyTableExistsAsync(CrawlerDbContext dbContext, string tableName)
        {
            var conn = dbContext.Database.GetDbConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT name FROM sqlite_master WHERE type='table' AND name='{tableName}';";
            var result = await cmd.ExecuteScalarAsync();
            if (result == null)
            {
                throw new InvalidOperationException($"表 {tableName} 创建失败，请检查模型配置或文件权限。");
            }
            Log.Information("表 {TableName} 创建成功", tableName);
        }

        private void ConfigureServices(HostBuilderContext context, IServiceCollection services)
        {
            services.AddSingleton<IEventAggregator, EventAggregator>();

            services.Configure<GrpcSettings>(context.Configuration.GetSection("GrpcSettings"));

            // 数据库上下文
            services.AddDbContext<CrawlerDbContext>(options =>
                options.UseSqlite(context.Configuration.GetConnectionString("ConnectionString")));

            // 业务服务（原项目已有）
            services.AddScoped<ITaskDataService, TaskDataService>();
            services.AddScoped<INodeManagementService, NodeManagementService>();

            // 节点健康检查服务（单例，因需持续运行）
            services.AddSingleton<NodeHealthCheckService>();

            // ===== 任务1.5 核心服务注册 =====
            services.AddHostedService<GrpcHostedService>();
            services.AddSingleton<SessionManager>();

            // 注册 CrawlerCoordinatorService 具体类型（单例）
            services.AddSingleton<CrawlerCoordinatorService>();

            services.AddSingleton<CrawlerCoordinator.CrawlerCoordinatorBase>(sp =>
        sp.GetRequiredService<CrawlerCoordinatorService>());

            services.AddSingleton<GrpcAuthenticationInterceptor>();

            // 同时注册为 IHostedService（后台运行）和 ITaskSchedulerService（对外接口）
            services.AddSingleton<TaskSchedulerHostedService>();
            services.AddHostedService(sp => sp.GetRequiredService<TaskSchedulerHostedService>());
            services.AddSingleton<ITaskSchedulerService>(sp => sp.GetRequiredService<TaskSchedulerHostedService>());

            services.AddTransient<MainWindowViewModel>();
            services.AddTransient<MainWindow>(provider =>
            {
                var viewModel = provider.GetRequiredService<MainWindowViewModel>();
                return new MainWindow(viewModel);
            });
        }

        protected override async void OnExit(ExitEventArgs e)
        {
            Log.Information("Master 控制端正常退出");

            // 停止健康检查服务
            _healthCheckService?.Stop();
            _healthCheckService?.Dispose();

            if (Host != null)
            {
                await Host.StopAsync(TimeSpan.FromSeconds(5));
                Host.Dispose();
            }

            Log.CloseAndFlush();  // 原项目清理逻辑
            base.OnExit(e);
        }
    }
}