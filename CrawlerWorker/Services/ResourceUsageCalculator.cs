using System;
using System.Globalization;
using System.Linq;

namespace CrawlerWorker.Services
{
    /// <summary>
    /// 资源采集中与平台无关的解析与计算（W-16、W-17），写成纯函数便于测试。文件读取在 <see cref="NodeInfoCollector"/> 中
    /// </summary>
    public static class ResourceUsageCalculator
    {
        /// <summary>按两次采样的空闲时间与总时间之差计算 CPU 使用率（%），数据异常时返回 null</summary>
        public static double? UsagePercent(long idleDelta, long totalDelta)
        {
            if (totalDelta <= 0 || idleDelta < 0 || idleDelta > totalDelta) return null;
            return Math.Clamp(100.0 * (1 - (double)idleDelta / totalDelta), 0, 100);
        }

        /// <summary>
        /// 解析 /proc/stat 的 “cpu ” 汇总行：空闲 = idle + iowait；总数为前 8 列（user … steal），
        /// 不再累加 guest、guest_nice（它们已包含在 user、nice 中，W-17 ④）
        /// </summary>
        public static bool TryParseProcStatCpuLine(string line, out long idle, out long total)
        {
            idle = 0;
            total = 0;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5 || parts[0] != "cpu") return false;

            var values = parts.Skip(1).Take(8).Select(p => long.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : -1).ToArray();
            if (values.Any(v => v < 0)) return false;

            total = values.Sum();
            idle = values[3] + (values.Length > 4 ? values[4] : 0);
            return true;
        }

        /// <summary>解析 cgroup v2 的 cpu.stat 中的 usage_usec（容器累计使用的 CPU 时间，微秒）</summary>
        public static bool TryParseCpuStatUsageUsec(string content, out long usageUsec)
            => TryReadKeyedValue(content, "usage_usec", out usageUsec);

        /// <summary>
        /// 解析 cgroup v2 的 cpu.max（“配额 周期”或 “max 周期”），得到容器可用的核数（可以是小数）；
        /// 无限制时为 <paramref name="processorCount"/>
        /// </summary>
        public static double ParseEffectiveCores(string? cpuMax, int processorCount)
        {
            var fallback = Math.Max(1, processorCount);
            if (string.IsNullOrWhiteSpace(cpuMax)) return fallback;
            var parts = cpuMax.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[0] == "max") return fallback;
            if (long.TryParse(parts[0], out var quota) && long.TryParse(parts[1], out var period) && quota > 0 && period > 0)
            {
                return Math.Min(fallback, (double)quota / period);
            }
            return fallback;
        }

        /// <summary>
        /// 容器 CPU 使用率（%）：两次采样之间 usage_usec 的增量 ÷（经过的时间 × 可用核数）（W-17 ①）
        /// </summary>
        public static double? CgroupUsagePercent(long usageDeltaUsec, TimeSpan elapsed, double effectiveCores)
        {
            if (usageDeltaUsec < 0 || elapsed <= TimeSpan.Zero || effectiveCores <= 0) return null;
            var available = elapsed.TotalMilliseconds * 1000 * effectiveCores;
            return Math.Clamp(100.0 * usageDeltaUsec / available, 0, 100);
        }

        /// <summary>
        /// 容器内存使用量（字节）：memory.current 减去 memory.stat 中的 inactive_file（页缓存中可回收的部分），
        /// 与 docker stats 的口径一致（W-17 ②）
        /// </summary>
        public static long ContainerMemoryUsageBytes(long memoryCurrent, string? memoryStat)
        {
            if (memoryStat != null && TryReadKeyedValue(memoryStat, "inactive_file", out var inactiveFile))
            {
                return Math.Max(0, memoryCurrent - inactiveFile);
            }
            return memoryCurrent;
        }

        /// <summary>读取 “键 值” 格式文件中某个键的值</summary>
        private static bool TryReadKeyedValue(string content, string key, out long value)
        {
            value = 0;
            foreach (var line in content.Split('\n'))
            {
                var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[0] == key && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out value))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
