using System.Collections;
using CrawlerCore.Infrastructure;
using CrawlerWorker.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CrawlerCore.Tests.Worker;

/// <summary>Worker 的配置加载与启动前校验（W-13、W-14、W-24）</summary>
public class WorkerConfigurationTests
{
    [Fact(DisplayName = "W-13：MASTER_ENDPOINT、NODE_AUTH_TOKEN、WORKER_ENDPOINT 覆盖 appsettings.json")]
    public void EnvironmentAliases_OverrideAppSettings()
    {
        // 测试输出目录中有 Worker 的 appsettings.json（MasterEndpoint 为 http://localhost:5001，令牌为空）
        var environment = new Hashtable
        {
            ["MASTER_ENDPOINT"] = "http://master.example:6001",
            ["NODE_AUTH_TOKEN"] = "env-secret-value",
            ["WORKER_ENDPOINT"] = "http://0.0.0.0:7002",
        };

        using var host = WorkerConfiguration.CreateHostBuilder([], environment, useSerilog: false).Build();
        var settings = host.Services.GetRequiredService<IOptions<GrpcSettings>>().Value;

        Assert.Equal("http://master.example:6001", settings.MasterEndpoint);
        Assert.Equal("env-secret-value", settings.NodeAuthToken);
        Assert.Equal("http://0.0.0.0:7002", settings.WorkerEndpoint);
    }

    [Fact(DisplayName = "W-13：内容根固定为程序所在目录，不依赖当前工作目录；未设置的项取 appsettings.json")]
    public void ContentRoot_IsApplicationDirectory()
    {
        using var host = WorkerConfiguration.CreateHostBuilder([], new Hashtable(), useSerilog: false).Build();

        var contentRoot = host.Services.GetRequiredService<IHostEnvironment>().ContentRootPath;
        Assert.Equal(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), Path.TrimEndingDirectorySeparator(contentRoot));
        Assert.Equal(2, host.Services.GetRequiredService<IOptions<CrawlerSettings>>().Value.MaxConcurrentTasks);
    }

    [Fact(DisplayName = "W-13：空白的简写环境变量不生效")]
    public void BlankAliases_Ignored()
    {
        var mappings = WorkerConfiguration.MapEnvironmentAliases(new Hashtable { ["MASTER_ENDPOINT"] = "  ", ["NODE_AUTH_TOKEN"] = "x" });
        Assert.Equal(new[] { "GrpcSettings:NodeAuthToken" }, mappings.Keys);
    }

    [Fact(DisplayName = "W-14：启动日志只给出令牌长度，不输出令牌内容")]
    public void DescribeAliases_DoesNotRevealToken()
    {
        var lines = WorkerConfiguration.DescribeEnvironmentAliases(new Hashtable
        {
            ["NODE_AUTH_TOKEN"] = "super-secret-token-123",
            ["MASTER_ENDPOINT"] = "http://m:5001"
        }).ToList();

        Assert.Contains(lines, l => l.Contains("长度 22"));
        Assert.DoesNotContain(lines, l => l.Contains("super") || l.Contains("secret") || l.Contains("123"));
        Assert.Contains(lines, l => l.Contains("http://m:5001"));
    }

    [Theory(DisplayName = "W-24：令牌为空或为公开的 test-token 时拒绝启动")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("test-token")]
    public void Validate_RejectsMissingOrPublicToken(string token)
    {
        var errors = WorkerConfiguration.Validate(new GrpcSettings { NodeAuthToken = token, MasterEndpoint = "http://localhost:5001" });
        Assert.Single(errors);
        Assert.Contains("令牌", errors[0]);
    }

    [Fact(DisplayName = "Master 地址无效时拒绝启动；配置正确时没有错误")]
    public void Validate_Endpoint()
    {
        Assert.Single(WorkerConfiguration.Validate(new GrpcSettings { NodeAuthToken = "a-random-token", MasterEndpoint = "localhost:5001" }));
        Assert.Empty(WorkerConfiguration.Validate(new GrpcSettings { NodeAuthToken = "a-random-token", MasterEndpoint = "http://localhost:5001" }));
    }

    [Fact(DisplayName = "W-24：仓库中的 Worker 默认配置不带令牌")]
    public void ShippedAppSettings_HasNoToken()
    {
        using var host = WorkerConfiguration.CreateHostBuilder([], new Hashtable(), useSerilog: false).Build();
        var settings = host.Services.GetRequiredService<IOptions<GrpcSettings>>().Value;

        // 进程环境变量可能设置了 GrpcSettings__NodeAuthToken（本机运行 Master/Worker 的终端），只在没有设置时检查
        if (Environment.GetEnvironmentVariable("GrpcSettings__NodeAuthToken") == null)
        {
            Assert.Equal(string.Empty, settings.NodeAuthToken);
        }
        Assert.Equal("http://localhost:5001", new GrpcSettings().MasterEndpoint);
    }
}
