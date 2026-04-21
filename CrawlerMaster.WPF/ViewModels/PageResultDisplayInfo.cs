using CrawlerCore.Entities;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;

namespace CrawlerMaster.WPF.ViewModels
{
    /// <summary>
    /// 单页爬取结果UI展示模型
    /// </summary>
    public class PageResultDisplayInfo
    {
        /// <summary>
        /// 页面主键ID
        /// </summary>
        public long Id { get; set; }

        /// <summary>
        /// 目标URL
        /// </summary>
        public string Url { get; set; } = string.Empty;

        /// <summary>
        /// 最终URL（重定向后）
        /// </summary>
        public string FinalUrl { get; set; } = string.Empty;

        /// <summary>
        /// HTTP状态码
        /// </summary>
        public int StatusCode { get; set; }

        /// <summary>
        /// 状态码描述（UI友好展示）
        /// </summary>
        public string StatusDescription => StatusCode switch
        {
            200 => "成功",
            404 => "页面不存在",
            500 => "服务器错误",
            403 => "访问被拒绝",
            _ => "其他"
        };

        /// <summary>
        /// 提取的链接数量
        /// </summary>
        public int LinkCount { get; set; }

        /// <summary>
        /// 解析后的结构化数据（用于详情展示）
        /// </summary>
        public Dictionary<string, object> ParsedData { get; set; } = new();

        /// <summary>
        /// 原始实体
        /// </summary>
        public PageResult RawEntity { get; set; } = null!;

        // 从实体映射的静态方法
        public static PageResultDisplayInfo FromEntity(PageResult entity)
        {
            return new PageResultDisplayInfo
            {
                Id = entity.Id,
                Url = entity.Url,
                FinalUrl = entity.FinalUrl,
                StatusCode = entity.StatusCode,
                LinkCount = entity.Links?.Count ?? 0,
                ParsedData = entity.ParsedData,
                RawEntity = entity
            };
        }
    }
}