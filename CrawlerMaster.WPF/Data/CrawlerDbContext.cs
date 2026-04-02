using CrawlerCore.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace CrawlerMaster.WPF.Data
{
    public class CrawlerDbContext : DbContext
    {
        // 全局JSON配置（避免重复创建，优化性能）
        private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };
        private readonly IConfiguration _configuration;

        public CrawlerDbContext(DbContextOptions<CrawlerDbContext> options, IConfiguration configuration)
            : base(options)
        {
            _configuration = configuration;
        }

        #region 实体集
        public DbSet<CrawlerTask> CrawlerTasks => Set<CrawlerTask>();
        public DbSet<CrawlResult> CrawlResults => Set<CrawlResult>();
        public DbSet<NodeInfo> NodeInfos => Set<NodeInfo>();
        public DbSet<PageResult> PageResults => Set<PageResult>();
        #endregion

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            #region 爬虫任务配置
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

                // JSON 存储集合（空值防护）
                entity.Property(e => e.StartUrls)
                        .HasConversion(v => JsonSerializer.Serialize(v, _jsonOptions),
                                    v => JsonSerializer.Deserialize<List<string>>(v, _jsonOptions) ?? new List<string>())
                        .HasColumnType("TEXT");

                entity.Property(e => e.Configuration)
                        .HasConversion(v => JsonSerializer.Serialize(v, _jsonOptions),
                                    v => JsonSerializer.Deserialize<Dictionary<string, string>>(v, _jsonOptions) ?? new())
                        .HasColumnType("TEXT");
            });
            #endregion

            #region 爬取结果配置
            modelBuilder.Entity<CrawlResult>(entity =>
            {
                entity.Property<long>("Id").ValueGeneratedOnAdd();
                entity.HasKey("Id");
                entity.ToTable("CrawlResults");
                entity.Property(e => e.TaskId).IsRequired();
                entity.HasMany(e => e.PageResults).WithOne().HasForeignKey("ResultId").OnDelete(DeleteBehavior.Cascade);
            });
            #endregion

            #region 页面结果配置
            modelBuilder.Entity<PageResult>(entity =>
            {
                entity.Property<long>("Id").ValueGeneratedOnAdd();
                entity.HasKey("Id");
                entity.ToTable("PageResults");
                entity.Property(e => e.Url).IsRequired().HasMaxLength(2048);

                entity.Property(e => e.ParsedData)
                      .HasConversion(v => JsonSerializer.Serialize(v, _jsonOptions),
                                     v => JsonSerializer.Deserialize<Dictionary<string, object>>(v, _jsonOptions) ?? new())
                      .HasColumnType("TEXT");

                entity.Property(e => e.Links)
                      .HasConversion(v => JsonSerializer.Serialize(v, _jsonOptions),
                                     v => JsonSerializer.Deserialize<List<string>>(v, _jsonOptions) ?? new List<string>())
                      .HasColumnType("TEXT");
            });
            #endregion

            #region 节点信息配置
            modelBuilder.Entity<NodeInfo>(entity =>
            {
                entity.HasKey(e => e.NodeId);
                entity.ToTable("NodeInfos");
                entity.Property(e => e.MachineName).HasMaxLength(50);
                entity.Property(e => e.IpAddress).HasMaxLength(39);
                entity.Property(e => e.Status).HasConversion<string>().HasDefaultValue(NodeStatus.Pending);
                entity.Property(e => e.LastHeartbeat).IsRequired().HasDefaultValueSql("DATETIME('now')");
            });
            #endregion

            base.OnModelCreating(modelBuilder);
        }
    }
}