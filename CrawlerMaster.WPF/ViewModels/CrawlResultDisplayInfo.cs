using CrawlerCore.Entities;
using CrawlerCore.Interfaces;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;

namespace CrawlerMaster.WPF.ViewModels
{
    /// <summary>
    /// 爬取总结果UI展示模型，对齐项目现有DisplayInfo规范
    /// </summary>
    public class CrawlResultDisplayInfo
    {
        /// <summary>
        /// 爬取结果主键ID（EF自增）
        /// </summary>
        public long Id { get; set; }

        /// <summary>
        /// 关联的爬虫任务ID
        /// </summary>
        public string TaskId { get; set; } = string.Empty;

        /// <summary>
        /// 关联的任务名称（从CrawlerTask映射）
        /// </summary>
        public string TaskName { get; set; } = string.Empty;

        /// <summary>
        /// 爬取总页面数
        /// </summary>
        public int TotalPages { get; set; }

        /// <summary>
        /// 任务完成时间（本地时间，适配UI展示）
        /// </summary>
        public DateTime? CompletedAt { get; set; }

        /// <summary>
        /// 原始实体（按需使用）
        /// </summary>
        public CrawlResult RawEntity { get; set; } = null!;

        public static CrawlResultDisplayInfo FromProjection(CrawlResultProjection projection)
        {
            return new CrawlResultDisplayInfo
            {
                Id = projection.Id,
                TaskId = projection.TaskId,
                TaskName = projection.TaskName,
                TotalPages = projection.PageResultCount,
                CompletedAt = projection.CompletedAt,
                RawEntity = null
            };
        }

        // 从实体映射的静态方法，统一转换规则
        public static CrawlResultDisplayInfo FromEntity(CrawlResult entity, string taskName)
        {
            return new CrawlResultDisplayInfo
            {
                Id = entity.Id,
                TaskId = entity.TaskId,
                TaskName = taskName,
                TotalPages = 0, // 初始为0，由ViewModel后续查询赋值
                CompletedAt = entity.CompletedAt,
                RawEntity = entity
            };
        }
    }
}