using CrawlerCore.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace CrawlerMaster.WPF.Data
{
    /// <summary>
    /// 数据库上下文类，负责管理爬虫任务、爬取结果和节点信息的存储和访问。
    /// </summary>
    public class CrawlerDbContext : DbContext
    {
        private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };
        private readonly IConfiguration _configuration;

        public CrawlerDbContext(DbContextOptions<CrawlerDbContext> options, IConfiguration configuration)
            : base(options)
        {
            _configuration = configuration;
        }

        public DbSet<CrawlerTask> CrawlerTasks => Set<CrawlerTask>();
        public DbSet<CrawlResult> CrawlResults => Set<CrawlResult>();
        public DbSet<NodeInfo> NodeInfos => Set<NodeInfo>();
        public DbSet<PageResult> PageResults => Set<PageResult>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<CrawlerTask>(entity =>
            {
                entity.HasKey(e => e.TaskId);
                entity.ToTable("CrawlerTasks");

                entity.Property(e => e.TaskName).IsRequired().HasMaxLength(100);
                entity.Property(e => e.PluginName).HasMaxLength(50);
                entity.Property(e => e.MaxDepth).HasDefaultValue(1);
                entity.Property(e => e.MaxPages).HasDefaultValue(0);
                entity.Property(e => e.Status).HasConversion<string>().HasDefaultValue(CrawlerCore.Entities.TaskStatus.Pending);
                entity.Property(e => e.CreatedAt).IsRequired().HasDefaultValueSql("DATETIME('now')");
                entity.Property(e => e.Priority).HasDefaultValue(1).IsRequired();

                entity.HasIndex(e => e.AssignedNode);
                entity.HasIndex(e => e.Status);

                // 修复：添加StartUrls的ValueComparer
                entity.Property(e => e.StartUrls)
                        .HasConversion(
                            v => JsonSerializer.Serialize(v, _jsonOptions),
                            v => JsonSerializer.Deserialize<List<string>>(v, _jsonOptions) ?? new List<string>())
                        .HasColumnType("TEXT")
                        .Metadata.SetValueComparer(
                            new ValueComparer<List<string>>(
                                (c1, c2) => c1.SequenceEqual(c2),
                                c => c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())),
                                c => c.ToList()));

                // 修复：添加Configuration的ValueComparer
                entity.Property(e => e.Configuration)
                        .HasConversion(
                            v => JsonSerializer.Serialize(v, _jsonOptions),
                            v => JsonSerializer.Deserialize<Dictionary<string, string>>(v, _jsonOptions) ?? new Dictionary<string, string>())
                        .HasColumnType("TEXT")
                        .Metadata.SetValueComparer(
                            new ValueComparer<Dictionary<string, string>>(
                                (c1, c2) => c1.SequenceEqual(c2),
                                c => c.Aggregate(0, (a, v) => HashCode.Combine(a, v.Key.GetHashCode(), v.Value.GetHashCode())),
                                c => c.ToDictionary(x => x.Key, x => x.Value)));
            });

            modelBuilder.Entity<CrawlResult>(entity =>
            {
                entity.Property<long>("Id").ValueGeneratedOnAdd();
                entity.HasKey("Id");
                entity.ToTable("CrawlResults");
                entity.Property(e => e.TaskId).IsRequired();
                entity.HasMany(e => e.PageResults).WithOne().HasForeignKey("ResultId").OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<PageResult>(entity =>
            {
                entity.Property<long>("Id").ValueGeneratedOnAdd();
                entity.HasKey("Id");
                entity.ToTable("PageResults");
                entity.Property(e => e.Url).IsRequired().HasMaxLength(2048);

                // 修复：添加ParsedData的ValueComparer
                entity.Property(e => e.ParsedData)
                      .HasConversion(
                          v => JsonSerializer.Serialize(v, _jsonOptions),
                          v => JsonSerializer.Deserialize<Dictionary<string, object>>(v, _jsonOptions) ?? new Dictionary<string, object>())
                      .HasColumnType("TEXT")
                      .Metadata.SetValueComparer(
                          new ValueComparer<Dictionary<string, object>>(
                              (c1, c2) => c1.SequenceEqual(c2),
                              c => c.Aggregate(0, (a, v) => HashCode.Combine(a, v.Key.GetHashCode(), v.Value != null ? v.Value.GetHashCode() : 0)),
                              c => c.ToDictionary(x => x.Key, x => x.Value)));

                // 修复：添加Links的ValueComparer
                entity.Property(e => e.Links)
                      .HasConversion(
                          v => JsonSerializer.Serialize(v, _jsonOptions),
                          v => JsonSerializer.Deserialize<List<string>>(v, _jsonOptions) ?? new List<string>())
                      .HasColumnType("TEXT")
                      .Metadata.SetValueComparer(
                          new ValueComparer<List<string>>(
                              (c1, c2) => c1.SequenceEqual(c2),
                              c => c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())),
                              c => c.ToList()));
            });

            modelBuilder.Entity<NodeInfo>(entity =>
            {
                entity.HasKey(e => e.NodeId);
                entity.ToTable("NodeInfos");
                entity.Property(e => e.MachineName).HasMaxLength(256);
                entity.Property(e => e.IpAddress).HasMaxLength(64);
                entity.Property(e => e.Status).HasConversion<string>().HasDefaultValue(NodeStatus.Pending);
                entity.Property(e => e.LastHeartbeat).IsRequired().HasDefaultValueSql("DATETIME('now')");
            });

            base.OnModelCreating(modelBuilder);
        }
    }
}