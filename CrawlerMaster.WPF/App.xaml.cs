using CrawlerCore.Infrastructure;
using CrawlerMaster.WPF.Hosting;
using CrawlerMaster.WPF.Infrastructure;
using CrawlerMaster.WPF.Services;
using CrawlerMaster.WPF.ViewModels;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;

namespace CrawlerMaster.WPF;

public partial class App : Application
{
    /// <summary>关闭主机（停止后台服务、释放容器）的最长等待时间</summary>
    private static readonly TimeSpan HostStopTimeout = TimeSpan.FromSeconds(5);

    public static IHost AppHost { get; private set; } = null!;
    private NodeHealthCheckService? _healthCheckService;
    private bool _shutdownStarted;
    private bool _shutdownCompleted;

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
                // 内容根固定为程序所在目录：appsettings.json 不随启动时的工作目录变化（例如在仓库根目录 dotnet run）
                .UseContentRoot(AppContext.BaseDirectory)
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

            // 5. 数据库初始化（结构不符时先备份再重建）
            var databaseResult = await AppHost.Services.InitializeMasterDatabaseAsync();

            // 6. 业务初始化：恢复任务、启动健康检查、激活故障任务恢复
            _healthCheckService = await AppHost.Services.StartMasterBackgroundServicesAsync();

            // 7. 启动WPF主窗口；关闭窗口时先异步停止主机，再退出进程
            var mainWindow = AppHost.Services.GetRequiredService<MainWindow>();
            mainWindow.Closing += OnMainWindowClosing;
            mainWindow.Show();

            ShowStartupNotices(mainWindow, databaseResult);
        }
        catch (Exception ex)
        {
            LogHelper.Fatal(ex, "程序启动崩溃");
            MessageBox.Show($"启动失败: {ex.Message}", "致命错误", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    #region 辅助方法（全异常隔离）
    /// <summary>
    /// 启动后需要用户知道的情况：数据库被备份重建、没有配置可用的节点令牌
    /// </summary>
    private static void ShowStartupNotices(Window owner, DatabaseInitializationResult databaseResult)
    {
        if (databaseResult.BackupPath != null)
        {
            MessageBox.Show(owner,
                $"数据库结构与当前程序不一致（{databaseResult.Reason}），已重建。\n原有数据已备份到：\n{databaseResult.BackupPath}",
                "数据库已重建", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        var interceptor = AppHost.Services.GetRequiredService<GrpcAuthenticationInterceptor>();
        if (!interceptor.IsTokenConfigured)
        {
            MessageBox.Show(owner,
                "未配置可用的节点令牌（GrpcSettings:NodeAuthToken 为空，或使用了公开的默认值 test-token），所有 Worker 的注册都会被拒绝。\n"
                + "请设置环境变量 GrpcSettings__NodeAuthToken 为一个随机字符串后重启 Master，并在 Worker 端设置相同的值（见 docs/project/deployment.md）。",
                "节点令牌未配置", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

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
    /// 主窗口第一次关闭时先取消关闭，异步停止主机（UI 线程不被阻塞，ViewModel 释放时投递到调度器的操作能执行），
    /// 完成后再退出进程。事件处理器内部捕获全部异常
    /// </summary>
    private async void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownCompleted) return;

        e.Cancel = true;
        if (_shutdownStarted) return;
        _shutdownStarted = true;

        try
        {
            await StopHostAsync();
        }
        catch (Exception ex)
        {
            LogHelper.Error(ex, "退出时停止主机失败");
        }
        finally
        {
            _shutdownCompleted = true;
            Shutdown();
        }
    }

    /// <summary>
    /// 停止健康检查与主机，并异步释放容器（容器中有只实现 IAsyncDisposable 的 ViewModel）
    /// </summary>
    private async Task StopHostAsync()
    {
        LogHelper.Info("Master 控制端开始正常退出流程");

        _healthCheckService?.Stop();
        _healthCheckService?.Dispose();

        if (AppHost == null) return;

        var viewModel = AppHost.Services.GetService<MainWindowViewModel>();
        if (viewModel != null)
        {
            await viewModel.DisposeAsync();
        }

        await AppHost.StopAsync(HostStopTimeout);
        if (AppHost is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else
        {
            AppHost.Dispose();
        }
        LogHelper.Info("Master 控制端退出完成");
    }

    /// <summary>
    /// 进程退出。正常关闭主窗口时主机已在 <see cref="OnMainWindowClosing"/> 中停止；
    /// 其他途径（例如启动失败）退出时，在线程池上尽力停止主机，并限时等待
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        if (!_shutdownCompleted && AppHost != null)
        {
            try
            {
                _healthCheckService?.Stop();
                var host = AppHost;
                if (!Task.Run(() => host.StopAsync(HostStopTimeout)).Wait(HostStopTimeout + TimeSpan.FromSeconds(1)))
                {
                    LogHelper.Warn("退出时停止主机超时");
                }
            }
            catch (Exception ex)
            {
                LogHelper.Error(ex, "退出时资源释放异常");
            }
        }

        Log.CloseAndFlush();
        base.OnExit(e);
    }
    #endregion
}
