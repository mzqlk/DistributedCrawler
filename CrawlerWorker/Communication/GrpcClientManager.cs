using CrawlerCore.Infrastructure;
using CrawlerCore.Protos;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.Compression;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace CrawlerWorker.Communication
{
    /// <summary>
    /// 线程安全的 gRPC 客户端管理器，支持通道健康检查、优雅关闭、IPv4/IPv6 全格式。
    /// 融合了配置灵活性、可观测性与健壮性。
    /// </summary>
    public sealed class GrpcClientManager : IGrpcClientManager, IDisposable
    {
        private readonly GrpcSettings _grpcSettings;
        private readonly ILogger<GrpcClientManager> _logger;
        private readonly object _lockObj = new();

        // 使用 volatile 保证多线程可见性，配合双重检查锁实现懒加载与安全重建
        private volatile GrpcChannel? _channel;
        private volatile CrawlerCoordinator.CrawlerCoordinatorClient? _client;
        private volatile bool _disposed;
        private volatile bool _isShuttingDown;

        public GrpcClientManager(
            IOptions<GrpcSettings> grpcSettingsOptions,
            ILogger<GrpcClientManager> logger)
        {
            _grpcSettings = grpcSettingsOptions?.Value ?? throw new ArgumentNullException(nameof(grpcSettingsOptions));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            if (string.IsNullOrWhiteSpace(_grpcSettings.MasterEndpoint))
                throw new ArgumentException("MasterEndpoint 配置不能为空", nameof(grpcSettingsOptions));
        }

        /// <summary>
        /// 获取 gRPC 客户端实例。线程安全，首次访问时自动创建通道。
        /// </summary>
        public CrawlerCoordinator.CrawlerCoordinatorClient CoordinatorClient
        {
            get
            {
                ThrowIfDisposedOrShutdown();

                if (_client == null)
                {
                    lock (_lockObj)
                    {
                        _client ??= CreateClientInternal();
                    }
                }
                return _client;
            }
        }

        /// <summary>
        /// 检查当前通道是否处于就绪状态。
        /// </summary>
        public bool IsChannelHealthy()
        {
            var channel = _channel;
            return channel != null && channel.State == ConnectivityState.Ready;
        }

        /// <summary>
        /// 强制重建 gRPC 通道（例如在检测到连接断开后）。
        /// </summary>
        public void RecreateChannel()
        {
            lock (_lockObj)
            {
                ThrowIfDisposedOrShutdown();

                _logger.LogInformation("正在重建 gRPC 通道，旧通道将被释放");

                // 释放旧资源
                _channel?.Dispose();
                _channel = null;
                _client = null;

                // 立即创建新通道
                CreateClientInternal();
            }
        }

        /// <summary>
        /// 通过 TCP 连接测试 Master 端点连通性，可用于启动前的健康预检。
        /// </summary>
        public async Task<bool> CheckMasterConnectivityAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                if (!TryParseEndpoint(out var host, out var port))
                    return false;

                _logger.LogDebug("检查 Master 端点 TCP 连通性: {Host}:{Port}", host, port);

                using var tcpClient = new TcpClient();
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                await tcpClient.ConnectAsync(host, port, linkedCts.Token).ConfigureAwait(false);
                _logger.LogDebug("Master 端点连通性正常");
                return true;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Master 端点连通性检查超时: {Endpoint}", _grpcSettings.MasterEndpoint);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Master 端点连通性检查失败: {Endpoint}", _grpcSettings.MasterEndpoint);
                return false;
            }
        }

        /// <summary>
        /// 优雅关闭 gRPC 通道，支持超时控制。
        /// </summary>
        public async Task ShutdownAsync(CancellationToken cancellationToken = default)
        {
            GrpcChannel? channelToShutdown = null;

            lock (_lockObj)
            {
                if (_isShuttingDown || _channel == null)
                    return;

                _isShuttingDown = true;
                channelToShutdown = _channel;
            }

            _logger.LogInformation("开始优雅关闭 gRPC 通道");

            try
            {
                // 设置关闭超时，避免无限等待
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                await channelToShutdown.ShutdownAsync().WaitAsync(linkedCts.Token).ConfigureAwait(false);
                _logger.LogInformation("gRPC 通道已正常关闭");
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("gRPC 通道关闭超时，将强制释放");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "gRPC 通道关闭过程中发生异常");
            }
            finally
            {
                lock (_lockObj)
                {
                    channelToShutdown?.Dispose();
                    _channel = null;
                    _client = null;
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            lock (_lockObj)
            {
                if (_disposed)
                    return;

                _channel?.Dispose();
                _channel = null;
                _client = null;
                _disposed = true;
            }

            GC.SuppressFinalize(this);
        }

        // --------------------------------------------------------------
        // 私有方法
        // --------------------------------------------------------------

        private CrawlerCoordinator.CrawlerCoordinatorClient CreateClientInternal()
        {
            _logger.LogInformation("初始化 gRPC 通道，Master 端点: {Endpoint}", _grpcSettings.MasterEndpoint);

            var httpHandler = CreateHttpHandler();
            var httpClient = new HttpClient(httpHandler)
            {
                DefaultRequestVersion = HttpVersion.Version20,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
            };

            var channelOptions = new GrpcChannelOptions
            {
                HttpClient = httpClient,
                MaxReceiveMessageSize = _grpcSettings.MaxMessageSizeMb * 1024 * 1024,
                MaxSendMessageSize = _grpcSettings.MaxMessageSizeMb * 1024 * 1024,
                CompressionProviders = new List<ICompressionProvider>
                {
                    new GzipCompressionProvider(System.IO.Compression.CompressionLevel.Fastest)
                },
                ThrowOperationCanceledOnCancellation = true,
                // 根据协议自动决定是否跳过证书验证
                UnsafeUseInsecureChannelCallCredentials = IsInsecureEndpoint()
            };

            _channel = GrpcChannel.ForAddress(_grpcSettings.MasterEndpoint, channelOptions);
            _client = new CrawlerCoordinator.CrawlerCoordinatorClient(_channel);

            _logger.LogDebug("gRPC 通道创建完成");
            return _client;
        }

        private SocketsHttpHandler CreateHttpHandler()
        {
            var handler = new SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = true,
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(_grpcSettings.ConnectionIdleTimeoutMinutes),
                ConnectTimeout = TimeSpan.FromSeconds(5),
                // 优化 HTTP/2 流控窗口
                InitialHttp2StreamWindowSize = 65535,
            };

            //// 仅对 http 端点跳过证书验证（保持生产环境对 https 的安全校验）
            //if (IsInsecureEndpoint())
            //{
            //    // 修正：必须实例化 SslOptions 才能设置回调
            //    handler.SslOptions = new SslClientAuthenticationOptions
            //    {
            //        RemoteCertificateValidationCallback = (_, _, _, _) => true
            //    };
            //}

            return handler;
        }

        private bool IsInsecureEndpoint()
        {
            return _grpcSettings.MasterEndpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
        }

        private bool TryParseEndpoint(out string host, out int port)
        {
            host = string.Empty;
            port = 0;

            try
            {
                var uri = new Uri(_grpcSettings.MasterEndpoint);
                host = uri.Host;
                port = uri.Port;

                // 兼容绑定在 0.0.0.0 或 [::] 的情况，TCP 连接时转换为 localhost
                if (host == "0.0.0.0" || host == "[::]")
                    host = "localhost";

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "解析 MasterEndpoint 失败: {Endpoint}", _grpcSettings.MasterEndpoint);
                return false;
            }
        }

        private void ThrowIfDisposedOrShutdown()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(GrpcClientManager));
            if (_isShuttingDown)
                throw new InvalidOperationException("gRPC 通道正在关闭或已关闭，无法执行操作");
        }
    }
}