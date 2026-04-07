using System;
using System.Runtime.CompilerServices;
using Serilog;

namespace CrawlerCore.Infrastructure
{
    /// <summary>
    /// 统一日志辅助类，封装 Serilog 常用级别。
    /// </summary>
    public static class LogHelper
    {
        /// <summary>
        /// 记录调试级别日志（开发环境使用）
        /// </summary>
        public static void Debug(string message, [CallerMemberName] string member = "")
        {
            Log.Debug("[{Member}] {Message}", member, message);
        }

        /// <summary>
        /// 记录信息级别日志（简单消息）
        /// </summary>
        public static void Info(string message, [CallerMemberName] string member = "")
        {
            Log.Information("[{Member}] {Message}", member, message);
        }

        /// <summary>
        /// 记录信息级别日志（格式化参数）
        /// </summary>
        public static void Info(string format, params object[] args)
        {
            Log.Information(format, args);
        }

        /// <summary>
        /// 记录警告级别日志
        /// </summary>
        public static void Warn(string message, [CallerMemberName] string member = "")
        {
            Log.Warning("[{Member}] {Message}", member, message);
        }

        /// <summary>
        /// 记录错误级别日志（无异常）
        /// </summary>
        public static void Error(string message, [CallerMemberName] string member = "")
        {
            Log.Error("[{Member}] {Message}", member, message);
        }

        /// <summary>
        /// 记录错误级别日志（带异常）
        /// </summary>
        public static void Error(Exception ex, string message, [CallerMemberName] string member = "")
        {
            Log.Error(ex, "[{Member}] {Message}", member, message);
        }

        /// <summary>
        /// 记录致命级别日志
        /// </summary>
        public static void Fatal(string message, [CallerMemberName] string member = "")
        {
            Log.Fatal("[{Member}] {Message}", member, message);
        }

        /// <summary>
        /// 记录致命级别日志（带异常）
        /// </summary>
        public static void Fatal(Exception ex, string message, [CallerMemberName] string member = "")
        {
            Log.Fatal(ex, "[{Member}] {Message}", member, message);
        }
    }
}