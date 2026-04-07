using CrawlerCore.Infrastructure;
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
    public class GrpcHostedService : IHostedService
    {
        private readonly ILogger<GrpcHostedService> _logger;
        private readonly GrpcSettings _grpcSettings;
        private readonly CrawlerCoordinator.CrawlerCoordinatorBase _coordinatorService;
        private readonly GrpcAuthenticationInterceptor _interceptor;
        private Server? _server;
        private CancellationTokenSource? _cts;   // 用于取消流式调用

        public GrpcHostedService(
            ILogger<GrpcHostedService> logger,
            IOptions<GrpcSettings> grpcSettingsOptions,
            CrawlerCoordinator.CrawlerCoordinatorBase coordinatorService,
            GrpcAuthenticationInterceptor interceptor)
        {
            _logger = logger;
            _grpcSettings = grpcSettingsOptions.Value;
            _coordinatorService = coordinatorService;
            _interceptor = interceptor;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("启动 gRPC 服务器...");

            // 创建 CancellationTokenSource（供后续流式方法使用）
            _cts = new CancellationTokenSource();

            var (host, port) = ParseEndpoint(_grpcSettings.MasterEndpoint);
            _logger.LogDebug("解析地址: Host={Host}, Port={Port}", host, port);

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

            _server = new Server(options)
            {
                Ports = { new ServerPort(host, port, ServerCredentials.Insecure) },
                Services =
                {
                    CrawlerCoordinator.BindService(_coordinatorService)
                                      .Intercept(_interceptor)
                }
            };

            _server.Start();
            _logger.LogInformation("gRPC 服务器已启动，监听 {Host}:{Port}", host, port);

            // 若有流式方法，可将 _cts.Token 传入，例如：
            // _coordinatorService.StartStreaming(_cts.Token);

            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("正在关闭 gRPC 服务器...");

            // 1. 取消所有流式调用
            if (_cts != null)
            {
                _logger.LogDebug("取消流式调用 CancellationTokenSource");
                _cts.Cancel();
            }

            // 2. 优雅关闭 gRPC 服务器，最多等待 5 秒
            if (_server != null)
            {
                try
                {
                    // 创建链接源：同时响应外部取消和 5 秒超时
                    using var shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    shutdownCts.CancelAfter(TimeSpan.FromSeconds(5));
                    await _server.ShutdownAsync().WaitAsync(shutdownCts.Token);
                    _logger.LogInformation("gRPC 服务器已正常关闭");
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("关闭 gRPC 服务器超时（5秒），强制停止");
                }
                finally
                {
                    _server = null;
                }
            }

            // 3. 释放 CancellationTokenSource
            _cts?.Dispose();
            _cts = null;
        }

        private (string host, int port) ParseEndpoint(string endpoint)
        {
            var trimmed = endpoint;
            if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed.Substring(8);
            else if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed.Substring(7);

            var parts = trimmed.Split(':');
            if (parts.Length != 2)
                throw new ArgumentException($"无效的 MasterEndpoint 格式: {endpoint}，应为 'host:port'");

            var host = parts[0];
            if (string.IsNullOrWhiteSpace(host))
                host = "0.0.0.0";

            if (!int.TryParse(parts[1], out var port) || port <= 0 || port > 65535)
                throw new ArgumentException($"无效的端口号: {parts[1]}");

            return (host, port);
        }

        private void CheckPortAvailable(string host, int port)
        {
            try
            {
                var addressesToCheck = new List<IPAddress>();
                if (host == "localhost")
                {
                    addressesToCheck.Add(IPAddress.Loopback);
                    addressesToCheck.Add(IPAddress.IPv6Loopback);
                }
                else if (host == "0.0.0.0" || host == "*")
                {
                    addressesToCheck.Add(IPAddress.Any);
                }
                else
                {
                    var entry = Dns.GetHostEntry(host);
                    addressesToCheck.AddRange(entry.AddressList);
                }
                foreach (var ip in addressesToCheck.Distinct())
                {
                    using var listener = new TcpListener(ip, port);
                    listener.Start();
                    listener.Stop();
                    _logger.LogDebug("端口 {Port} 可用", port);
                }
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                _logger.LogError(ex, "端口 {Port} 已被占用", port);
                throw new InvalidOperationException($"端口 {port} 已被占用，请修改配置或释放端口", ex);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "端口可用性检查异常，将继续尝试启动服务");
            }
        }
    }
}