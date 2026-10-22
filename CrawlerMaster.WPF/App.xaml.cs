using CrawlerCore.Infrastructure;
using CrawlerMaster.WPF.Hosting;
using CrawlerMaster.WPF.Services;
using CrawlerMaster.WPF.ViewModels;
using Microsoft.AspNetCore.Hosting;
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
            //    后台服务注册抽到 MasterHostingExtensions，与集成测试共用
            AppHost = Host.CreateDefaultBuilder(e.Args)
                .UseSerilog(Log.Logger)
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseMasterKestrel()
                        .ConfigureServices((context, services) => services.AddMasterWebServices(context.Configuration))
                        .Configure((context, app) => app.UseMasterEndpoints(context.Configuration));
                })
                .ConfigureServices((context, services) =>
                {
                    // ==================== 配置绑定、数据库与后台业务服务 ====================
                    services.AddMasterCoreServices(context.Configuration);

                    // ==================== WPF UI服务 ====================
                    services.AddSingleton<MainWindowViewModel>();
                    services.AddSingleton<NodeMonitorViewModel>();
                    services.AddSingleton<TaskManagementViewModel>();
                    services.AddSingleton<DataViewerViewModel>();
                    services.AddTransient<NodeDetailViewModel>();
                    services.AddTransient<CreateTaskViewModel>();
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

            // 5. 数据库初始化（缺表时重建）
            await AppHost.Services.InitializeMasterDatabaseAsync();

            // 6. 业务初始化：恢复Pending任务、启动健康检查、激活故障任务恢复
            _healthCheckService = await AppHost.Services.StartMasterBackgroundServicesAsync();

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

    #region 辅助方法（全异常隔离）
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