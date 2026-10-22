using System.Collections.Concurrent;
using System.Net;
using CrawlerCore.Entities;
using CrawlerCore.Events;
using CrawlerCore.Interfaces;
using CrawlerMaster.WPF.Data;
using CrawlerMaster.WPF.Hosting;
using CrawlerMaster.WPF.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Context;
using TaskStatus = CrawlerCore.Entities.TaskStatus;

namespace CrawlerCore.Tests.Integration.Infrastructure;

/// <summary>
/// 进程内 Master：不创建 WPF 界面，按 App.OnStartup 的顺序启动同一套后台服务
/// （gRPC 协调、任务调度、实时推送、故障恢复、健康检查），使用临时 SQLite 文件。
/// 与生产启动的差别只有：Kestrel 只监听环回地址的随机端口（不开 SignalR 端口），配置来自内存。
/// </summary>
public sealed class TestMaster : IAsyncDisposable
{
    /// <summary>Master 与 Worker 共用的测试令牌</summary>
    public const string AuthToken = "integration-test-token";

    /// <summary>测试中缩短的健康检查参数：每 1 秒检查，5 秒无心跳判离线（生产默认 15 秒 / 60 秒）</summary>
    public const int HealthCheckIntervalSeconds = 1;
    public const int NodeTimeoutSeconds = 5;

    private readonly IHost _host;
    private NodeHealthCheckService? _healthCheck;
    private bool _stopped;

    private TestMaster(IHost host, int grpcPort, string name)
    {
        _host = host;
        GrpcPort = grpcPort;
        Name = name;
    }

    /// <summary>gRPC 实际监听端口（环回地址）</summary>
    public int GrpcPort { get; }

    /// <summary>日志中的组件名</summary>
    public string Name { get; }

    public IServiceProvider Services => _host.Services;

    public IEventAggregator Events => Services.GetRequiredService<IEventAggregator>();

