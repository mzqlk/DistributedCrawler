using CrawlerCore.Entities;
using CrawlerCore.Infrastructure;
using CrawlerCore.Interfaces;
using CrawlerCore.Protos;
using CrawlerMaster.WPF.Data;
using CrawlerMaster.WPF.Infrastructure;
using CrawlerMaster.WPF.Services;
using CrawlerMaster.WPF.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using System;
using System.Configuration;
using System.Windows;

namespace CrawlerMaster.WPF
{
    public partial class App : Application
    {
        //public static IServiceProvider ServiceProvider { get; private set; } = null!;
        public static IHost Host { get; private set; } = null!;

        /// <summary>
        /// 测试任务与节点的CRUD操作
        /// </summary>
        private async Task TestDatabaseCrudAsync(IServiceProvider serviceProvider)
        {
            try
            {
                var taskService = serviceProvider.GetRequiredService<ITaskDataService>();
                var nodeService = serviceProvider.GetRequiredService<INodeManagementService>();

                // 1. 测试创建爬虫任务
                var testTask = new CrawlerCore.Entities.CrawlerTask
                {
                    TaskName = "测试任务_工作日6",
                    StartUrls = new List<string> { "https://example.com" },
                    PluginName = "TestPlugin",
                    MaxDepth = 2,
                    MaxPages = 10,
                    Configuration = new Dictionary<string, string>
                    {
                        ["Selector"] = "body",
                        ["Delay"] = "1000"
                    }
                };
                var addedTask = await taskService.AddTaskAsync(testTask);
                Console.WriteLine($"任务创建成功：{addedTask.TaskId}");

                // 2. 测试查询所有任务
                var allTasks = await taskService.GetAllTasksAsync();
                Console.WriteLine($"当前总任务数：{allTasks.Count}");

                // 3. 测试更新任务状态
                var updateResult = await taskService.UpdateTaskStatusAsync(addedTask.TaskId, CrawlerCore.Entities.TaskStatus.Running);
                Console.WriteLine($"任务状态更新：{updateResult}");

                // 4. 测试创建节点
                var testNode = new NodeInfo
                {
                    NodeId = "TestNode_001",
                    MachineName = "TEST-PC",
                    IpAddress = "127.0.0.1",
                    CpuCores = 8,
                    TotalMemoryGb = 16,
                    Status = NodeStatus.Online,
                    CpuUsage = 25.5,
                    MemoryUsageMb = 4096,
                    DiskUsagePercent = 30
                };
                var registeredNode = await nodeService.RegisterNodeAsync(testNode);
                Console.WriteLine($"节点注册成功：{registeredNode.NodeId}");

                // 5. 测试查询在线节点
                var activeNodes = await nodeService.GetActiveNodesAsync();
                Console.WriteLine($"在线节点数：{activeNodes.Count}");

                // 6. 测试更新节点心跳
                var heartbeatResult = await nodeService.UpdateNodeHeartbeatAsync(registeredNode.NodeId);
                Console.WriteLine($"节点心跳更新：{heartbeatResult}");

                MessageBox.Show("数据库CRUD测试完成！\n任务/节点增删改查均正常", "测试成功",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"数据库测试失败：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

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
                    await dbContext.Database.EnsureCreatedAsync();
                }

                // ===================== 工作日6：数据库CRUD测试 =====================
                //await TestDatabaseCrudAsync(scope.ServiceProvider);
                // ================================================================

                // 从容器解析 CrawlerCoordinatorService（确保服务实例被创建）
                using (var scope = Host.Services.CreateScope())
                {
                    var coordinator = scope.ServiceProvider.GetRequiredService<CrawlerCoordinatorService>();
                    // 可选：记录日志或做其他初始化
                    Log.Information("CrawlerCoordinatorService 已解析");
                }

                // 5. 启动主窗口
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

        private void ConfigureServices(HostBuilderContext context, IServiceCollection services)
        {
            // Host.CreateDefaultBuilder 已自动注册 IConfiguration，无需重复添加

            services.Configure<GrpcSettings>(context.Configuration.GetSection("GrpcSettings"));

            // 数据库上下文
            services.AddDbContext<CrawlerDbContext>(options =>
                options.UseSqlite(context.Configuration.GetConnectionString("ConnectionString")));

            // 业务服务（原项目已有）
            services.AddScoped<ITaskDataService, TaskDataService>();
            services.AddScoped<INodeManagementService, NodeDataService>();

            // ===== 任务1.5 核心服务注册 =====
            services.AddHostedService<GrpcHostedService>();
            services.AddSingleton<SessionManager>();

            // 注册 CrawlerCoordinatorService 具体类型（单例）
            services.AddSingleton<CrawlerCoordinatorService>();

            services.AddSingleton<CrawlerCoordinator.CrawlerCoordinatorBase>(sp =>
        sp.GetRequiredService<CrawlerCoordinatorService>());

            services.AddSingleton<GrpcAuthenticationInterceptor>();

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