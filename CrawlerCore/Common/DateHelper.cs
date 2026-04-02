using System;

namespace CrawlerCore.Common
{
    /// <summary>
    /// 日期处理工具类（统一UTC/本地时间转换，避免时区混乱）
    /// </summary>
    public static class DateHelper
    {
        private const string DefaultDateTimeFormat = "yyyy-MM-dd HH:mm:ss";

        /// <summary>
        /// 将DateTime转换为UTC格式字符串
        /// </summary>
        /// <param name="dateTime">待转换的DateTime（若为null，返回空字符串）</param>
        /// <param name="format">自定义格式（默认使用yyyy-MM-dd HH:mm:ss）</param>
        /// <returns>格式化后的UTC时间字符串</returns>
        /// <remarks>无论输入是Local/Utc/Unspecified，均转换为UTC时间后格式化</remarks>
        public static string ToUtcString(DateTime? dateTime, string format = null)
        {
            if (!dateTime.HasValue) return string.Empty;
            return dateTime.Value.ToUniversalTime().ToString(format ?? DefaultDateTimeFormat);
        }

        /// <summary>
        /// 将DateTime转换为本地时间格式字符串
        /// </summary>
        /// <param name="dateTime">待转换的DateTime（若为null，返回空字符串）</param>
        /// <param name="format">自定义格式（默认使用yyyy-MM-dd HH:mm:ss）</param>
        /// <returns>格式化后的本地时间字符串</returns>
        /// <remarks>无论输入是Local/Utc/Unspecified，均转换为本地时间后格式化</remarks>
        public static string ToLocalString(DateTime? dateTime, string format = null)
        {
            if (!dateTime.HasValue) return string.Empty;
            return dateTime.Value.ToLocalTime().ToString(format ?? DefaultDateTimeFormat);
        }

        /// <summary>
        /// 计算两个DateTime的时间差（总秒数）
        /// </summary>
        /// <param name="start">开始时间</param>
        /// <param name="end">结束时间</param>
        /// <returns>时间差（秒），若任一参数为null返回-1；end<start返回负数</returns>
        public static double GetTimeSpanSeconds(DateTime? start, DateTime? end)
        {
            if (!start.HasValue || !end.HasValue) return -1;
            return (end.Value - start.Value).TotalSeconds;
        }
    }
}