    /// <summary>
    /// 启动 Master。顺序与 App.OnStartup 一致：启动主机 → 初始化数据库 → 恢复 Pending 任务、启动健康检查、激活故障恢复。
    /// </summary>
    public static async Task<TestMaster> StartAsync(string databasePath, string name = "Master")
    {
        var settings = new Dictionary<string, string?>
        {
            ["GrpcSettings:MasterEndpoint"] = "http://127.0.0.1:0",
            ["GrpcSettings:NodeAuthToken"] = AuthToken,
            ["GrpcSettings:HeartbeatIntervalSeconds"] = "1",
            ["GrpcSettings:MaxRetryAttempts"] = "3",
            ["GrpcSettings:RetryDelayMilliseconds"] = "200",
            ["GrpcSettings:MaxMessageSizeMb"] = "64",
            ["Database:ConnectionString"] = $"Data Source={databasePath}",
            ["HealthCheckSettings:CheckIntervalSeconds"] = HealthCheckIntervalSeconds.ToString(),
            ["HealthCheckSettings:NodeTimeoutSeconds"] = NodeTimeoutSeconds.ToString(),
            ["CrawlerSettings:EnableProgressSimulation"] = "false",
        };

        var host = Host.CreateDefaultBuilder()
            // 校验 DI 生命周期：单例不得从根容器解析 Scoped 服务（CLAUDE.md 约定）
            .UseDefaultServiceProvider(options =>
            {
                options.ValidateScopes = true;
                options.ValidateOnBuild = true;
            })
            .ConfigureAppConfiguration((_, configuration) =>
            {
                // 不读取输出目录里的 appsettings.json 与环境变量，保证测试配置确定
                configuration.Sources.Clear();
                configuration.AddInMemoryCollection(settings);
            })
            .UseSerilog()
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseKestrel(options =>
                    options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));
                webBuilder.ConfigureServices((context, services) => services.AddMasterWebServices(context.Configuration));
                webBuilder.Configure((context, app) => app.UseMasterEndpoints(context.Configuration));
            })
            .ConfigureServices((context, services) =>
            {
                services.AddMasterCoreServices(context.Configuration);
                services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(5));
            })
            .Build();

        // 让该主机启动的后台循环都带上 Component 日志属性（异步上下文随任务流转）
        using (LogContext.PushProperty("Component", name))
        {
            await host.StartAsync();

            var address = host.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First();
            var port = new Uri(address).Port;

            var master = new TestMaster(host, port, name);
            await host.Services.InitializeMasterDatabaseAsync();
            master._healthCheck = await host.Services.StartMasterBackgroundServicesAsync();
            Log.Information("测试 Master 已启动，gRPC 端口 {Port}，数据库 {Database}", port, databasePath);
            return master;
        }
    }

    /// <summary>与界面“创建任务”相同的入口：经 ITaskSchedulerService 持久化并入队</summary>
    public async Task<CrawlerTask> ScheduleTaskAsync(CrawlerTask task)
    {
        using var scope = Services.CreateScope();
        var scheduler = scope.ServiceProvider.GetRequiredService<ITaskSchedulerService>();
        Assert.True(await scheduler.ScheduleTaskAsync(task), $"任务 {task.TaskName} 入队失败");
        return task;
    }

    /// <summary>每次新建作用域查询数据库（不跟踪实体），读取的是已提交的最新状态</summary>
    public async Task<T> QueryAsync<T>(Func<CrawlerDbContext, Task<T>> query)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CrawlerDbContext>();
        return await query(db);
    }

    public Task<NodeInfo?> GetNodeAsync(string nodeId)
        => QueryAsync(db => db.NodeInfos.AsNoTracking().FirstOrDefaultAsync(n => n.NodeId == nodeId));

    public Task<CrawlerTask?> GetTaskAsync(string taskId)
        => QueryAsync(db => db.CrawlerTasks.AsNoTracking().FirstOrDefaultAsync(t => t.TaskId == taskId));

    /// <summary>经界面使用的 ITaskDataService 读取任务的单页结果；尚无结果时返回 null</summary>
    public async Task<List<PageResult>?> GetPageResultsAsync(string taskId)
    {
        using var scope = Services.CreateScope();
        var dataService = scope.ServiceProvider.GetRequiredService<ITaskDataService>();
        var result = await dataService.GetCrawlResultByTaskIdAsync(taskId);
        return result == null ? null : await dataService.GetPageResultsByResultIdAsync(result.Id);
    }

    public Task<int> CountCrawlResultsAsync(string taskId)
        => QueryAsync(db => db.CrawlResults.CountAsync(r => r.TaskId == taskId));

    /// <summary>等待节点注册、心跳均已被处理且状态为 Online</summary>
    public Task<NodeInfo> WaitForNodeOnlineAsync(string nodeId)
        => Poll.ValueAsync(async () =>
        {
            var node = await GetNodeAsync(nodeId);
            return node is { Status: NodeStatus.Online, CpuUsage: TestNodeInfoCollector.FixedCpuUsage } ? node : null;
        }, $"节点 {nodeId} 注册并上报心跳后变为 Online");

    public Task WaitForNodeStatusAsync(string nodeId, NodeStatus status, TimeSpan? timeout = null)
        => Poll.UntilAsync(async () => (await GetNodeAsync(nodeId))?.Status == status,
            $"节点 {nodeId} 状态变为 {status}", timeout);

    /// <summary>等待任务状态为 Completed 且爬取结果已落库，返回单页结果</summary>
    public Task<List<PageResult>> WaitForTaskCompletedAsync(string taskId, TimeSpan? timeout = null)
        => Poll.ValueAsync(async () =>
        {
            var task = await GetTaskAsync(taskId);
            if (task?.Status != TaskStatus.Completed) return null;
            return await GetPageResultsAsync(taskId);
        }, $"任务 {taskId} 完成且结果落库", timeout);

    /// <summary>开始记录 Master 发布的任务进度事件（即界面 ViewModel 收到的事件）</summary>
    public TaskProgressRecorder RecordTaskProgress() => new(Events);

    /// <summary>与 App.OnExit 相同：先停健康检查，再停止主机</summary>
    public async Task StopAsync()
    {
        if (_stopped) return;
        _stopped = true;

        _healthCheck?.Stop();
        await _host.StopAsync(TimeSpan.FromSeconds(5));
        _host.Dispose();
        Log.Information("测试 Master {Name} 已停止", Name);
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}

/// <summary>
/// 记录 Master 事件聚合器上的 <see cref="TaskProgressUpdatedEventArgs"/>，按发布顺序保存。
/// </summary>
public sealed class TaskProgressRecorder : IDisposable
{
    private readonly IEventAggregator _events;
    private readonly Guid _subscriptionId;
    private readonly ConcurrentQueue<TaskProgressUpdatedEventArgs> _received = new();

    public TaskProgressRecorder(IEventAggregator events)
    {
        _events = events;
        _subscriptionId = events.Subscribe<TaskProgressUpdatedEventArgs>((_, args) => _received.Enqueue(args));
    }

    /// <summary>某个任务的进度事件（按发布顺序）</summary>
    public IReadOnlyList<TaskProgressUpdatedEventArgs> For(string taskId)
        => _received.Where(e => e.TaskId == taskId).ToList();

    public void Dispose() => _events.Unsubscribe<TaskProgressUpdatedEventArgs>(_subscriptionId);
}
