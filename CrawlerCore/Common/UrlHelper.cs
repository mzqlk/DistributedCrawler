using System;

namespace CrawlerCore.Common
{
    /// <summary>
    /// URL处理工具类
    /// </summary>
    public static class UrlHelper
    {
        /// <summary>
        /// 验证URL是否合法（必须包含协议，如http/https，等价于Uri.TryCreate）
        /// </summary>
        /// <param name="url">待验证的URL字符串（null/空返回false）</param>
        /// <returns>合法返回true，否则返回false</returns>
        public static bool IsValidUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            return Uri.TryCreate(url, UriKind.Absolute, out _);
        }

        /// <summary>
        /// 拼接基础URL与相对路径，自动处理斜杠重复问题
        /// </summary>
        /// <param name="baseUrl">基础URL（如https://example.com，null/空返回relativePath）</param>
        /// <param name="relativePath">相对路径（如/path/to/page，null/空返回baseUrl）</param>
        /// <returns>拼接后的完整URL</returns>
        public static string CombineUrl(string baseUrl, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(baseUrl)) return relativePath ?? string.Empty;
            if (string.IsNullOrWhiteSpace(relativePath)) return baseUrl;

            // 移除基础URL末尾斜杠，相对路径开头斜杠，避免重复
            string cleanBase = baseUrl.TrimEnd('/');
            string cleanRelative = relativePath.TrimStart('/');
            return $"{cleanBase}/{cleanRelative}";
        }
    }
}