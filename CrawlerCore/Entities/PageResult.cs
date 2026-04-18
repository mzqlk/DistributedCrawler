using CrawlerCore.Infrastructure;
using System.Collections.Generic;

namespace CrawlerCore.Entities
{
    /// <summary>
    /// 单个URL的爬取结果实体
    /// </summary>
    public class PageResult
    {
        /// <summary>
        /// 爬取的目标URL
        /// </summary>
        public string Url { get; set; } = string.Empty;

        /// <summary>
        /// 重定向后的最终URL（如果有重定向，否则与Url相同）
        /// </summary>
        public string FinalUrl { get; set; } = string.Empty;

        /// <summary>
        /// HTTP响应状态码（如200/404/500）
        /// </summary>
        public int StatusCode { get; set; }

        /// <summary>
        /// 页面解析后的数据（键值对，如标题、内容、发布时间）
        /// </summary>
        public Dictionary<string, object> ParsedData { get; set; }

        /// <summary>
        /// 从页面中提取的所有链接（用于深度爬取）
        /// </summary>
        public List<string> Links { get; set; }

        /// <summary>
        /// 构造函数：初始化集合属性，避免空引用
        /// </summary>
        public PageResult()
        {
            ParsedData = new Dictionary<string, object>();
            Links = new List<string>();
            LogHelper.Debug("PageResult 实例创建");
        }
    }
}