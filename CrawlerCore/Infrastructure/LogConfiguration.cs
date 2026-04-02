using System;
using System.IO;
using Serilog;
using Serilog.Events;

namespace CrawlerCore.Infrastructure
{
    public static class LogConfiguration
    {
        /// <summary>
        /// 初始化全局 Serilog 日志系统
        /// </summary>
        /// <param name="appName">应用名称（Master / Worker）</param>
        /// <param name="minimumLevel">最低日志级别，默认 Information</param>
        /// <exception cref="InvalidOperationException">日志目录创建失败时抛出</exception>
        public static void Configure(string appName, LogEventLevel minimumLevel = LogEventLevel.Information)
        {
            var logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            try
            {
                if (!Directory.Exists(logDir))
                    Directory.CreateDirectory(logDir);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"无法创建日志目录 {logDir}", ex);
            }

            const string outputTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [{Application}] [{SourceContext}] {Message:lj}{NewLine}{Exception}";

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Is(minimumLevel)
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .MinimumLevel.Override("System", LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("Application", appName)
                .WriteTo.Console(outputTemplate: outputTemplate)
                .WriteTo.File(
                    Path.Combine(logDir, "log-.txt"),
                    rollingInterval: RollingInterval.Day,
                    outputTemplate: outputTemplate,
                    retainedFileCountLimit: 7,
                    fileSizeLimitBytes: 10 * 1024 * 1024,
                    rollOnFileSizeLimit: true
                )
                .CreateLogger();
        }

        /// <summary>
        /// 安全关闭日志系统（刷新所有缓冲，释放资源）
        /// </summary>
        public static void CloseAndFlush()
        {
            Log.CloseAndFlush();
        }
    }
}