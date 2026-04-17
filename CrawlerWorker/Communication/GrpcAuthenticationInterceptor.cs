using CrawlerCore.Infrastructure;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

namespace CrawlerWorker.Communication;

/// <summary>
/// Worker服务端全局认证拦截器，仅允许Master端合法请求访问
/// </summary>
public class GrpcAuthenticationInterceptor : Interceptor
{
    private readonly GrpcSettings _grpcSettings;
    private readonly ILogger<GrpcAuthenticationInterceptor> _logger;

    public GrpcAuthenticationInterceptor(IOptions<GrpcSettings> grpcSettings, ILogger<GrpcAuthenticationInterceptor> logger)
    {
        _grpcSettings = grpcSettings.Value;
        _logger = logger;
    }

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            var authHeader = context.RequestHeaders.Get("authorization");
            if (authHeader == null || !authHeader.Value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("未携带有效认证Token，拒绝访问，请求方法: {Method}", context.Method);
                throw new RpcException(new Status(StatusCode.Unauthenticated, "缺少有效认证Token"));
            }

            var token = authHeader.Value["Bearer ".Length..].Trim();
            if (!token.Equals(_grpcSettings.NodeAuthToken, StringComparison.Ordinal))
            {
                _logger.LogWarning("认证Token无效，拒绝访问，请求方法: {Method}", context.Method);
                throw new RpcException(new Status(StatusCode.Unauthenticated, "认证Token无效"));
            }

            _logger.LogDebug("请求认证通过，方法: {Method}, 客户端: {Peer}", context.Method, context.Peer);
            return await continuation(request, context).ConfigureAwait(false);
        }
        catch (RpcException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "服务端请求处理异常，方法: {Method}", context.Method);
            throw new RpcException(new Status(StatusCode.Internal, "服务端内部处理异常"));
        }
    }

    public override async Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            var authHeader = context.RequestHeaders.Get("authorization");
            if (authHeader == null || !authHeader.Value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("未携带有效认证Token，拒绝访问，请求方法: {Method}", context.Method);
                throw new RpcException(new Status(StatusCode.Unauthenticated, "缺少有效认证Token"));
            }

            var token = authHeader.Value["Bearer ".Length..].Trim();
            if (!token.Equals(_grpcSettings.NodeAuthToken, StringComparison.Ordinal))
            {
                _logger.LogWarning("认证Token无效，拒绝访问，请求方法: {Method}", context.Method);
                throw new RpcException(new Status(StatusCode.Unauthenticated, "认证Token无效"));
            }

            _logger.LogDebug("请求认证通过，方法: {Method}, 客户端: {Peer}", context.Method, context.Peer);
            await continuation(requestStream, responseStream, context).ConfigureAwait(false);
        }
        catch (RpcException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "服务端请求处理异常，方法: {Method}", context.Method);
            throw new RpcException(new Status(StatusCode.Internal, "服务端内部处理异常"));
        }
    }
}