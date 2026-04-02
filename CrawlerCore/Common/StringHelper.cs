using System;
using System.Security.Cryptography;
using System.Text;

namespace CrawlerCore.Common
{
    /// <summary>
    /// 字符串处理工具类
    /// </summary>
    public static class StringHelper
    {
        /// <summary>
        /// 判断字符串是否为空或空白字符（等价于string.IsNullOrWhiteSpace）
        /// </summary>
        /// <param name="input">待判断的字符串</param>
        /// <returns>为空/空白返回true，否则返回false</returns>
        public static bool IsNullOrEmpty(string input)
        {
            return string.IsNullOrWhiteSpace(input);
        }

        /// <summary>
        /// 生成字符串的MD5哈希值（32位小写）
        /// </summary>
        /// <param name="input">待加密的字符串（若为null，视为空字符串）</param>
        /// <returns>32位小写MD5哈希值</returns>
        public static string Md5Hash(string input)
        {
            if (IsNullOrEmpty(input)) input = string.Empty;

            using (var md5 = MD5.Create())
            {
                byte[] bytes = Encoding.UTF8.GetBytes(input);
                byte[] hash = md5.ComputeHash(bytes);
                StringBuilder sb = new StringBuilder();
                foreach (byte b in hash)
                {
                    sb.Append(b.ToString("x2")); // 转小写16进制
                }
                return sb.ToString();
            }
        }

        /// <summary>
        /// 截断字符串并添加省略号（避免过长显示）
        /// </summary>
        /// <param name="input">待截断的字符串</param>
        /// <param name="maxLength">最大长度（需≥3，否则返回原字符串；=3时返回"..."）</param>
        /// <returns>截断后的字符串（如"abc..."）</returns>
        public static string Truncate(string input, int maxLength)
        {
            if (IsNullOrEmpty(input) || maxLength < 3 || input.Length <= maxLength)
                return input;

            return $"{input.Substring(0, maxLength - 3)}...";
        }
    }
}