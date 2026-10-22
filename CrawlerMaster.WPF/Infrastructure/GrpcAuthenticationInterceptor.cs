using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using CrawlerCore.Infrastructure;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CrawlerMaster.WPF.Infrastructure
{
    /// <summary>
    /// gRPC 认证拦截器：校验节点令牌（Authorization: Bearer），记录请求日志，并统一处理异常。
    /// 在 <c>AddGrpc</c> 的拦截器列表中启用，对 RegisterNode 与 StartStreaming 都生效。
    /// </summary>
    public class GrpcAuthenticationInterceptor : Interceptor
    {
        /// <summary>
        /// 曾出现在公开仓库中的默认令牌。任何人都知道，视同未配置
        /// </summary>
        public static readonly IReadOnlySet<string> KnownInsecureTokens = new HashSet<string>(StringComparer.Ordinal) { "test-token" };

        /// <summary>
        /// 未预期异常返回给客户端的固定文本（详细信息只写日志，不返回给客户端）
        /// </summary>
        public const string InternalErrorMessage = "Internal server error";

        private readonly byte[]? _expectedTokenBytes;
        private readonly ILogger<GrpcAuthenticationInterceptor> _logger;

        public GrpcAuthenticationInterceptor(IConfiguration configuration, ILogger<GrpcAuthenticationInterceptor> logger)
        {
            _logger = logger;
            var token = configuration[$"{GrpcSettings.SectionName}:{nameof(GrpcSettings.NodeAuthToken)}"];
            IsTokenConfigured = IsUsableToken(token);
            if (IsTokenConfigured)
            {
                _expectedTokenBytes = Encoding.UTF8.GetBytes(token!);
            }
            else
            {
                _logger.LogError(
                    "未配置可用的节点令牌（GrpcSettings:NodeAuthToken 为空，或使用了公开的默认值 test-token），将拒绝所有 Worker 的注册与建流。"
                    + "请用环境变量 GrpcSettings__NodeAuthToken 设置一个随机令牌，并在 Worker 端设置相同的值");
            }
        }

        /// <summary>
        /// 是否配置了可用的令牌。为 false 时拒绝所有请求
        /// </summary>
        public bool IsTokenConfigured { get; }

        /// <summary>
        /// 令牌是否可用：非空，且不是公开的默认令牌
        /// </summary>
        public static bool IsUsableToken(string? token)
            => !string.IsNullOrWhiteSpace(token) && !KnownInsecureTokens.Contains(token.Trim());

        /// <summary>
        /// 一元服务端方法拦截（RegisterNode）
        /// </summary>
        public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
            TRequest request,
            ServerCallContext context,
            UnaryServerMethod<TRequest, TResponse> continuation)
        {
            Authenticate(context);

            var method = context.Method;
            _logger.LogInformation("一元请求开始: {Method}, 客户端: {Peer}", method, context.Peer);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var response = await continuation(request, context);
                _logger.LogInformation("一元请求结束: {Method}, 耗时 {ElapsedMs} ms", method, stopwatch.ElapsedMilliseconds);
                return response;
            }
            catch (RpcException)
            {
                // 业务代码主动返回的状态码（PermissionDenied 等）原样透传，客户端据此决定如何处理
                throw;
            }
            catch (Exception ex) when (IsClientDisconnect(ex))
            {
                _logger.LogInformation("一元请求因客户端断开而结束: {Method}, 耗时 {ElapsedMs} ms", method, stopwatch.ElapsedMilliseconds);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "一元请求失败: {Method}, 耗时 {ElapsedMs} ms", method, stopwatch.ElapsedMilliseconds);
                throw new RpcException(new Status(StatusCode.Internal, InternalErrorMessage));
            }
        }

        /// <summary>
        /// 双向流式服务端方法拦截（StartStreaming）
        /// </summary>
        public override async Task DuplexStreamingServerHandler<TRequest, TResponse>(
            IAsyncStreamReader<TRequest> requestStream,
            IServerStreamWriter<TResponse> responseStream,
            ServerCallContext context,
            DuplexStreamingServerMethod<TRequest, TResponse> continuation)
        {
            Authenticate(context);

            var method = context.Method;
            _logger.LogInformation("双向流请求开始: {Method}, 客户端: {Peer}", method, context.Peer);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                await continuation(requestStream, responseStream, context);
                _logger.LogInformation("双向流请求结束: {Method}, 耗时 {ElapsedMs} ms", method, stopwatch.ElapsedMilliseconds);
            }
            catch (RpcException)
            {
                // 会话无效（PermissionDenied）等状态码必须原样返回，Worker 依赖它决定重连还是重新注册
                throw;
            }
            catch (Exception ex) when (IsClientDisconnect(ex))
            {
                _logger.LogInformation("双向流因客户端断开而结束: {Method}, 耗时 {ElapsedMs} ms", method, stopwatch.ElapsedMilliseconds);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "双向流请求失败: {Method}, 耗时 {ElapsedMs} ms", method, stopwatch.ElapsedMilliseconds);
                throw new RpcException(new Status(StatusCode.Internal, InternalErrorMessage));
            }
        }

        /// <summary>
        /// 客户端断开或调用被取消时产生的异常，属于正常情况，不记为错误
        /// </summary>
        private static bool IsClientDisconnect(Exception ex) => ex is OperationCanceledException or IOException;

        /// <summary>
        /// 从请求头提取 Bearer 令牌并与配置值比较（定长比较，避免按耗时猜测令牌）
        /// </summary>
        private void Authenticate(ServerCallContext context)
        {
            if (_expectedTokenBytes == null)
            {
                _logger.LogWarning("Master 未配置可用的节点令牌，拒绝请求 {Method}，客户端: {Peer}", context.Method, context.Peer);
                throw new RpcException(new Status(StatusCode.Unauthenticated, "Node authentication token is not configured on master"));
            }

            var authHeader = context.RequestHeaders.GetValue("authorization");
            if (string.IsNullOrEmpty(authHeader))
            {
                _logger.LogWarning("缺少 Authorization 头，客户端: {Peer}", context.Peer);
                throw new RpcException(new Status(StatusCode.Unauthenticated, "Missing authorization token"));
            }

            const string bearerPrefix = "Bearer ";
            if (!authHeader.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Authorization 头格式错误，应为 Bearer token，客户端: {Peer}", context.Peer);
                throw new RpcException(new Status(StatusCode.Unauthenticated, "Invalid authorization header format"));
            }

            var tokenBytes = Encoding.UTF8.GetBytes(authHeader.Substring(bearerPrefix.Length));
            if (!CryptographicOperations.FixedTimeEquals(tokenBytes, _expectedTokenBytes))
            {
                _logger.LogWarning("令牌无效，客户端: {Peer}", context.Peer);
                throw new RpcException(new Status(StatusCode.Unauthenticated, "Invalid token"));
            }
        }
    }
}
