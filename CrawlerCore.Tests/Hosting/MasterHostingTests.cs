using System.Net;
using CrawlerCore.Infrastructure;
using CrawlerMaster.WPF.Data;
using CrawlerMaster.WPF.Hosting;
using Grpc.AspNetCore.Server;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace CrawlerCore.Tests.Hosting;

/// <summary>
/// Master 主机配置的单元测试（T4：M-05①、M-20、M-22）
/// </summary>
public class MasterHostingTests
{
    #region M-22 数据库路径
    [Fact(DisplayName = "相对路径的 Data Source 解析到程序目录下（M-22）")]
    public void ResolveSqliteConnectionString_RelativePath_UsesBaseDirectory()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "master-app");

        var resolved = MasterHostingExtensions.ResolveSqliteConnectionString("Data Source=crawler.db", baseDirectory);

        var dataSource = new SqliteConnectionStringBuilder(resolved).DataSource;
        Assert.Equal(Path.Combine(baseDirectory, "crawler.db"), dataSource);
    }

    [Theory(DisplayName = "绝对路径、内存库、URI 形式保持不变（M-22）")]
    [InlineData(":memory:")]
    [InlineData("file:crawler.db?mode=memory")]
    public void ResolveSqliteConnectionString_SpecialDataSources_Unchanged(string dataSource)
    {
        var connectionString = $"Data Source={dataSource}";

        Assert.Equal(connectionString, MasterHostingExtensions.ResolveSqliteConnectionString(connectionString, "C:\\app"));
    }

    [Fact(DisplayName = "绝对路径保持不变（M-22）")]
    public void ResolveSqliteConnectionString_AbsolutePath_Unchanged()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "x", "crawler.db");
        var connectionString = $"Data Source={absolute}";

        Assert.Equal(connectionString, MasterHostingExtensions.ResolveSqliteConnectionString(connectionString, "C:\\app"));
    }

    [Fact(DisplayName = "注册到容器的 DbContext 使用程序目录下的数据库文件（M-22）")]
    public void AddMasterCoreServices_UsesDatabaseUnderBaseDirectory()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddMasterCoreServices(configuration);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var connectionString = scope.ServiceProvider.GetRequiredService<CrawlerDbContext>().Database.GetConnectionString();

        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "crawler.db"), new SqliteConnectionStringBuilder(connectionString).DataSource);
    }
    #endregion

    #region M-05① keepalive
    [Fact(DisplayName = "HTTP/2 keepalive 取自 GrpcSettings（M-05①）")]
    public void ConfigureMasterHttp2KeepAlive_UsesSettings()
    {
        var options = new KestrelServerOptions();

        MasterHostingExtensions.ConfigureMasterHttp2KeepAlive(options, new GrpcSettings { KeepAlivePingDelaySeconds = 7, KeepAlivePingTimeoutSeconds = 9 });

        Assert.Equal(TimeSpan.FromSeconds(7), options.Limits.Http2.KeepAlivePingDelay);
        Assert.Equal(TimeSpan.FromSeconds(9), options.Limits.Http2.KeepAlivePingTimeout);
    }

    [Fact(DisplayName = "keepalive 配置无效（≤0）时使用默认值 15/30 秒，不会关闭 keepalive（M-05①）")]
    public void ConfigureMasterHttp2KeepAlive_InvalidValues_UseDefaults()
    {
        var options = new KestrelServerOptions();

        MasterHostingExtensions.ConfigureMasterHttp2KeepAlive(options, new GrpcSettings { KeepAlivePingDelaySeconds = 0, KeepAlivePingTimeoutSeconds = -1 });

        Assert.Equal(TimeSpan.FromSeconds(15), options.Limits.Http2.KeepAlivePingDelay);
        Assert.Equal(TimeSpan.FromSeconds(30), options.Limits.Http2.KeepAlivePingTimeout);
    }
    #endregion

    #region M-20 SignalR
    [Theory(DisplayName = "SignalR 监听地址：默认环回，空/*/0.0.0.0 表示所有网卡，无法解析时退回环回（M-20）")]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("localhost", "127.0.0.1")]
    [InlineData("192.168.1.10", "192.168.1.10")]
    [InlineData("not-an-ip", "127.0.0.1")]
    [InlineData("0.0.0.0", null)]
    [InlineData("*", null)]
    [InlineData("", null)]
    public void ResolveListenAddress_Cases(string configured, string? expected)
    {
        var address = MasterHostingExtensions.ResolveListenAddress(configured);

        Assert.Equal(expected, address?.ToString());
    }

    [Fact(DisplayName = "SignalR 默认配置只监听本机")]
    public void SignalRSettings_DefaultListensOnLoopback()
    {
        Assert.Equal(IPAddress.Loopback, MasterHostingExtensions.ResolveListenAddress(new SignalRSettings().ListenAddress));
    }

    private static ServiceProvider BuildWebServices(string environment, params string[] allowedOrigins)
    {
        var settings = new Dictionary<string, string?>();
        for (var i = 0; i < allowedOrigins.Length; i++)
        {
            settings[$"SignalRSettings:AllowedOrigins:{i}"] = allowedOrigins[i];
        }
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var environmentMock = new Mock<IHostEnvironment>();
        environmentMock.SetupGet(e => e.EnvironmentName).Returns(environment);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(environmentMock.Object);
        services.AddMasterWebServices(configuration);
        return services.BuildServiceProvider();
    }

    [Fact(DisplayName = "CORS 只允许白名单中的来源，默认不允许任何跨域来源（M-20）")]
    public void Cors_OnlyAllowsConfiguredOrigins()
    {
        using var defaults = BuildWebServices(Environments.Production);
        var defaultPolicy = defaults.GetRequiredService<IOptions<CorsOptions>>().Value.GetPolicy(MasterHostingExtensions.SignalRCorsPolicy)!;
        Assert.False(defaultPolicy.IsOriginAllowed("http://evil.example"));
        Assert.False(defaultPolicy.SupportsCredentials);

        using var configured = BuildWebServices(Environments.Production, "http://localhost:5173");
        var policy = configured.GetRequiredService<IOptions<CorsOptions>>().Value.GetPolicy(MasterHostingExtensions.SignalRCorsPolicy)!;
        Assert.True(policy.IsOriginAllowed("http://localhost:5173"));
        Assert.False(policy.IsOriginAllowed("http://evil.example"));
    }

    [Theory(DisplayName = "gRPC 与 SignalR 的详细错误只在 Development 环境开启（M-20、M-29）")]
    [InlineData("Development", true)]
    [InlineData("Production", false)]
    public void DetailedErrors_OnlyInDevelopment(string environment, bool expected)
    {
        using var provider = BuildWebServices(environment);

        Assert.Equal(expected, provider.GetRequiredService<IOptions<GrpcServiceOptions>>().Value.EnableDetailedErrors);
        Assert.Equal(expected, provider.GetRequiredService<IOptions<HubOptions>>().Value.EnableDetailedErrors);
    }

    [Fact(DisplayName = "节点令牌认证拦截器已加入 gRPC 拦截器列表（M-01）")]
    public void Grpc_RegistersAuthenticationInterceptor()
    {
        using var provider = BuildWebServices(Environments.Production);

        var interceptors = provider.GetRequiredService<IOptions<GrpcServiceOptions>>().Value.Interceptors;

        Assert.Contains(interceptors, i => i.Type == typeof(CrawlerMaster.WPF.Infrastructure.GrpcAuthenticationInterceptor));
    }
    #endregion
}
