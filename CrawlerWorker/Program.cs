using CrawlerCore.Infrastructure;
using CrawlerWorker.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;

// Master 只提供明文 HTTP/2（h2c）
AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
    .CreateBootstrapLogger();

try
{
    Log.Information("Worker节点应用启动，开始加载配置");
    var environment = Environment.GetEnvironmentVariables();
    foreach (var line in WorkerConfiguration.DescribeEnvironmentAliases(environment))
    {
        Log.Information(line);
    }

    // 配置与服务注册见 WorkerConfiguration / WorkerServiceCollectionExtensions（与测试共用）
    using var host = WorkerConfiguration.CreateHostBuilder(args, environment).Build();

    // 配置错误时不启动：记 Fatal 并以退出码 1 退出，容器的重启策略据此识别失败（W-11）
    var errors = WorkerConfiguration.Validate(host.Services.GetRequiredService<IOptions<GrpcSettings>>().Value);
    if (errors.Count > 0)
    {
        foreach (var error in errors)
        {
            Log.Fatal("配置错误：{Error}", error);
        }
        return 1;
    }

    await host.RunAsync();
    return Environment.ExitCode;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Worker节点应用启动失败，致命异常");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
