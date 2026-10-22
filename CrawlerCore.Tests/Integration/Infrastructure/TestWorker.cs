using CrawlerWorker.Hosting;
using CrawlerWorker.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Context;

namespace CrawlerCore.Tests.Integration.Infrastructure;

/// <summary>
/// 进程内的真实 Worker：使用与 Program.cs 相同的服务注册（<see cref="WorkerServiceCollectionExtensions.AddCrawlerWorker"/>），
/// 经 <see cref="TcpFaultProxy"/> 连接 Master，以便模拟崩溃、失联。
/// 唯一替换的组件是节点信息采集器（见 <see cref="TestNodeInfoCollector"/>）。
/// </summary>
public sealed class TestWorker : IAsyncDisposable
{
    private readonly IHost _host;
    private bool _stopped;

    private TestWorker(string nodeId, TcpFaultProxy proxy, IHost host)
    {
        NodeId = nodeId;
        Proxy = proxy;
        _host = host;
    }

    public string NodeId { get; }

    /// <summary>该 Worker 到 Master 的网络链路</summary>
    public TcpFaultProxy Proxy { get; }

    public IServiceProvider Services => _host.Services;

    /// <param name="nodeId">节点标识</param>
    /// <param name="masterGrpcPort">Master gRPC 端口（代理的上游）</param>
    /// <param name="maxConcurrentTasks">Worker 同时执行的任务数</param>
    /// <param name="reportedMaxConcurrentTasks">注册时上报的并发上限；null 表示上报真实值，0 模拟不上报上限的旧版 Worker</param>
    /// <param name="startDisconnected">代理以“断开”状态启动（Master 不可达），由测试稍后 <see cref="TcpFaultProxy.Restore"/></param>
    /// <param name="authToken">节点令牌；默认与测试 Master 一致</param>
    public static async Task<TestWorker> StartAsync(string nodeId, int masterGrpcPort, int maxConcurrentTasks = 2,
        int? reportedMaxConcurrentTasks = null, bool startDisconnected = false, string authToken = TestMaster.AuthToken)
    {
        var proxy = TcpFaultProxy.Start(masterGrpcPort);
        if (startDisconnected)
        {
            proxy.Sever();
        }
        var settings = new Dictionary<string, string?>
        {
            ["GrpcSettings:MasterEndpoint"] = proxy.Endpoint,
            ["GrpcSettings:NodeAuthToken"] = authToken,
            ["GrpcSettings:HeartbeatIntervalSeconds"] = "1",
            ["GrpcSettings:MaxRetryAttempts"] = "3",
            ["GrpcSettings:RetryDelayMilliseconds"] = "200",
            // 测试中缩短的连接参数：重连退避上限 1 秒，等待 Master 响应 3 秒，keepalive 2 秒/2 秒（生产默认 30、10、15/30 秒）
            ["GrpcSettings:MaxRetryDelaySeconds"] = "1",
            ["GrpcSettings:MasterResponseTimeoutSeconds"] = "3",
            ["GrpcSettings:KeepAlivePingDelaySeconds"] = "2",
            ["GrpcSettings:KeepAlivePingTimeoutSeconds"] = "2",
            ["GrpcSettings:ConnectionIdleTimeoutMinutes"] = "30",
            ["GrpcSettings:MaxMessageSizeMb"] = "64",
            ["CrawlerSettings:RequestTimeoutSeconds"] = "30",
            ["CrawlerSettings:DelayBetweenRequestsMs"] = "20",
            ["CrawlerSettings:MaxConcurrentTasks"] = maxConcurrentTasks.ToString(),
            ["CrawlerSettings:EnableProgressSimulation"] = "false",
            // 不节流进度上报，使每一页的进度都送达 Master，便于验证进度不回退
            ["SignalRSettings:TaskProgressThrottleSeconds"] = "0",
        };

        var host = Host.CreateDefaultBuilder()
            .UseDefaultServiceProvider(options =>
            {
                options.ValidateScopes = true;
                options.ValidateOnBuild = true;
            })
            .ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.Sources.Clear();
                configuration.AddInMemoryCollection(settings);
            })
            .UseSerilog()
            .ConfigureServices((context, services) =>
            {
                services.AddCrawlerWorker(context.Configuration);
                services.AddSingleton<NodeInfoCollector>();
                services.Replace(ServiceDescriptor.Singleton<INodeInfoCollector>(sp =>
                    new TestNodeInfoCollector(sp.GetRequiredService<NodeInfoCollector>(), nodeId, reportedMaxConcurrentTasks)));
                services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(5));
            })
            .Build();

        using (LogContext.PushProperty("Component", nodeId))
        {
            await host.StartAsync();
        }
        Log.Information("测试 Worker {NodeId} 已启动，经代理端口 {ProxyPort} 连接 Master 端口 {MasterPort}",
            nodeId, proxy.Port, masterGrpcPort);
        return new TestWorker(nodeId, proxy, host);
    }

    /// <summary>
    /// 优雅停止（相当于在控制台按 Ctrl+C 或 docker stop）。停止与释放都不应抛出异常（W-09、W-35），
    /// 有异常时直接抛给测试，使测试失败
    /// </summary>
    public async Task StopAsync()
    {
        if (_stopped) return;
        _stopped = true;

        using (LogContext.PushProperty("Component", NodeId))
        {
            await _host.StopAsync(TimeSpan.FromSeconds(10));
            _host.Dispose();
        }
        Log.Information("测试 Worker {NodeId} 已停止", NodeId);
    }

    /// <summary>
    /// 模拟进程崩溃：先重置到 Master 的连接（Master 立即感知断开），再停止主机。
    /// 停止过程中 Worker 发出的任何消息都到不了 Master。
    /// </summary>
    public async Task CrashAsync()
    {
        Proxy.Sever();
        await StopAsync();
    }

    /// <summary>模拟失联：连接保持但不再有数据到达 Master，只能靠健康检查超时发现</summary>
    public void Hang() => Proxy.BlackHole();

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync();
        }
        finally
        {
            await Proxy.DisposeAsync();
        }
    }
}
