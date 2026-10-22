using CrawlerCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using System;
using System.Collections;
using System.Collections.Generic;

namespace CrawlerWorker.Hosting;

/// <summary>
/// Worker 主机的配置（W-13）：Program.cs 用它构建主机，单元测试直接验证。
/// <list type="bullet">
/// <item>内容根固定为程序所在目录，appsettings.json 不随工作目录变化（在仓库根目录 <c>dotnet run --project CrawlerWorker</c> 也能找到）。</item>
/// <item>配置优先级（低 → 高）：appsettings.json → appsettings.{环境}.json → 环境变量（<c>Section__Key</c>）→ 命令行 →
/// 兼容的简写环境变量 <c>MASTER_ENDPOINT</c>、<c>NODE_AUTH_TOKEN</c>、<c>WORKER_ENDPOINT</c>。</item>
/// </list>
/// </summary>
public static class WorkerConfiguration
{
    /// <summary>曾经提交在公开仓库里的默认令牌，Master 会拒绝它</summary>
    public const string PublicDefaultToken = "test-token";

    /// <summary>简写环境变量 → 配置键</summary>
    public static readonly IReadOnlyDictionary<string, string> EnvironmentAliases = new Dictionary<string, string>
    {
        ["MASTER_ENDPOINT"] = "GrpcSettings:MasterEndpoint",
        ["NODE_AUTH_TOKEN"] = "GrpcSettings:NodeAuthToken",
        ["WORKER_ENDPOINT"] = "GrpcSettings:WorkerEndpoint",
    };

    /// <summary>从环境变量中取出设置了值的简写变量，映射为配置键</summary>
    public static Dictionary<string, string?> MapEnvironmentAliases(IDictionary environment)
    {
        var mappings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (variable, key) in EnvironmentAliases)
        {
            if (environment[variable] is string value && !string.IsNullOrWhiteSpace(value))
            {
                mappings[key] = value.Trim();
            }
        }
        return mappings;
    }

    /// <summary>
    /// 说明哪些简写环境变量生效了，用于启动日志。令牌只给出长度，不输出任何字符（W-14）
    /// </summary>
    public static IEnumerable<string> DescribeEnvironmentAliases(IDictionary environment)
    {
        var mappings = MapEnvironmentAliases(environment);
        foreach (var (variable, key) in EnvironmentAliases)
        {
            if (!mappings.TryGetValue(key, out var value)) continue;
            yield return key == "GrpcSettings:NodeAuthToken"
                ? $"已通过环境变量 {variable} 设置节点令牌（长度 {value!.Length}）"
                : $"已通过环境变量 {variable} 设置 {key} = {value}";
        }
    }

    /// <summary>构建 Worker 主机</summary>
    /// <param name="args">命令行参数</param>
    /// <param name="environment">环境变量（测试时传入假的集合）；为 null 时读取进程环境变量</param>
    /// <param name="useSerilog">是否用配置中的 Serilog 设置替换全局日志（测试时传 false）</param>
    public static IHostBuilder CreateHostBuilder(string[] args, IDictionary? environment = null, bool useSerilog = true)
    {
        var aliases = MapEnvironmentAliases(environment ?? Environment.GetEnvironmentVariables());
        var builder = Host.CreateDefaultBuilder(args)
            .UseContentRoot(AppContext.BaseDirectory)
            // 最后加入的配置源优先级最高，简写环境变量因此能覆盖 appsettings.json
            .ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(aliases))
            .ConfigureServices((context, services) => services.AddCrawlerWorker(context.Configuration));

        if (useSerilog)
        {
            builder.UseSerilog((context, _, logger) => logger.ReadFrom.Configuration(context.Configuration));
        }
        return builder;
    }

    /// <summary>
    /// 启动前检查必须正确的配置；返回的每一条都是无法继续运行的原因（Program.cs 记 Fatal 后以退出码 1 退出）
    /// </summary>
    public static IReadOnlyList<string> Validate(GrpcSettings settings)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(settings.NodeAuthToken))
        {
            errors.Add("未配置节点令牌。请设置环境变量 NODE_AUTH_TOKEN（或 GrpcSettings__NodeAuthToken），值与 Master 的 GrpcSettings__NodeAuthToken 相同。"
                + "生成随机令牌的方法见 docs/project/deployment.md“节点令牌”");
        }
        else if (string.Equals(settings.NodeAuthToken.Trim(), PublicDefaultToken, StringComparison.Ordinal))
        {
            errors.Add($"节点令牌是曾经公开在仓库里的默认值 {PublicDefaultToken}，Master 会拒绝它。请改用随机生成的令牌");
        }

        if (!Uri.TryCreate(settings.MasterEndpoint, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            errors.Add($"Master 地址无效：{settings.MasterEndpoint}。请设置 MASTER_ENDPOINT（或 GrpcSettings__MasterEndpoint），例如 http://localhost:5001");
        }
        return errors;
    }
}
