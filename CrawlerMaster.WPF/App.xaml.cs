using CrawlerCore.Entities;
using CrawlerCore.Infrastructure;
using CrawlerCore.Interfaces;
using CrawlerMaster.WPF.Data;
using CrawlerMaster.WPF.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using System;
using System.Windows;

namespace CrawlerMaster.WPF
{
    public partial class App : Application
    {
        public static IServiceProvider ServiceProvider { get; private set; } = null!;

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
                var testTask = new CrawlerTask
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

                // 3. 依赖注入容器配置
                var services = new ServiceCollection();
                ConfigureServices(services);
                ServiceProvider = services.BuildServiceProvider();

                // 4. 异步初始化数据库
                using var scope = ServiceProvider.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<CrawlerDbContext>();
                await dbContext.Database.EnsureCreatedAsync();

                // ===================== 工作日6：数据库CRUD测试 =====================
                //await TestDatabaseCrudAsync(scope.ServiceProvider);
                // ================================================================

                // 5. 启动主窗口
                var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();
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

        /// <summary>
        /// 依赖注入服务配置
        /// </summary>
        private void ConfigureServices(ServiceCollection services)
        {
            // 加载配置文件
            var config = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", false)
                .Build();
            services.AddSingleton<IConfiguration>(config);

            // 数据库上下文（SQLite）
            services.AddDbContext<CrawlerDbContext>(options =>
                options.UseSqlite(config.GetConnectionString("CrawlerDb")));

            // 业务服务注册
            services.AddScoped<ITaskDataService, TaskDataService>();
            services.AddScoped<INodeManagementService, NodeDataService>();

            // WPF窗口单例注册
            services.AddSingleton<MainWindow>();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Log.Information("Master 控制端正常退出");
            LogConfiguration.CloseAndFlush();
            base.OnExit(e);
        }
    }
}