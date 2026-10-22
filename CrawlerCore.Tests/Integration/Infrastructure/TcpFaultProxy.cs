using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace CrawlerCore.Tests.Integration.Infrastructure;

/// <summary>
/// 本地 TCP 转发代理：Worker 通过它连接 Master，测试借此注入网络故障。
/// <list type="bullet">
/// <item><see cref="Sever"/>：用 RST 立即断开所有连接，此后拒绝新连接（模拟进程被杀，对端立即感知断开）。</item>
/// <item><see cref="BlackHole"/>：连接保持不断，但双向数据全部丢弃（模拟网络分区或进程假死，对端只能靠心跳超时发现）。</item>
/// <item><see cref="Restore"/>：恢复正常转发，可同时切换上游端口（模拟 Master 重启后监听新端口）。</item>
/// </list>
/// </summary>
public sealed class TcpFaultProxy : IAsyncDisposable
{
    private enum ProxyMode { Forward, BlackHole, Severed }

    private readonly Socket _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<Guid, ProxyConnection> _connections = new();
    private readonly Task _acceptLoop;
    private volatile int _upstreamPort;
    private volatile ProxyMode _mode = ProxyMode.Forward;
    private int _totalConnections;

    private TcpFaultProxy(int upstreamPort)
    {
        _upstreamPort = upstreamPort;
        _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _listener.Listen(64);
        Port = ((IPEndPoint)_listener.LocalEndPoint!).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>代理监听端口（仅环回地址）</summary>
    public int Port { get; }

    /// <summary>供 Worker 使用的 gRPC 地址</summary>
    public string Endpoint => $"http://127.0.0.1:{Port}";

    /// <summary>当前仍保持的连接数</summary>
    public int ActiveConnectionCount => _connections.Count;

    /// <summary>累计接受的客户端连接数（W-12：Worker 放弃半开连接后会建立新连接）</summary>
    public int TotalConnectionCount => Volatile.Read(ref _totalConnections);

    public static TcpFaultProxy Start(int upstreamPort) => new(upstreamPort);

    /// <summary>立即重置所有连接，并拒绝之后的新连接</summary>
    public void Sever()
    {
        _mode = ProxyMode.Severed;
        foreach (var connection in _connections.Values)
        {
            connection.Abort();
        }
    }

    /// <summary>保持连接但丢弃双向数据；新建的连接同样被吞掉</summary>
    public void BlackHole() => _mode = ProxyMode.BlackHole;

    /// <summary>恢复正常转发；指定 <paramref name="upstreamPort"/> 时同时切换上游端口（只影响新连接）</summary>
    public void Restore(int? upstreamPort = null)
    {
        if (upstreamPort.HasValue)
        {
            _upstreamPort = upstreamPort.Value;
        }
        _mode = ProxyMode.Forward;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _listener.AcceptAsync(_cts.Token).ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            _ = Task.Run(() => HandleClientAsync(client));
        }
    }

    private async Task HandleClientAsync(Socket client)
    {
        client.NoDelay = true;
        Interlocked.Increment(ref _totalConnections);
        if (_mode == ProxyMode.Severed)
        {
            ProxyConnection.Reset(client);
            return;
        }

        var upstream = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await upstream.ConnectAsync(new IPEndPoint(IPAddress.Loopback, _upstreamPort), _cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // 上游不可达（例如 Master 尚未启动）：直接重置客户端连接
            ProxyConnection.Reset(client);
            ProxyConnection.Reset(upstream);
            return;
        }

        var connection = new ProxyConnection(client, upstream);
        var id = Guid.NewGuid();
        _connections[id] = connection;
        // 登记之后再检查一次：检查模式与登记之间若调用了 Sever()，这条连接不会被它重置（W-35 ③）
        if (_mode == ProxyMode.Severed)
        {
            _connections.TryRemove(id, out _);
            connection.Abort();
            return;
        }
        try
        {
            await Task.WhenAll(
                PumpAsync(client, upstream, connection),
                PumpAsync(upstream, client, connection)).ConfigureAwait(false);
        }
        finally
        {
            _connections.TryRemove(id, out _);
            connection.Abort();
        }
    }

    private async Task PumpAsync(Socket from, Socket to, ProxyConnection connection)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                var read = await from.ReceiveAsync(buffer, SocketFlags.None, _cts.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    // 对端正常关闭：转发模式下把 FIN 传给另一端；黑洞模式下什么也不传
                    if (_mode == ProxyMode.Forward)
                    {
                        try { to.Shutdown(SocketShutdown.Send); } catch { }
                    }
                    return;
                }

                if (_mode == ProxyMode.BlackHole)
                {
                    continue;
                }

                var offset = 0;
                while (offset < read)
                {
                    offset += await to.SendAsync(buffer.AsMemory(offset, read - offset), SocketFlags.None, _cts.Token).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            if (_mode == ProxyMode.BlackHole && !_cts.IsCancellationRequested)
            {
                // 黑洞模式下一端出错不传播给另一端（真实的网络分区里对端同样无从得知）
                ProxyConnection.Reset(from);
                return;
            }
            connection.Abort();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Close(); } catch { }
        foreach (var connection in _connections.Values)
        {
            connection.Abort();
        }
        try { await _acceptLoop.ConfigureAwait(false); } catch { }
        _cts.Dispose();
    }

    /// <summary>一对被转发的连接</summary>
    private sealed class ProxyConnection(Socket client, Socket upstream)
    {
        private int _aborted;

        public void Abort()
        {
            if (Interlocked.Exchange(ref _aborted, 1) != 0) return;
            Reset(client);
            Reset(upstream);
        }

        /// <summary>以 linger=0 关闭套接字，对端收到 RST</summary>
        public static void Reset(Socket socket)
        {
            try { socket.LingerState = new LingerOption(true, 0); } catch { }
            try { socket.Close(); } catch { }
        }
    }
}
