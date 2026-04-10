using CrawlerCore.Infrastructure;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using System;

namespace CrawlerWorker.Communication;

/// <summary>
/// Worker客户端认证拦截器，用于向Master发送请求时自动添加Token
/// </summary>
public class GrpcClientAuthenticationInterceptor : Interceptor
{
    private readonly GrpcSettings _grpcSettings;
    private readonly ILogger<GrpcClientAuthenticationInterceptor> _logger;

    public GrpcClientAuthenticationInterceptor(
        IOptions<GrpcSettings> grpcSettings,
        ILogger<GrpcClientAuthenticationInterceptor> logger)
    {
        _grpcSettings = grpcSettings.Value ?? throw new ArgumentNullException(nameof(grpcSettings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // 拦截一元调用
    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        var newContext = CreateContextWithAuth(context);
        _logger.LogDebug("已添加认证Token到一元请求: {Method}", context.Method);
        return continuation(request, newContext);
    }

    // 拦截服务器流式调用
    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncServerStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        var newContext = CreateContextWithAuth(context);
        _logger.LogDebug("已添加认证Token到服务器流式请求: {Method}", context.Method);
        return continuation(request, newContext);
    }

    // 拦截客户端流式调用
    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncClientStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        var newContext = CreateContextWithAuth(context);
        _logger.LogDebug("已添加认证Token到客户端流式请求: {Method}", context.Method);
        return continuation(newContext);
    }

    // 拦截双向流式调用（节点注册通常用这个）
    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncDuplexStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        var newContext = CreateContextWithAuth(context);
        _logger.LogDebug("已添加认证Token到双向流请求: {Method}", context.Method);
        return continuation(newContext);
    }

    // 拦截阻塞调用
    public override TResponse BlockingUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        BlockingUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        var newContext = CreateContextWithAuth(context);
        _logger.LogDebug("已添加认证Token到阻塞请求: {Method}", context.Method);
        return continuation(request, newContext);
    }

    private ClientInterceptorContext<TRequest, TResponse> CreateContextWithAuth<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context)
        where TRequest : class
        where TResponse : class
    {
        var metadata = AddAuthHeader(context.Options.Headers);
        var newOptions = context.Options.WithHeaders(metadata);
        return new ClientInterceptorContext<TRequest, TResponse>(
            context.Method, context.Host, newOptions);
    }

    private Metadata AddAuthHeader(Metadata? existingHeaders)
    {
        var metadata = existingHeaders ?? new Metadata();

        // 检查是否已存在认证头，避免重复添加
        var existingAuth = metadata.Get("authorization");
        if (existingAuth == null)
        {
            metadata.Add("authorization", $"Bearer {_grpcSettings.NodeAuthToken}");
            _logger.LogDebug("Token已添加: {Token}", _grpcSettings.NodeAuthToken[..Math.Min(10, _grpcSettings.NodeAuthToken.Length)] + "...");
        }
        else
        {
            _logger.LogDebug("认证头已存在，跳过添加");
        }

        return metadata;
    }
}