using System.Diagnostics;
using CrawlerCore.Infrastructure;
using CrawlerWorker.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CrawlerCore.Tests.Worker;

/// <summary>节点资源采集（W-16 Windows CPU、W-17 容器口径）</summary>
public class ResourceUsageTests
{
    [Fact(DisplayName = "W-17 ④：/proc/stat 的总时间不重复计入 guest、guest_nice")]
    public void ProcStat_ExcludesGuestColumns()
    {
        //            user nice system idle iowait irq softirq steal guest guest_nice
        const string line = "cpu  100  10   50     800  40     5   5       0     70    3";

        Assert.True(ResourceUsageCalculator.TryParseProcStatCpuLine(line, out var idle, out var total));
        Assert.Equal(840, idle);
        Assert.Equal(1010, total);
        Assert.False(ResourceUsageCalculator.TryParseProcStatCpuLine("cpu0 1 2 3 4", out _, out _));
    }

    [Fact(DisplayName = "使用率由两次采样的差计算，数据异常时为 null")]
    public void UsagePercent()
    {
        Assert.Equal(75, ResourceUsageCalculator.UsagePercent(idleDelta: 25, totalDelta: 100));
        Assert.Null(ResourceUsageCalculator.UsagePercent(10, 0));
        Assert.Null(ResourceUsageCalculator.UsagePercent(-1, 100));
    }

    [Theory(DisplayName = "W-17 ①：cpu.max 解析出可用核数（可以是小数），无限制时取处理器数")]
    [InlineData("max 100000", 8, 8)]
    [InlineData("150000 100000", 8, 1.5)]
    [InlineData("50000 100000", 8, 0.5)]
    [InlineData("", 4, 4)]
    [InlineData(null, 4, 4)]
    public void EffectiveCores(string? cpuMax, int processors, double expected)
        => Assert.Equal(expected, ResourceUsageCalculator.ParseEffectiveCores(cpuMax, processors));

    [Fact(DisplayName = "W-17 ①：容器 CPU 使用率按配额计算：限制为 1 核的容器跑满时接近 100%")]
    public void CgroupUsage_RelativeToQuota()
    {
        const string before = "usage_usec 1000000\nuser_usec 800000\nsystem_usec 200000\n";
        const string after = "usage_usec 1990000\nuser_usec 1700000\nsystem_usec 290000\n";
        Assert.True(ResourceUsageCalculator.TryParseCpuStatUsageUsec(before, out var u1));
        Assert.True(ResourceUsageCalculator.TryParseCpuStatUsageUsec(after, out var u2));

        // 1 秒内用了 0.99 秒 CPU：1 核配额下是 99%，宿主机 8 核时按整机口径只有约 12%
        Assert.Equal(99, ResourceUsageCalculator.CgroupUsagePercent(u2 - u1, TimeSpan.FromSeconds(1), effectiveCores: 1)!.Value, 3);
        Assert.Equal(12.375, ResourceUsageCalculator.CgroupUsagePercent(u2 - u1, TimeSpan.FromSeconds(1), effectiveCores: 8)!.Value, 3);
        Assert.Null(ResourceUsageCalculator.CgroupUsagePercent(100, TimeSpan.Zero, 1));
    }

    [Fact(DisplayName = "W-17 ②：容器内存使用量减去可回收的页缓存 inactive_file")]
    public void ContainerMemory_ExcludesInactiveFile()
    {
        const string memoryStat = "anon 104857600\nfile 314572800\nactive_file 52428800\ninactive_file 262144000\n";
        Assert.Equal(419430400 - 262144000, ResourceUsageCalculator.ContainerMemoryUsageBytes(419430400, memoryStat));
        Assert.Equal(1000, ResourceUsageCalculator.ContainerMemoryUsageBytes(1000, null));
    }

    [Fact(DisplayName = "W-16：Windows 上 CPU 使用率能正常采集（不再恒为 0），没有警告日志")]
    public async Task WindowsCpuUsage_IsSampled()
    {
        if (!OperatingSystem.IsWindows()) return;

        var logger = new CollectingLogger<NodeInfoCollector>();
        var collector = new NodeInfoCollector(
            logger,
            new WorkerSessionManager(NullLogger<WorkerSessionManager>.Instance),
            Options.Create(new GrpcSettings()),
            Options.Create(new CrawlerSettings()));

        Assert.Equal(0, (await collector.CollectHeartbeatAsync()).CpuUsage); // 首次采样
        // 所有处理器都忙一段时间，保证第二次采样大于 0
        var busyUntil = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 2;
        Parallel.For(0, Environment.ProcessorCount, _ =>
        {
            while (Stopwatch.GetTimestamp() < busyUntil) { }
        });
        var second = await collector.CollectHeartbeatAsync();

        Assert.InRange(second.CpuUsage, 0.01, 100);
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    private sealed class CollectingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
