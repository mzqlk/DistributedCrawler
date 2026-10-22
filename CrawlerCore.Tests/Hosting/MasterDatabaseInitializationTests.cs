using CrawlerCore.Entities;
using CrawlerMaster.WPF.Data;
using CrawlerMaster.WPF.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CrawlerCore.Tests.Hosting;

/// <summary>
/// 数据库初始化（结构校验、重建前备份）的单元测试（T4：M-23）。每个测试使用独立的临时目录，不接触真实的 crawler.db
/// </summary>
public sealed class MasterDatabaseInitializationTests : IDisposable
{
    private readonly string _directory;
    private readonly string _databasePath;
    private readonly ServiceProvider _services;

    public MasterDatabaseInitializationTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "DistributedCrawler.DbInitTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "crawler.db");

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddDbContext<CrawlerDbContext>(o => o.UseSqlite($"Data Source={_databasePath}"));
        _services = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    #region 辅助方法
    private void Execute(string sql, string? path = null)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path ?? _databasePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private long Scalar(string sql, string? path = null)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path ?? _databasePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    /// <summary>用当前模型建库并写入一个任务</summary>
    private void CreateCurrentSchemaWithTask(string taskName)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CrawlerDbContext>();
        db.Database.EnsureCreated();
        db.CrawlerTasks.Add(new CrawlerTask { TaskName = taskName });
        db.SaveChanges();
    }

    private string[] BackupFiles() => Directory.GetFiles(_directory, "crawler.db.bak-*");

    private int CountTasks()
    {
        using var scope = _services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<CrawlerDbContext>().CrawlerTasks.Count();
    }
    #endregion

    [Fact(DisplayName = "数据库不存在：直接创建，不产生备份")]
    public async Task MissingDatabase_IsCreatedWithoutBackup()
    {
        var result = await _services.InitializeMasterDatabaseAsync();

        Assert.True(result.Rebuilt);
        Assert.Null(result.BackupPath);
        Assert.Empty(BackupFiles());
        Assert.Equal(0, CountTasks());
    }

    [Fact(DisplayName = "结构一致：原样使用，数据保留，不产生备份")]
    public async Task MatchingSchema_IsKept()
    {
        CreateCurrentSchemaWithTask("保留的任务");
        SqliteConnection.ClearAllPools();

        var result = await _services.InitializeMasterDatabaseAsync();

        Assert.False(result.Rebuilt);
        Assert.Empty(BackupFiles());
        Assert.Equal(1, CountTasks());
    }

    [Fact(DisplayName = "旧库缺少新增的唯一索引：只检查表与列，照常使用")]
    public async Task MissingIndexOnly_IsKept()
    {
        CreateCurrentSchemaWithTask("旧库任务");
        SqliteConnection.ClearAllPools();
        Execute("DROP INDEX IF EXISTS IX_CrawlResults_TaskId");

        var result = await _services.InitializeMasterDatabaseAsync();

        Assert.False(result.Rebuilt);
        Assert.Equal(1, CountTasks());
    }

    [Fact(DisplayName = "缺表且有数据：先备份（含原有数据）再重建")]
    public async Task MissingTableWithData_IsBackedUpThenRebuilt()
    {
        CreateCurrentSchemaWithTask("需要备份的任务");
        SqliteConnection.ClearAllPools();
        Execute("DROP TABLE NodeInfos");

        var result = await _services.InitializeMasterDatabaseAsync();

        Assert.True(result.Rebuilt);
        Assert.NotNull(result.BackupPath);
        Assert.Contains("NodeInfos", result.Reason);
        Assert.True(File.Exists(result.BackupPath));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM CrawlerTasks", result.BackupPath));
        Assert.Equal(0, CountTasks());
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='NodeInfos'"));
    }

    [Fact(DisplayName = "缺列且有数据：先备份再重建（实体加了字段、旧库没有重建）")]
    public async Task MissingColumnWithData_IsBackedUpThenRebuilt()
    {
        // 旧版本的任务表：少了 Priority 列
        Execute("""
            CREATE TABLE CrawlerTasks (TaskId TEXT PRIMARY KEY, TaskName TEXT NOT NULL, StartUrls TEXT NOT NULL, PluginName TEXT NOT NULL,
                Configuration TEXT NOT NULL, MaxDepth INTEGER NOT NULL, MaxPages INTEGER NOT NULL, Status TEXT NOT NULL,
                AssignedNode TEXT NOT NULL, CreatedAt TEXT NOT NULL);
            INSERT INTO CrawlerTasks VALUES ('t1', '旧任务', '[]', 'p', '{}', 1, 0, 'Pending', '', '2026-01-01');
            """);

        var result = await _services.InitializeMasterDatabaseAsync();

        Assert.True(result.Rebuilt);
        Assert.NotNull(result.BackupPath);
        Assert.Contains("Priority", result.Reason);
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM CrawlerTasks", result.BackupPath));
        Assert.Equal(0, CountTasks());
    }

    [Fact(DisplayName = "结构不符但库中没有数据：直接重建，不产生备份")]
    public async Task MismatchWithoutData_IsRebuiltWithoutBackup()
    {
        Execute("CREATE TABLE Unrelated (Id INTEGER)");

        var result = await _services.InitializeMasterDatabaseAsync();

        Assert.True(result.Rebuilt);
        Assert.Null(result.BackupPath);
        Assert.Empty(BackupFiles());
    }

    [Fact(DisplayName = "文件无法读取（不是数据库）：复制原文件作备份后重建")]
    public async Task CorruptFile_IsCopiedThenRebuilt()
    {
        var garbage = Enumerable.Range(0, 4096).Select(i => (byte)(i * 7)).ToArray();
        File.WriteAllBytes(_databasePath, garbage);

        var result = await _services.InitializeMasterDatabaseAsync();

        Assert.True(result.Rebuilt);
        Assert.NotNull(result.BackupPath);
        Assert.Equal(garbage, File.ReadAllBytes(result.BackupPath!));
        Assert.Equal(0, CountTasks());
    }
}
