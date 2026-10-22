using Microsoft.Data.Sqlite;
using Serilog;
using Serilog.Events;
using Xunit.Abstractions;

namespace CrawlerCore.Tests.Integration.Infrastructure;

/// <summary>
/// 集成测试基类：每个测试一个临时目录（SQLite 数据库文件）、一个静态测试网站，
/// 日志输出到测试输出；结束时按 Worker → Master → 网站 的顺序清理，并删除临时目录。
/// </summary>
public abstract class IntegrationTestBase : IAsyncLifetime
{
    private readonly List<TestWorker> _workers = new();
    private readonly List<TestMaster> _masters = new();
    private readonly List<RawNodeClient> _rawNodes = new();

    protected IntegrationTestBase(ITestOutputHelper output)
    {
        Output = output;
        TempDirectory = Path.Combine(Path.GetTempPath(), "DistributedCrawler.IntegrationTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(TempDirectory);
        DatabasePath = Path.Combine(TempDirectory, "crawler.db");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .MinimumLevel.Override("Grpc", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Sink(new TestOutputLogSink(output))
            .CreateLogger();
    }

    protected ITestOutputHelper Output { get; }

    /// <summary>本测试独占的临时目录</summary>
    protected string TempDirectory { get; }

    /// <summary>临时 SQLite 数据库文件，Master 重启时复用同一个文件</summary>
    protected string DatabasePath { get; }

    protected TestWebSite Site { get; private set; } = null!;

    public virtual async Task InitializeAsync()
    {
        Site = await TestWebSite.StartAsync();
    }

    protected async Task<TestMaster> StartMasterAsync(string name = "Master")
    {
        var master = await TestMaster.StartAsync(DatabasePath, name);
        _masters.Add(master);
        return master;
    }

    protected async Task<TestWorker> StartWorkerAsync(string nodeId, TestMaster master, int maxConcurrentTasks = 2,
        int? reportedMaxConcurrentTasks = null, bool startDisconnected = false)
    {
        var worker = await TestWorker.StartAsync(nodeId, master.GrpcPort, maxConcurrentTasks, reportedMaxConcurrentTasks, startDisconnected);
        _workers.Add(worker);
        return worker;
    }

    /// <summary>新建一个直接调用 Master 接口的假节点（不经过代理），测试结束时自动关闭</summary>
    protected RawNodeClient CreateRawNode(TestMaster master, string nodeId, string? token = TestMaster.AuthToken)
    {
        var node = RawNodeClient.Create(master.GrpcPort, nodeId, token);
        _rawNodes.Add(node);
        return node;
    }

    /// <summary>新建一个爬取测试网站的任务（MockTestPlugin，默认深度 2）</summary>
    protected CrawlerCore.Entities.CrawlerTask NewSiteTask(string name, int maxDepth = 2, int maxPages = 10, int priority = 3)
        => new()
        {
            TaskName = name,
            PluginName = "MockTestPlugin",
            StartUrls = [Site.PageUrl("index.html")],
            MaxDepth = maxDepth,
            MaxPages = maxPages,
            Priority = priority
        };

    public virtual async Task DisposeAsync()
    {
        // 先停 Worker 与假节点（关闭到 Master 的流），再停 Master，避免 Master 停机时等待未结束的流
        foreach (var node in Enumerable.Reverse(_rawNodes))
        {
            await SafeAsync(() => node.DisposeAsync().AsTask(), $"关闭假节点 {node.NodeId}");
        }
        foreach (var worker in Enumerable.Reverse(_workers))
        {
            await SafeAsync(() => worker.DisposeAsync().AsTask(), $"停止 Worker {worker.NodeId}");
        }
        foreach (var master in Enumerable.Reverse(_masters))
        {
            await SafeAsync(() => master.DisposeAsync().AsTask(), $"停止 {master.Name}");
        }
        if (Site != null)
        {
            await SafeAsync(() => Site.DisposeAsync().AsTask(), "停止测试网站");
        }

        // SQLite 连接池会持有文件句柄，清空后才能删除临时目录
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(TempDirectory, recursive: true);
        }
        catch (Exception ex)
        {
            Output.WriteLine($"临时目录删除失败（不影响测试结果）：{TempDirectory}，{ex.Message}");
        }

        await Log.CloseAndFlushAsync();
    }

    private async Task SafeAsync(Func<Task> action, string description)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Output.WriteLine($"清理时出错（{description}）：{ex}");
        }
    }
}
