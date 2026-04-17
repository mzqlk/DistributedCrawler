using CrawlerCore.Infrastructure;
using CrawlerCore.Infrastructure.Constants;
using CrawlerCore.Protos;
using CrawlerMaster.WPF.Infrastructure;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace CrawlerMaster.WPF.Services
{
    /// <summary>
    /// gRPC托管服务类，负责启动和管理gRPC服务器
    /// </summary>
    public class GrpcHostedService : IHostedService, IDisposable
    {
        private readonly ILogger<GrpcHostedService> _logger;
        private readonly GrpcSettings _grpcSettings;
        private readonly CrawlerCoordinator.CrawlerCoordinatorBase _coordinatorService;
        private readonly GrpcAuthenticationInterceptor _interceptor;
        private Server? _server;
        private bool _disposed = false;

        public GrpcHostedService(
            ILogger<GrpcHostedService> logger,
            IOptions<GrpcSettings> grpcSettingsOptions,
            CrawlerCoordinator.CrawlerCoordinatorBase coordinatorService,
            GrpcAuthenticationInterceptor interceptor)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _grpcSettings = grpcSettingsOptions.Value ?? throw new ArgumentNullException(nameof(grpcSettingsOptions));
            _coordinatorService = coordinatorService ?? throw new ArgumentNullException(nameof(coordinatorService));
            _interceptor = interceptor ?? throw new ArgumentNullException(nameof(interceptor));

            // 前置配置校验
            if (string.IsNullOrWhiteSpace(_grpcSettings.MasterEndpoint))
            {
                throw new ArgumentException("MasterEndpoint配置不能为空", nameof(grpcSettingsOptions));
            }
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("启动 gRPC 服务器...");

            var (host, port, useTls) = ParseEndpoint(_grpcSettings.MasterEndpoint);
            _logger.LogDebug("解析监听地址: Host={Host}, Port={Port}, UseTls={UseTls}", host, port, useTls);

            CheckPortAvailable(host, port);

            var maxMessageSizeBytes = _grpcSettings.MaxMessageSizeMb * 1024 * 1024;
            var idleTimeoutMs = _grpcSettings.ConnectionIdleTimeoutMinutes * 60 * 1000;

            var options = new List<ChannelOption>
            {
                new ChannelOption("grpc.max_receive_message_length", maxMessageSizeBytes),
                new ChannelOption("grpc.max_send_message_length", maxMessageSizeBytes),
                new ChannelOption("grpc.max_connection_idle_ms", idleTimeoutMs),
                new ChannelOption("grpc.default_compression_algorithm", "gzip"),
                new ChannelOption("grpc.default_compression_level", "balanced")
            };

            // 修复TLS凭证逻辑，正确适配HTTP/HTTPS
            ServerCredentials serverCredentials;
            if (useTls)
            {
                // 如需TLS加密，在此处替换为你的证书配置
                // var keyPair = new KeyCertificatePair(File.ReadAllText("server.crt"), File.ReadAllText("server.key"));
                // serverCredentials = new SslServerCredentials(new List<KeyCertificatePair> { keyPair });
                serverCredentials = ServerCredentials.Insecure;
                _logger.LogWarning("当前使用HTTPS端点但未配置TLS证书，已降级为Insecure模式，生产环境请配置正式证书");
            }
            else
            {
                serverCredentials = ServerCredentials.Insecure;
            }

            _server = new Server(options)
            {
                Ports = { new ServerPort(host, port, serverCredentials) },
                Services =
                {
                    CrawlerCoordinator.BindService(_coordinatorService)
                                      .Intercept(_interceptor)
                }
            };

            _server.Start();
            _logger.LogInformation("gRPC 服务器已启动，监听 {Host}:{Port}", host, port);
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("正在关闭 gRPC 服务器...");

            if (_server != null)
            {
                try
                {
                    using var shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    shutdownCts.CancelAfter(TimeSpan.FromSeconds(GrpcConstants.DefaultGracefulShutdownTimeoutSeconds));
                    await _server.ShutdownAsync().WaitAsync(shutdownCts.Token);
                    _logger.LogInformation("gRPC 服务器已正常关闭");
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("关闭 gRPC 服务器超时，强制停止");
                    await _server.KillAsync();
                }
                finally
                {
                    _server = null;
                }
            }
        }

        /// <summary>
        /// 修复端点解析，兼容IPv4/IPv6/域名全格式
        /// </summary>
        private (string host, int port, bool useTls) ParseEndpoint(string endpoint)
        {
            bool useTls = false;
            string trimmedEndpoint = endpoint;

            // 解析协议
            if (trimmedEndpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                trimmedEndpoint = trimmedEndpoint.Substring(8);
                useTls = true;
            }
            else if (trimmedEndpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                trimmedEndpoint = trimmedEndpoint.Substring(7);
                useTls = false;
            }

            string host;
            int port;

            // 处理IPv6地址（带方括号）
            if (trimmedEndpoint.StartsWith("[", StringComparison.Ordinal))
            {
                var closingBracketIndex = trimmedEndpoint.IndexOf(']');
                if (closingBracketIndex == -1)
                    throw new ArgumentException($"无效的IPv6地址格式: {endpoint}");

                host = trimmedEndpoint.Substring(1, closingBracketIndex - 1);
                var portPart = trimmedEndpoint.Substring(closingBracketIndex + 1);

                if (!portPart.StartsWith(":"))
                    throw new ArgumentException($"无效的端点格式，缺少端口: {endpoint}");

                if (!int.TryParse(portPart.Substring(1), out port) || port <= 0 || port > 65535)
                    throw new ArgumentException($"无效的端口号: {portPart.Substring(1)}");
            }
            // 处理IPv4/域名
            else
            {
                var parts = trimmedEndpoint.Split(':');
                if (parts.Length != 2)
                    throw new ArgumentException($"无效的端点格式: {endpoint}，应为 'scheme://host:port'");

                host = parts[0];
                if (!int.TryParse(parts[1], out port) || port <= 0 || port > 65535)
                    throw new ArgumentException($"无效的端口号: {parts[1]}");
            }

            // 兼容0.0.0.0监听地址
            if (string.IsNullOrWhiteSpace(host) || host == "0.0.0.0" || host == "[::]")
                host = IPAddress.Any.ToString();

            return (host, port, useTls);
        }

        /// <summary>
        /// 修复端口检查，兼容IPv6，避免系统不支持IPv6时启动失败
        /// </summary>
        private void CheckPortAvailable(string host, int port)
        {
            try
            {
                IPAddress[] addresses;
                if (host == IPAddress.Any.ToString() || host == IPAddress.IPv6Any.ToString())
                {
                    addresses = new[] { IPAddress.Any, IPAddress.IPv6Any };
                }
                else
                {
                    var entry = Dns.GetHostEntry(host);
                    addresses = entry.AddressList;
                }

                foreach (var ip in addresses.Distinct())
                {
                    try
                    {
                        using var listener = new TcpListener(ip, port);
                        listener.Start();
                        listener.Stop();
                        _logger.LogDebug("端口 {Port} 在地址 {Ip} 可用", port, ip);
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
                    {
                        _logger.LogError(ex, "端口 {Port} 在地址 {Ip} 已被占用", port, ip);
                        throw new InvalidOperationException($"端口 {port} 已被占用，请修改配置或释放端口", ex);
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressFamilyNotSupported)
                    {
                        _logger.LogDebug("地址 {Ip} 协议不支持，跳过", ip);
                    }
                }
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                _logger.LogWarning(ex, "端口可用性检查异常，将继续尝试启动服务");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _server?.KillAsync().Wait();
            _disposed = true;
            GC.SuppressFinalize(this);
        }

        ~GrpcHostedService()
        {
            Dispose();
        }
    }
}