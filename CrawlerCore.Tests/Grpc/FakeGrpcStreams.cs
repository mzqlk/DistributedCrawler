using System.Collections.Concurrent;
using Grpc.Core;

namespace CrawlerCore.Tests.Grpc;

/// <summary>
/// 单元测试用的服务端调用上下文：可指定请求头与取消令牌
/// </summary>
public sealed class TestServerCallContext : ServerCallContext
{
    private readonly Metadata _requestHeaders;
    private readonly CancellationToken _cancellationToken;

    public TestServerCallContext(Metadata? requestHeaders = null, CancellationToken cancellationToken = default, string method = "/CrawlerSystem.CrawlerCoordinator/StartStreaming")
    {
        _requestHeaders = requestHeaders ?? new Metadata();
        _cancellationToken = cancellationToken;
        MethodCore = method;
    }

    protected override string MethodCore { get; }
    protected override string HostCore => "localhost";
    protected override string PeerCore => "ipv4:127.0.0.1:12345";
    protected override DateTime DeadlineCore => DateTime.MaxValue;
    protected override Metadata RequestHeadersCore => _requestHeaders;
    protected override CancellationToken CancellationTokenCore => _cancellationToken;
    protected override Metadata ResponseTrailersCore { get; } = new();
    protected override Status StatusCore { get; set; }
    protected override WriteOptions? WriteOptionsCore { get; set; }
    protected override AuthContext AuthContextCore { get; } = new(null, new Dictionary<string, List<AuthProperty>>());

    protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options)
        => throw new NotSupportedException();

    protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
}

/// <summary>
/// 假的请求流：测试写入消息，或让读取以异常结束（模拟连接断开、消息超限）
/// </summary>
public sealed class FakeRequestStream<T> : IAsyncStreamReader<T> where T : class
{
    private readonly System.Threading.Channels.Channel<T> _channel = System.Threading.Channels.Channel.CreateUnbounded<T>();
    private Exception? _fault;

    public T Current { get; private set; } = null!;

    public async Task<bool> MoveNext(CancellationToken cancellationToken)
    {
        if (await _channel.Reader.WaitToReadAsync(cancellationToken) && _channel.Reader.TryRead(out var message))
        {
            Current = message;
            return true;
        }
        if (_fault != null)
        {
            throw _fault;
        }
        return false;
    }

    public void Write(T message) => _channel.Writer.TryWrite(message);

    /// <summary>结束请求流；指定异常时，读取方在读完已有消息后收到该异常</summary>
    public void Complete(Exception? fault = null)
    {
        _fault = fault;
        _channel.Writer.TryComplete();
    }
}

/// <summary>
/// 假的响应流：记录服务端写出的消息
/// </summary>
public sealed class FakeResponseStream<T> : IServerStreamWriter<T>
{
    private readonly ConcurrentQueue<T> _written = new();

    public WriteOptions? WriteOptions { get; set; }

    public IReadOnlyList<T> Written => _written.ToList();

    public Task WriteAsync(T message)
    {
        _written.Enqueue(message);
        return Task.CompletedTask;
    }
}
