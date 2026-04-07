using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CrawlerMaster.WPF.Infrastructure
{
    /// <summary>
    /// gRPC 认证拦截器，实现 Token 校验、结构化日志记录以及异常统一转换
    /// </summary>
    public class GrpcAuthenticationInterceptor : Interceptor
    {
        private readonly string _expectedToken;
        private readonly ILogger<GrpcAuthenticationInterceptor> _logger;

        public GrpcAuthenticationInterceptor(IConfiguration configuration, ILogger<GrpcAuthenticationInterceptor> logger)
        {
            // 从配置中读取预期的 Token，默认空字符串（若未配置则所有请求将因 Token 不匹配而失败）
            _expectedToken = configuration["GrpcSettings:NodeAuthToken"] ?? string.Empty;
            _logger = logger;
        }

        /// <summary>
        /// 一元服务端方法拦截（非流式 RPC）
        /// </summary>
        public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
            TRequest request,
            ServerCallContext context,
            UnaryServerMethod<TRequest, TResponse> continuation)
        {
            // 1. 认证校验
            Authenticate(context);

            // 2. 记录请求开始
            var method = context.Method;
            var peer = context.Peer;
            _logger.LogInformation("一元请求开始: {Method}, 客户端: {Peer}", method, peer);

            var stopwatch = Stopwatch.StartNew();
            try
            {
                // 3. 执行实际处理逻辑
                var response = await continuation(request, context);
                stopwatch.Stop();
                _logger.LogInformation("一元请求结束: {Method}, 耗时 {ElapsedMs} ms", method, stopwatch.ElapsedMilliseconds);
                return response;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                // 4. 异常转换为 RpcException
                _logger.LogError(ex, "一元请求失败: {Method}, 耗时 {ElapsedMs} ms", method, stopwatch.ElapsedMilliseconds);
                throw new RpcException(new Status(StatusCode.Internal, $"Internal server error: {ex.Message}"));
            }
        }

        /// <summary>
        /// 双向流式服务端方法拦截
        /// </summary>
        public override async Task DuplexStreamingServerHandler<TRequest, TResponse>(
            IAsyncStreamReader<TRequest> requestStream,
            IServerStreamWriter<TResponse> responseStream,
            ServerCallContext context,
            DuplexStreamingServerMethod<TRequest, TResponse> continuation)
        {
            // 1. 认证校验
            Authenticate(context);

            // 2. 记录流开始
            var method = context.Method;
            var peer = context.Peer;
            _logger.LogInformation("双向流请求开始: {Method}, 客户端: {Peer}", method, peer);

            var stopwatch = Stopwatch.StartNew();
            try
            {
                // 3. 执行实际流处理逻辑
                await continuation(requestStream, responseStream, context);
                stopwatch.Stop();
                _logger.LogInformation("双向流请求结束: {Method}, 耗时 {ElapsedMs} ms", method, stopwatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                // 4. 异常转换为 RpcException
                _logger.LogError(ex, "双向流请求失败: {Method}, 耗时 {ElapsedMs} ms", method, stopwatch.ElapsedMilliseconds);
                throw new RpcException(new Status(StatusCode.Internal, $"Internal server error: {ex.Message}"));
            }
        }

        /// <summary>
        /// 认证核心逻辑：从请求头提取 Bearer Token 并与配置值比较
        /// </summary>
        private void Authenticate(ServerCallContext context)
        {
            // 从元数据中获取 Authorization 头
            var authHeader = context.RequestHeaders.FirstOrDefault(m => m.Key == "Authorization")?.Value;
            if (string.IsNullOrEmpty(authHeader))
            {
                _logger.LogWarning("缺少 Authorization 头，客户端: {Peer}", context.Peer);
                throw new RpcException(new Status(StatusCode.Unauthenticated, "Missing authorization token"));
            }

            // 检查 Bearer 格式
            const string bearerPrefix = "Bearer ";
            if (!authHeader.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Authorization 头格式错误，应为 Bearer token，客户端: {Peer}", context.Peer);
                throw new RpcException(new Status(StatusCode.Unauthenticated, "Invalid authorization header format"));
            }

            var token = authHeader.Substring(bearerPrefix.Length);
            if (string.IsNullOrEmpty(token) || token != _expectedToken)
            {
                _logger.LogWarning("Token 无效或未匹配，客户端: {Peer}", context.Peer);
                throw new RpcException(new Status(StatusCode.Unauthenticated, "Invalid token"));
            }
        }
    }
}