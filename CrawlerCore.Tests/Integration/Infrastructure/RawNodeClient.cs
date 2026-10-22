using System.Collections.Concurrent;
using CrawlerCore.Protos;
using Grpc.Core;
using Grpc.Net.Client;

namespace CrawlerCore.Tests.Integration.Infrastructure;

/// <summary>
/// 直接调用 Master gRPC 接口的“假节点”：由测试逐条决定发送什么（注册参数、心跳、进度、结果），
/// 并记录 Master 下发的全部消息。用于验证只与 Master 有关的行为（认证、隔离旧节点、并发上限、结果回执），
/// 不依赖 Worker 的实现。
/// </summary>
public sealed class RawNodeClient : IAsyncDisposable
{
    private readonly GrpcChannel _channel;
    private readonly string? _token;
    private readonly ConcurrentQueue<FromMaster> _received = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private AsyncDuplexStreamingCall<FromWorker, FromMaster>? _call;
    private Task? _readLoop;
    private bool _disposed;

    private RawNodeClient(int masterPort, string nodeId, string? token)
    {
        NodeId = nodeId;
        _token = token;
        _channel = GrpcChannel.ForAddress($"http://127.0.0.1:{masterPort}");
        Client = new CrawlerCoordinator.CrawlerCoordinatorClient(_channel);
    }

    public string NodeId { get; }

    public string? SessionId { get; private set; }

    public CrawlerCoordinator.CrawlerCoordinatorClient Client { get; }

    /// <summary>Master 下发的消息（按到达顺序）</summary>
    public IReadOnlyList<FromMaster> Received => _received.ToList();

    /// <summary>流的结束原因：正常结束为 null，异常结束为对应的 RpcException 等</summary>
    public Exception? StreamError { get; private set; }

    /// <summary>读循环是否已结束（Master 关闭了流，或连接断开）</summary>
    public bool StreamEnded => _readLoop is { IsCompleted: true };

    /// <param name="masterPort">Master gRPC 端口</param>
    /// <param name="nodeId">节点ID</param>
    /// <param name="token">节点令牌；为 null 时不带 authorization 头</param>
    public static RawNodeClient Create(int masterPort, string nodeId, string? token = TestMaster.AuthToken)
        => new(masterPort, nodeId, token);

    public Metadata AuthHeaders()
    {
        var headers = new Metadata();
        if (_token != null)
        {
            headers.Add("authorization", $"Bearer {_token}");
        }
        return headers;
    }

    /// <summary>注册，成功时保存会话ID</summary>
    public async Task<RegistrationResponse> RegisterAsync(int maxConcurrentTasks = 0)
    {
        var response = await Client.RegisterNodeAsync(new NodeRegistration
        {
            NodeId = NodeId,
            MachineName = $"raw-{NodeId}",
            CpuCores = 4,
            TotalMemoryGb = 64,
            IpAddress = "127.0.0.1",
            MaxConcurrentTasks = maxConcurrentTasks
        }, AuthHeaders());
        if (response.Success)
        {
            SessionId = response.SessionId;
        }
        return response;
    }

    /// <summary>用当前会话建立双向流，并在后台读取 Master 下发的消息</summary>
    public void StartStream(string? sessionId = null)
    {
        var headers = AuthHeaders();
        headers.Add("node-id", NodeId);
        headers.Add("session-id", sessionId ?? SessionId ?? string.Empty);
        _call = Client.StartStreaming(headers);
        var call = _call;
        _readLoop = Task.Run(async () =>
        {
            try
            {
                await foreach (var message in call.ResponseStream.ReadAllAsync())
                {
                    _received.Enqueue(message);
                }
            }
            catch (Exception ex)
            {
                StreamError = ex;
            }
        });
    }

    /// <summary>等待读循环结束（流被 Master 关闭或因错误结束）</summary>
    public Task WaitForStreamEndAsync(string description)
        => Poll.UntilAsync(() => StreamEnded, description);

    public async Task SendAsync(FromWorker message)
    {
        await _writeLock.WaitAsync();
        try
        {
            await _call!.RequestStream.WriteAsync(message);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>发送心跳（负载取测试 Worker 的固定值，<see cref="TestMaster.WaitForNodeOnlineAsync"/> 据此确认心跳已落库）</summary>
    public Task SendHeartbeatAsync(int activeTasks = 0) => SendAsync(new FromWorker
    {
        Heartbeat = new Heartbeat
        {
            NodeId = NodeId,
            CpuUsage = TestNodeInfoCollector.FixedCpuUsage,
            MemoryUsageMb = TestNodeInfoCollector.FixedMemoryUsageMb,
            ActiveTasks = activeTasks,
            Timestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow)
        }
    });

    public Task SendProgressAsync(string taskId, CrawlerCore.Protos.TaskStatus status, int crawledPages = 0, string errorMessage = "")
        => SendAsync(new FromWorker
        {
            TaskProgress = new TaskProgress
            {
                TaskId = taskId,
                CrawledPages = crawledPages,
                TotalPages = 10,
                CurrentStatus = status,
                AssignedNodeId = NodeId,
                ErrorMessage = errorMessage
            }
        });

    /// <summary>发送一份结果（单页，URL 带上节点ID，便于区分来源）</summary>
    public Task SendResultAsync(string taskId)
    {
        var result = new CrawlerCore.Protos.CrawlResult
        {
            TaskId = taskId,
            CompletedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow)
        };
        result.PageResults.Add(new CrawlerCore.Protos.PageResult
        {
            Url = $"http://example.test/{NodeId}",
            Finalurl = $"http://example.test/{NodeId}",
            StatusCode = 200
        });
        return SendAsync(new FromWorker { CrawlResult = result });
    }

    /// <summary>等待一条满足条件的下发消息</summary>
    public Task<FromMaster> WaitForMessageAsync(Func<FromMaster, bool> predicate, string description, TimeSpan? timeout = null)
        => Poll.ValueAsync(() => Task.FromResult(_received.FirstOrDefault(predicate)), description, timeout);

    /// <summary>收到的新任务ID</summary>
    public IReadOnlyList<string> ReceivedTaskIds
        => _received.Where(m => m.MessageCase == FromMaster.MessageOneofCase.NewTask).Select(m => m.NewTask.TaskId).ToList();

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_call != null)
        {
            try
            {
                await _call.RequestStream.CompleteAsync();
            }
            catch
            {
                // 流可能已被 Master 关闭
            }
            _call.Dispose();
        }
        if (_readLoop != null)
        {
            await Task.WhenAny(_readLoop, Task.Delay(TimeSpan.FromSeconds(5)));
        }
        _channel.Dispose();
        _writeLock.Dispose();
    }
}
