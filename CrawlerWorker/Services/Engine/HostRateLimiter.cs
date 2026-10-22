using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace CrawlerWorker.Services.Engine
{
    /// <summary>
    /// 按主机限速（W-08）：本 Worker 进程内所有任务对同一主机的请求（包括 robots.txt、每一跳重定向和重试）
    /// 都要先经过它，相邻两次请求的间隔不小于双方要求的间隔中较大的一个。
    /// 跨节点（多个 Worker 共用一个出口 IP）的总速率不在这里控制，见路线图 T6。
    /// </summary>
    public interface IHostRateLimiter
    {
        /// <summary>
        /// 等到允许向 <paramref name="host"/> 发出下一个请求时返回，并把这一刻记为该主机的最近一次请求
        /// </summary>
        /// <param name="host">主机名（用 <see cref="HostRateLimiter.HostKey"/> 计算）</param>
        /// <param name="interval">本次请求要求的最小间隔</param>
        Task WaitAsync(string host, TimeSpan interval, CancellationToken cancellationToken);

        /// <summary>
        /// 推迟该主机的下一次请求（例如收到 429/503 的 Retry-After），对所有任务生效
        /// </summary>
        void Defer(string host, TimeSpan delay);
    }

    /// <summary>
    /// 每个主机一个信号量串行发放请求许可，记录最近一次请求的时间与间隔，以及 Retry-After 推迟到的时刻。
    /// 用单调时钟（<see cref="TimeProvider.GetTimestamp"/>）计时，不受系统时间调整影响
    /// </summary>
    public sealed class HostRateLimiter : IHostRateLimiter
    {
        private readonly TimeProvider _timeProvider;
        private readonly ConcurrentDictionary<string, HostState> _hosts = new(StringComparer.OrdinalIgnoreCase);

        public HostRateLimiter(TimeProvider timeProvider)
        {
            _timeProvider = timeProvider;
        }

        /// <summary>限速使用的主机键：小写主机名，不含端口</summary>
        public static string HostKey(Uri url) => url.IdnHost.ToLowerInvariant();

        public async Task WaitAsync(string host, TimeSpan interval, CancellationToken cancellationToken)
        {
            var state = _hosts.GetOrAdd(host, _ => new HostState());
            await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                while (true)
                {
                    long earliest;
                    lock (state)
                    {
                        earliest = state.DeferredUntil;
                        if (state.LastRequest.HasValue)
                        {
                            var gap = interval > state.LastInterval ? interval : state.LastInterval;
                            earliest = Math.Max(earliest, state.LastRequest.Value + ToTimestamp(gap));
                        }
                    }

                    var remaining = earliest - _timeProvider.GetTimestamp();
                    if (remaining <= 0)
                    {
                        break;
                    }
                    // 等待期间可能有新的 Defer，醒来后重新计算
                    await Task.Delay(FromTimestamp(remaining), _timeProvider, cancellationToken).ConfigureAwait(false);
                }

                lock (state)
                {
                    state.LastRequest = _timeProvider.GetTimestamp();
                    state.LastInterval = interval;
                }
            }
            finally
            {
                state.Gate.Release();
            }
        }

        public void Defer(string host, TimeSpan delay)
        {
            if (delay <= TimeSpan.Zero) return;
            var state = _hosts.GetOrAdd(host, _ => new HostState());
            var until = _timeProvider.GetTimestamp() + ToTimestamp(delay);
            lock (state)
            {
                state.DeferredUntil = Math.Max(state.DeferredUntil, until);
            }
        }

        private long ToTimestamp(TimeSpan span) => (long)(span.TotalSeconds * _timeProvider.TimestampFrequency);

        private TimeSpan FromTimestamp(long ticks)
            // 向上取整到毫秒，避免因舍入提前醒来
            => TimeSpan.FromMilliseconds(Math.Ceiling(ticks * 1000.0 / _timeProvider.TimestampFrequency));

        private sealed class HostState
        {
            public SemaphoreSlim Gate { get; } = new(1, 1);
            public long? LastRequest { get; set; }
            public TimeSpan LastInterval { get; set; }
            public long DeferredUntil { get; set; }
        }
    }
}
