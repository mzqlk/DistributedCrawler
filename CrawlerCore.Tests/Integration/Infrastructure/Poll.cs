using System.Diagnostics;

namespace CrawlerCore.Tests.Integration.Infrastructure;

/// <summary>
/// 轮询等待：周期性检查条件，满足即返回，超时抛出带描述的异常。集成测试不使用固定 sleep。
/// </summary>
public static class Poll
{
    /// <summary>默认超时。正常情况下各步骤在 1~5 秒内完成，留足余量以应对机器繁忙</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    /// <summary>等待异步条件成立</summary>
    public static async Task UntilAsync(Func<Task<bool>> condition, string description, TimeSpan? timeout = null)
    {
        await CoreAsync(async () => (await condition().ConfigureAwait(false), (object?)null), description, timeout)
            .ConfigureAwait(false);
    }

    /// <summary>等待同步条件成立</summary>
    public static Task UntilAsync(Func<bool> condition, string description, TimeSpan? timeout = null)
        => UntilAsync(() => Task.FromResult(condition()), description, timeout);

    /// <summary>等待探测函数返回非 null 值，并返回该值</summary>
    public static async Task<T> ValueAsync<T>(Func<Task<T?>> probe, string description, TimeSpan? timeout = null)
        where T : class
    {
        var result = await CoreAsync(async () =>
        {
            var value = await probe().ConfigureAwait(false);
            return (value is not null, (object?)value);
        }, description, timeout).ConfigureAwait(false);
        return (T)result!;
    }

    private static async Task<object?> CoreAsync(
        Func<Task<(bool Satisfied, object? Value)>> probe,
        string description,
        TimeSpan? timeout)
    {
        var limit = timeout ?? DefaultTimeout;
        var stopwatch = Stopwatch.StartNew();
        Exception? lastError = null;

        while (true)
        {
            try
            {
                var (satisfied, value) = await probe().ConfigureAwait(false);
                if (satisfied)
                {
                    return value;
                }
            }
            catch (Exception ex)
            {
                // 探测过程中的瞬时异常（如数据库忙）不立即失败，超时时一并报告
                lastError = ex;
            }

            if (stopwatch.Elapsed > limit)
            {
                throw new TimeoutException($"等待超时（{limit.TotalSeconds:F0} 秒）：{description}", lastError);
            }

            await Task.Delay(Interval).ConfigureAwait(false);
        }
    }
}
