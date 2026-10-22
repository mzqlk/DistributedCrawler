using CrawlerCore.Protos;
using CrawlerMaster.WPF.Infrastructure;
using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CrawlerCore.Tests.Grpc;

/// <summary>
/// Master 认证拦截器单元测试（T4：M-01、M-09、M-21）
/// </summary>
public class GrpcAuthenticationInterceptorTests
{
    private const string Token = "unit-test-token";

    private static GrpcAuthenticationInterceptor CreateInterceptor(string? token)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["GrpcSettings:NodeAuthToken"] = token })
            .Build();
        return new GrpcAuthenticationInterceptor(configuration, NullLogger<GrpcAuthenticationInterceptor>.Instance);
    }

    private static TestServerCallContext ContextWithToken(string? token)
    {
        var headers = new Metadata();
        if (token != null)
        {
            headers.Add("authorization", $"Bearer {token}");
        }
        return new TestServerCallContext(headers);
    }

    private static Task InvokeDuplexAsync(GrpcAuthenticationInterceptor interceptor, ServerCallContext context, Func<Task> body)
        => interceptor.DuplexStreamingServerHandler<FromWorker, FromMaster>(
            new FakeRequestStream<FromWorker>(),
            new FakeResponseStream<FromMaster>(),
            context,
            (_, _, _) => body());

    private static Task<RegistrationResponse> InvokeUnaryAsync(GrpcAuthenticationInterceptor interceptor, ServerCallContext context, Func<Task<RegistrationResponse>> body)
        => interceptor.UnaryServerHandler<NodeRegistration, RegistrationResponse>(
            new NodeRegistration(),
            context,
            (_, _) => body());

    [Fact(DisplayName = "令牌正确：请求被放行")]
    public async Task ValidToken_IsAccepted()
    {
        var interceptor = CreateInterceptor(Token);

        var response = await InvokeUnaryAsync(interceptor, ContextWithToken(Token), () => Task.FromResult(new RegistrationResponse { Success = true }));

        Assert.True(response.Success);
        Assert.True(interceptor.IsTokenConfigured);
    }

    [Theory(DisplayName = "缺少或错误的令牌：返回 Unauthenticated，且不执行业务逻辑")]
    [InlineData(null)]
    [InlineData("wrong-token")]
    [InlineData("")]
    public async Task MissingOrWrongToken_IsRejected(string? token)
    {
        var interceptor = CreateInterceptor(Token);
        var called = false;

        var error = await Assert.ThrowsAsync<RpcException>(() =>
            InvokeDuplexAsync(interceptor, ContextWithToken(token), () => { called = true; return Task.CompletedTask; }));

        Assert.Equal(StatusCode.Unauthenticated, error.StatusCode);
        Assert.False(called);
    }

    [Theory(DisplayName = "Master 未配置令牌或使用公开的默认令牌 test-token：拒绝所有节点（M-21）")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("test-token")]
    public async Task UnusableConfiguredToken_RejectsEveryone(string? configuredToken)
    {
        var interceptor = CreateInterceptor(configuredToken);

        Assert.False(interceptor.IsTokenConfigured);
        var error = await Assert.ThrowsAsync<RpcException>(() =>
            InvokeUnaryAsync(interceptor, ContextWithToken(configuredToken ?? "test-token"), () => Task.FromResult(new RegistrationResponse())));
        Assert.Equal(StatusCode.Unauthenticated, error.StatusCode);
    }

    [Theory(DisplayName = "业务代码返回的状态码原样透传，不被改写为 Internal（M-09）")]
    [InlineData(StatusCode.PermissionDenied)]
    [InlineData(StatusCode.AlreadyExists)]
    public async Task RpcException_PassesThroughUnchanged(StatusCode statusCode)
    {
        var interceptor = CreateInterceptor(Token);

        var duplexError = await Assert.ThrowsAsync<RpcException>(() =>
            InvokeDuplexAsync(interceptor, ContextWithToken(Token), () => throw new RpcException(new Status(statusCode, "业务状态"))));
        var unaryError = await Assert.ThrowsAsync<RpcException>(() =>
            InvokeUnaryAsync(interceptor, ContextWithToken(Token), () => throw new RpcException(new Status(statusCode, "业务状态"))));

        Assert.Equal(statusCode, duplexError.StatusCode);
        Assert.Equal("业务状态", duplexError.Status.Detail);
        Assert.Equal(statusCode, unaryError.StatusCode);
    }

    [Fact(DisplayName = "未预期异常转为 Internal，只返回固定文本，不泄露异常信息（M-09）")]
    public async Task UnexpectedException_BecomesInternalWithFixedMessage()
    {
        var interceptor = CreateInterceptor(Token);

        var error = await Assert.ThrowsAsync<RpcException>(() =>
            InvokeDuplexAsync(interceptor, ContextWithToken(Token), () => throw new InvalidOperationException("数据库路径 C:\\secret\\crawler.db")));

        Assert.Equal(StatusCode.Internal, error.StatusCode);
        Assert.Equal(GrpcAuthenticationInterceptor.InternalErrorMessage, error.Status.Detail);
        Assert.DoesNotContain("secret", error.Status.Detail);
    }

    [Fact(DisplayName = "客户端断开（OperationCanceledException/IOException）原样抛出，不转为 Internal（M-09）")]
    public async Task ClientDisconnect_IsRethrown()
    {
        var interceptor = CreateInterceptor(Token);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            InvokeDuplexAsync(interceptor, ContextWithToken(Token), () => throw new OperationCanceledException()));
        await Assert.ThrowsAsync<IOException>(() =>
            InvokeDuplexAsync(interceptor, ContextWithToken(Token), () => throw new IOException("连接被重置")));
    }
}
