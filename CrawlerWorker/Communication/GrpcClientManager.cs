using CrawlerCore.Infrastructure;
using CrawlerCore.Protos;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using Grpc.Net.Compression;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace CrawlerWorker.Communication
{
    public sealed class GrpcClientManager : IGrpcClientManager, IDisposable
    {
        private readonly GrpcSettings _grpcSettings;
        private readonly ILogger<GrpcClientManager> _logger;
        private readonly GrpcClientAuthenticationInterceptor _authInterceptor;
        private readonly object _lockObj = new();

        private volatile GrpcChannel? _channel;
        private volatile CrawlerCoordinator.CrawlerCoordinatorClient? _client;
        private volatile bool _disposed;
        private volatile bool _isShuttingDown;

        public GrpcClientManager(
            IOptions<GrpcSettings> grpcSettingsOptions,
            ILogger<GrpcClientManager> logger,
            GrpcClientAuthenticationInterceptor authInterceptor)
        {
            _grpcSettings = grpcSettingsOptions?.Value ?? throw new ArgumentNullException(nameof(grpcSettingsOptions));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _authInterceptor = authInterceptor ?? throw new ArgumentNullException(nameof(authInterceptor));

            if (string.IsNullOrWhiteSpace(_grpcSettings.MasterEndpoint))
                throw new ArgumentException("MasterEndpoint 配置不能为空", nameof(grpcSettingsOptions));

            _logger.LogInformation("GrpcClientManager 初始化完成，MasterEndpoint: {Endpoint}", _grpcSettings.MasterEndpoint);
        }

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

        public bool IsChannelHealthy()
        {
            var channel = _channel;
            return channel != null && channel.State == ConnectivityState.Ready;
        }

        public void RecreateChannel()
        {
            lock (_lockObj)
            {
                ThrowIfDisposedOrShutdown();

                _logger.LogInformation("正在重建 gRPC 通道，旧通道将被释放");

                _channel?.Dispose();
                _channel = null;
                _client = null;

                CreateClientInternal();
            }
        }

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
                UnsafeUseInsecureChannelCallCredentials = IsInsecureEndpoint()
            };

            _channel = GrpcChannel.ForAddress(_grpcSettings.MasterEndpoint, channelOptions);

            var callInvoker = _channel.Intercept(_authInterceptor);
            _client = new CrawlerCoordinator.CrawlerCoordinatorClient(callInvoker);

            _logger.LogInformation("gRPC 通道创建完成，已配置认证拦截器");
            return _client;
        }

        private SocketsHttpHandler CreateHttpHandler()
        {
            var handler = new SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = true,
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(_grpcSettings.ConnectionIdleTimeoutMinutes),
                ConnectTimeout = TimeSpan.FromSeconds(10),
                InitialHttp2StreamWindowSize = 65535,
            };

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

                // 兼容 0.0.0.0，转换为 localhost 用于 TCP 连接测试
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