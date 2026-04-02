using System;
using System.Threading;
using System.Threading.Tasks;
using CrawlerCore.Infrastructure;
using Serilog;

namespace CrawlerWorker
{
    class Program
    {
        static async Task Main(string[] args)
        {
            LogConfiguration.Configure("Worker", Serilog.Events.LogEventLevel.Information);
            Log.Information("Worker 节点启动，进程ID: {ProcessId}, 命令行参数: {Args}", Environment.ProcessId, args);

            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                var ex = e.ExceptionObject as Exception;
                Log.Fatal(ex, "Worker 进程未处理异常 (IsTerminating={IsTerminating})", e.IsTerminating);
                Console.Error.WriteLine($"致命错误: {ex?.Message}");
            };

            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (sender, e) =>
            {
                Log.Warning("收到 Ctrl+C 退出信号，正在停止...");
                e.Cancel = true;
                cts.Cancel();
            };

            try
            {
                Log.Information("Worker 服务主循环开始，按 Ctrl+C 退出");
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (TaskCanceledException)
            {
                Log.Information("Worker 服务主循环已取消");
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Worker 服务主循环发生未预期异常");
                Console.WriteLine($"严重错误: {ex.Message}");
            }
            finally
            {
                Log.Information("Worker 节点正在关闭...");
                LogConfiguration.CloseAndFlush();
                Console.WriteLine("日志系统已关闭，Worker 退出。");
            }
        }
    }
}