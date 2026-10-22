using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;
using Xunit.Abstractions;

namespace CrawlerCore.Tests.Integration.Infrastructure;

/// <summary>
/// 把 Serilog 日志写到 xUnit 的测试输出。Master 与 Worker 的日志（LogHelper 与 ILogger）都走全局 Serilog，
/// 测试失败时可在输出中看到完整过程。Component 属性标明日志来自 Master 还是哪个 Worker；
/// 为空的通常是 Master 处理 gRPC 请求的线程（Kestrel 不沿用主机启动时的异步上下文）。
/// </summary>
public sealed class TestOutputLogSink : ILogEventSink
{
    private readonly ITestOutputHelper _output;
    private readonly MessageTemplateTextFormatter _formatter =
        new("{Timestamp:HH:mm:ss.fff} [{Level:u3}] [{Component}] {Message:lj}{NewLine}{Exception}");

    public TestOutputLogSink(ITestOutputHelper output)
    {
        _output = output;
    }

    public void Emit(LogEvent logEvent)
    {
        using var writer = new StringWriter();
        _formatter.Format(logEvent, writer);
        try
        {
            _output.WriteLine(writer.ToString().TrimEnd());
        }
        catch (InvalidOperationException)
        {
            // 测试结束后后台线程仍可能写日志，此时输出通道已关闭，忽略即可
        }
    }
}
