using CrawlerCore.Infrastructure;
using CrawlerCore.Protos;
using Grpc.Net.Client;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using System.Net.Sockets;
using System.Net.Http;
using Grpc.Net.Compression;
using System.IO.Compression;
using System.Collections.Generic;
using GrpcChannel = Grpc.Net.Client.GrpcChannel;
using System.Net;

namespace CrawlerWorker.Communication;

public class GrpcClientManager : IGrpcClientManager, IDisposable
{
    private readonly GrpcSettings _grpcSettings;
    private readonly ILogger<GrpcClientManager> _logger;
    private GrpcChannel? _channel;
    private CrawlerCoordinator.CrawlerCoordinatorClient? _coordinatorClient;
    private readonly object _lockObj = new();
    private volatile bool _disposed = false;
    private volatile bool _isShutdown = false;

    public CrawlerCoordinator.CrawlerCoordinatorClient CoordinatorClient
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(GrpcClientManager));
            if (_isShutdown) throw new InvalidOperationException("gRPC通道已关闭，无法创建客户端");

            // 双重校验锁，线程安全懒加载
            if (_coordinatorClient == null)
            {
                lock (_lockObj)
                {
                    _coordinatorClient ??= CreateCoordinatorClient();
                }
            }
            return _coordinatorClient;
        }
    }

    public GrpcClientManager(IOptions<GrpcSettings> grpcSettings, ILogger<GrpcClientManager> logger)
    {
        _grpcSettings = grpcSettings.Value;
        _logger = logger;
    }

    /// <summary>
    /// 创建gRPC通道，与Master端配置100%完全对齐
    /// </summary>
    private CrawlerCoordinator.CrawlerCoordinatorClient CreateCoordinatorClient()
    {
        _logger.LogInformation("初始化gRPC通道，Master端点: {Endpoint}", _grpcSettings.MasterEndpoint);

        var httpHandler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(_grpcSettings.ConnectionIdleTimeoutMinutes),
            ConnectTimeout = TimeSpan.FromSeconds(5)
        };

        var httpClient = new HttpClient(httpHandler)
        {
            DefaultRequestVersion = HttpVersion.Version20,                    // 优先使用 HTTP/2
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher  // 允许协商更高版本（如 HTTP/3）
        };

        var channelOptions = new GrpcChannelOptions
        {
            HttpClient = httpClient,
            MaxReceiveMessageSize = _grpcSettings.MaxMessageSizeMb * 1024 * 1024,
            MaxSendMessageSize = _grpcSettings.MaxMessageSizeMb * 1024 * 1024,
            CompressionProviders = new List<ICompressionProvider>
        {
            new GzipCompressionProvider(CompressionLevel.Fastest)
        },
            ThrowOperationCanceledOnCancellation = true,
            UnsafeUseInsecureChannelCallCredentials = true
        };

        _channel = GrpcChannel.ForAddress(_grpcSettings.MasterEndpoint, channelOptions);
        return new CrawlerCoordinator.CrawlerCoordinatorClient(_channel);
    }

    /// <summary>
    /// Master端口连通性预检测，全链路传递取消令牌
    /// </summary>
    public async Task<bool> CheckMasterConnectivityAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var endpoint = new Uri(_grpcSettings.MasterEndpoint);
            var host = endpoint.Host;
            var port = endpoint.Port;

            using var tcpClient = new TcpClient();
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            await tcpClient.ConnectAsync(host, port, linkedCts.Token).ConfigureAwait(false);
            _logger.LogDebug("Master端点连通性检测通过: {Host}:{Port}", host, port);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Master端点连通性检测失败: {Endpoint}", _grpcSettings.MasterEndpoint);
            return false;
        }
    }

    /// <summary>
    /// 优雅关闭通道，幂等设计，避免重复关闭异常
    /// </summary>
    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        lock (_lockObj)
        {
            if (_isShutdown || _channel == null) return;
            _isShutdown = true;
        }

        _logger.LogInformation("开始关闭gRPC通道");
        await _channel.ShutdownAsync().ConfigureAwait(false);
        _logger.LogInformation("gRPC通道已关闭");
    }

    /// <summary>
    /// 完整IDisposable模式，多线程安全，终结器兜底
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            // 释放托管资源
            _channel?.Dispose();
        }

        _disposed = true;
        _isShutdown = true;
    }

    ~GrpcClientManager()
    {
        Dispose(false);
    }
}