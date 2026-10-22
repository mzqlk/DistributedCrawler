using CrawlerCore.Infrastructure;
using CrawlerCore.Protos;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CrawlerWorker.Services
{
    /// <summary>
    /// 节点信息采集实现
    /// </summary>
    public class NodeInfoCollector : INodeInfoCollector
    {
        private readonly ILogger<NodeInfoCollector> _logger;
        private readonly IWorkerSessionManager _sessionManager;
        private readonly GrpcSettings _grpcSettings;
        private readonly CrawlerSettings _crawlerSettings;
        private readonly string _nodeId;

        // CPU 采样状态（两次采样差分；首次采样返回 0）
        private readonly object _cpuLock = new();
        private long _lastIdleTime;
        private long _lastTotalTime;
        private long _lastCgroupUsageUsec;
        private long _lastCgroupSampleTimestamp;
        private bool _isFirstCpuSample = true;

        // cgroup v2 与 /proc 的位置（Linux）
        private const string CgroupRoot = "/sys/fs/cgroup";
        private const string ProcStatPath = "/proc/stat";
        private int _platformWarningLogged;

        // 节点 ID 相关常量
        private const string NodeIdEnvVar = "MASTER_NODE_ID";
        private const int MaxNodeIdLength = 128;
        private static readonly Regex NodeIdValidCharRegex = new(@"[^a-zA-Z0-9_\-]", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

        public NodeInfoCollector(
            ILogger<NodeInfoCollector> logger,
            IWorkerSessionManager sessionManager,
            IOptions<GrpcSettings> grpcSettings,
            IOptions<CrawlerSettings> crawlerSettings)
        {
            _logger = logger;
            _sessionManager = sessionManager;
            _grpcSettings = grpcSettings.Value;
            _crawlerSettings = crawlerSettings.Value;
            _nodeId = GenerateNormalizedNodeId();
        }

        #region INodeInfoCollector 实现

        public string GetNodeId() => _nodeId;

        public async Task<NodeRegistration> CollectNodeRegistrationAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogDebug("开始采集节点注册信息");

            var machineName = Environment.MachineName;
            var cpuCores = GetCpuCoreCount();
            var totalMemoryGb = Math.Round(await GetTotalMemoryGbAsync(cancellationToken).ConfigureAwait(false), 2);
            var ipAddress = await GetLocalIpAddressAsync(cancellationToken).ConfigureAwait(false);

            var registration = new NodeRegistration
            {
                NodeId = _nodeId,
                MachineName = machineName,
                CpuCores = cpuCores,
                TotalMemoryGb = totalMemoryGb,
                IpAddress = ipAddress,
                // 告诉 Master 本节点同时执行的任务数上限，Master 不会分配超过这个数的任务（W-22、M-06）
                MaxConcurrentTasks = Math.Max(1, _crawlerSettings.MaxConcurrentTasks)
            };

            _logger.LogInformation("节点注册信息采集完成: NodeId={NodeId}, MachineName={MachineName}, Cores={Cores}, Memory={Memory}GB, IP={IP}, 并发上限={MaxConcurrentTasks}",
                _nodeId, machineName, cpuCores, totalMemoryGb, ipAddress, registration.MaxConcurrentTasks);

            return registration;
        }

        public async Task<Heartbeat> CollectHeartbeatAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogDebug("开始采集节点心跳数据");

            var utcNow = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc);
            var heartbeat = new Heartbeat
            {
                NodeId = _nodeId,
                CpuUsage = Math.Round(await GetSystemCpuUsageAsync().ConfigureAwait(false), 2),
                MemoryUsageMb = Math.Round(await GetSystemMemoryUsageMbAsync(cancellationToken).ConfigureAwait(false), 2),
                DiskUsagePercent = Math.Round(GetDiskUsagePercent(), 2),
                ActiveTasks = _sessionManager.ActiveTaskCount,
                Timestamp = Timestamp.FromDateTime(utcNow)
            };

            _logger.LogDebug("节点心跳数据采集完成: NodeId={NodeId}, CPU={CpuUsage}%, Memory={MemoryUsageMb}MB, Disk={DiskUsagePercent}%",
                _nodeId, heartbeat.CpuUsage, heartbeat.MemoryUsageMb, heartbeat.DiskUsagePercent);

            return heartbeat;
        }

        #endregion

        #region 节点 ID 规范化

        private string GenerateNormalizedNodeId()
        {
            string rawNodeId;

            // 优先从环境变量读取 (Docker/K8s 注入)
            var envNodeId = Environment.GetEnvironmentVariable(NodeIdEnvVar);
            if (!string.IsNullOrWhiteSpace(envNodeId))
            {
                rawNodeId = envNodeId.Trim();
                _logger.LogDebug("使用环境变量注入的原始节点ID: {RawNodeId}", rawNodeId);
            }
            else
            {
                rawNodeId = Environment.MachineName;
                _logger.LogDebug("使用机器名作为原始节点ID: {RawNodeId}", rawNodeId);
            }

            // 过滤非法字符，限制最大长度
            var cleanedNodeId = NodeIdValidCharRegex.Replace(rawNodeId, "_");
            var finalNodeId = cleanedNodeId.Length > MaxNodeIdLength
                ? cleanedNodeId[..MaxNodeIdLength]
                : cleanedNodeId;

            if (finalNodeId != rawNodeId)
            {
                _logger.LogWarning("节点ID已规范化，原始: {RawNodeId}，最终: {FinalNodeId}", rawNodeId, finalNodeId);
            }

            return finalNodeId;
        }

        #endregion

        #region CPU 核心数

        private int GetCpuCoreCount()
        {
            try
            {
                // Linux 容器环境：读取 cgroup v2 CPU 限制
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && File.Exists("/sys/fs/cgroup/cpu.max"))
                {
                    var cpuMaxContent = File.ReadAllText("/sys/fs/cgroup/cpu.max").Trim();
                    var parts = cpuMaxContent.Split(' ');
                    if (parts.Length >= 2 && long.TryParse(parts[1], out var cpuPeriod) && cpuPeriod > 0)
                    {
                        // "max" 表示无限制
                        if (parts[0] == "max")
                        {
                            _logger.LogDebug("容器 CPU 无限制，使用系统核心数: {CoreCount}", Environment.ProcessorCount);
                            return Environment.ProcessorCount;
                        }

                        if (long.TryParse(parts[0], out var cpuQuota))
                        {
                            var coreCount = (int)Math.Max(1, Math.Ceiling((double)cpuQuota / cpuPeriod));
                            _logger.LogDebug("容器环境 CPU 核心数 (cgroup v2): {CoreCount}", coreCount);
                            return coreCount;
                        }
                    }
                }

                // 回退到系统核心数
                var systemCoreCount = Environment.ProcessorCount;
                _logger.LogDebug("系统 CPU 核心数: {CoreCount}", systemCoreCount);
                return systemCoreCount;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CPU 核心数采集异常，使用默认值");
                return Math.Max(1, Environment.ProcessorCount);
            }
        }

        #endregion

        #region 总内存

        private async Task<double> GetTotalMemoryGbAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Linux 容器环境：cgroup v2 内存限制
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && File.Exists("/sys/fs/cgroup/memory.max"))
                {
                    var memoryMaxContent = await File.ReadAllTextAsync("/sys/fs/cgroup/memory.max", cancellationToken).ConfigureAwait(false);
                    memoryMaxContent = memoryMaxContent.Trim();

                    // 处理 "max" 无限制情况：返回系统物理内存
                    if (memoryMaxContent == "max")
                    {
                        _logger.LogDebug("容器内存无限制，尝试读取系统物理内存");
                        return await GetSystemPhysicalMemoryGbAsync(cancellationToken).ConfigureAwait(false);
                    }

                    if (long.TryParse(memoryMaxContent, out var memoryBytes) && memoryBytes > 0)
                    {
                        var memoryGb = (double)memoryBytes / 1024 / 1024 / 1024;
                        _logger.LogDebug("容器环境总内存 (cgroup v2): {MemoryGb:F2} GB", memoryGb);
                        return memoryGb;
                    }
                }

                // 物理机 / 无 cgroup 限制：返回系统总内存
                return await GetSystemPhysicalMemoryGbAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "总内存采集异常，返回 0");
                return 0;
            }
        }

        private async Task<double> GetSystemPhysicalMemoryGbAsync(CancellationToken cancellationToken)
        {
            // Linux 物理机/虚拟机：读取 /proc/meminfo
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && File.Exists("/proc/meminfo"))
            {
                var memInfoLines = await File.ReadAllLinesAsync("/proc/meminfo", cancellationToken).ConfigureAwait(false);
                var totalMemLine = memInfoLines.FirstOrDefault(l => l.StartsWith("MemTotal:", StringComparison.OrdinalIgnoreCase));
                if (totalMemLine != null)
                {
                    var parts = totalMemLine.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && long.TryParse(parts[1], out var memoryKb))
                    {
                        var memoryGb = memoryKb / 1024.0 / 1024.0;
                        _logger.LogDebug("Linux 系统总内存: {MemoryGb:F2} GB", memoryGb);
                        return memoryGb;
                    }
                }
            }

            // Windows 环境：调用 GlobalMemoryStatusEx
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var memoryStatus = new MEMORYSTATUSEX();
                memoryStatus.dwLength = (uint)Marshal.SizeOf(memoryStatus);
                if (GlobalMemoryStatusEx(ref memoryStatus))
                {
                    var memoryGb = (double)memoryStatus.ullTotalPhys / 1024 / 1024 / 1024;
                    _logger.LogDebug("Windows 系统总内存: {MemoryGb:F2} GB", memoryGb);
                    return memoryGb;
                }
            }

            _logger.LogWarning("无法获取系统总内存，返回 0");
            return 0;
        }

        #endregion

        #region IP 地址采集

        private async Task<string> GetLocalIpAddressAsync(CancellationToken cancellationToken)
        {
            try
            {
                // 通过实际使用的 Master 端点（GrpcSettings:MasterEndpoint）获取出口 IP（W-13）
                var masterUri = new Uri(_grpcSettings.MasterEndpoint);

                using var udpClient = new UdpClient();
                udpClient.Connect(masterUri.Host, masterUri.Port);
                var localEndPoint = udpClient.Client.LocalEndPoint as IPEndPoint;

                if (localEndPoint != null && !IPAddress.IsLoopback(localEndPoint.Address))
                {
                    var ipAddress = localEndPoint.Address.ToString();
                    _logger.LogDebug("通过 Master 端点获取出口 IP: {IpAddress}", ipAddress);
                    return ipAddress;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "通过 Master 端点获取 IP 失败，尝试降级方案");
            }

            try
            {
                // 获取第一个非环回 IPv4 地址
                var hostEntry = await Dns.GetHostEntryAsync(Dns.GetHostName(), AddressFamily.InterNetwork, cancellationToken).ConfigureAwait(false);
                var ipAddressFallback = hostEntry.AddressList
                    .FirstOrDefault(ip => !IPAddress.IsLoopback(ip))?.ToString();

                if (!string.IsNullOrEmpty(ipAddressFallback))
                {
                    _logger.LogDebug("通过 DNS 获取本机 IP: {IpAddress}", ipAddressFallback);
                    return ipAddressFallback;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "DNS 获取 IP 失败，使用环回地址");
            }

            _logger.LogWarning("所有 IP 采集策略均失败，使用 127.0.0.1");
            return "127.0.0.1";
        }

        #endregion

        #region CPU 使用率

        /// <summary>
        /// CPU 使用率（%）：
        /// <list type="bullet">
        /// <item>Windows：GetSystemTimes 的两次采样差分（W-16：原来按处理器查询的结构体布局错误，始终返回 0）。</item>
        /// <item>Linux 且有 cgroup v2 的 cpu.stat：容器（或整机根 cgroup）的 usage_usec 增量 ÷（时间 × 可用核数），
        /// 受容器 CPU 配额限制（W-17 ①）。</item>
        /// <item>其他 Linux：/proc/stat 的整机数据（cgroup v1 时启动时记一次警告）。</item>
        /// </list>
        /// </summary>
        private async Task<double> GetSystemCpuUsageAsync()
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    if (!GetSystemTimes(out var idle, out var kernel, out var user))
                    {
                        _logger.LogWarning("GetSystemTimes 调用失败，错误码: {Error}", Marshal.GetLastWin32Error());
                        return 0;
                    }
                    // kernel 时间包含 idle
                    return SampleIdleTotal(ToLong(idle), ToLong(kernel) + ToLong(user));
                }

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    var cpuStatPath = Path.Combine(CgroupRoot, "cpu.stat");
                    if (File.Exists(cpuStatPath))
                    {
                        var cpuStat = await File.ReadAllTextAsync(cpuStatPath).ConfigureAwait(false);
                        if (ResourceUsageCalculator.TryParseCpuStatUsageUsec(cpuStat, out var usageUsec))
                        {
                            var cpuMaxPath = Path.Combine(CgroupRoot, "cpu.max");
                            var cpuMax = File.Exists(cpuMaxPath) ? await File.ReadAllTextAsync(cpuMaxPath).ConfigureAwait(false) : null;
                            return SampleCgroupUsage(usageUsec, ResourceUsageCalculator.ParseEffectiveCores(cpuMax, Environment.ProcessorCount));
                        }
                    }

                    WarnOnceAboutPlatform("未找到 cgroup v2 的 cpu.stat（可能是 cgroup v1），CPU 与内存按整机（宿主机）口径上报");
                    var statLines = await File.ReadAllLinesAsync(ProcStatPath).ConfigureAwait(false);
                    var cpuLine = statLines.FirstOrDefault(l => l.StartsWith("cpu ", StringComparison.Ordinal));
                    if (cpuLine != null && ResourceUsageCalculator.TryParseProcStatCpuLine(cpuLine, out var procIdle, out var procTotal))
                    {
                        return SampleIdleTotal(procIdle, procTotal);
                    }
                    return 0;
                }

                WarnOnceAboutPlatform($"当前平台（{RuntimeInformation.OSDescription}）不支持采集 CPU 与内存使用量，按 0 上报");
                return 0;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CPU 使用率采集失败，返回 0");
                return 0;
            }
        }

        private double SampleIdleTotal(long idle, long total)
        {
            lock (_cpuLock)
            {
                var first = _isFirstCpuSample;
                var usage = ResourceUsageCalculator.UsagePercent(idle - _lastIdleTime, total - _lastTotalTime);
                _lastIdleTime = idle;
                _lastTotalTime = total;
                _isFirstCpuSample = false;
                return first ? 0 : Math.Round(usage ?? 0, 2);
            }
        }

        private double SampleCgroupUsage(long usageUsec, double effectiveCores)
        {
            lock (_cpuLock)
            {
                var now = System.Diagnostics.Stopwatch.GetTimestamp();
                var first = _isFirstCpuSample;
                var usage = ResourceUsageCalculator.CgroupUsagePercent(
                    usageUsec - _lastCgroupUsageUsec,
                    System.Diagnostics.Stopwatch.GetElapsedTime(_lastCgroupSampleTimestamp, now),
                    effectiveCores);
                _lastCgroupUsageUsec = usageUsec;
                _lastCgroupSampleTimestamp = now;
                _isFirstCpuSample = false;
                return first ? 0 : Math.Round(usage ?? 0, 2);
            }
        }

        private static long ToLong(FILETIME time) => ((long)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime;

        private void WarnOnceAboutPlatform(string message)
        {
            if (Interlocked.Exchange(ref _platformWarningLogged, 1) == 0)
            {
                _logger.LogWarning("{Message}", message);
            }
        }

        #endregion

        #region 内存使用量

        /// <summary>
        /// 内存使用量（MB）：容器用 memory.current 减去 inactive_file（与 docker stats 一致，W-17 ②）；
        /// Linux 整机用 MemTotal − MemAvailable；Windows 上报整机已用物理内存（既定口径，见 design.md）
        /// </summary>
        private async Task<double> GetSystemMemoryUsageMbAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    var currentPath = Path.Combine(CgroupRoot, "memory.current");
                    if (File.Exists(currentPath))
                    {
                        var currentStr = await File.ReadAllTextAsync(currentPath, cancellationToken).ConfigureAwait(false);
                        if (long.TryParse(currentStr.Trim(), out var currentBytes))
                        {
                            var statPath = Path.Combine(CgroupRoot, "memory.stat");
                            var memoryStat = File.Exists(statPath) ? await File.ReadAllTextAsync(statPath, cancellationToken).ConfigureAwait(false) : null;
                            return ResourceUsageCalculator.ContainerMemoryUsageBytes(currentBytes, memoryStat) / 1024.0 / 1024.0;
                        }
                    }

                    var memInfo = await File.ReadAllLinesAsync("/proc/meminfo", cancellationToken).ConfigureAwait(false);
                    var totalLine = memInfo.FirstOrDefault(l => l.StartsWith("MemTotal:", StringComparison.OrdinalIgnoreCase));
                    var availableLine = memInfo.FirstOrDefault(l => l.StartsWith("MemAvailable:", StringComparison.OrdinalIgnoreCase));
                    if (totalLine != null && availableLine != null)
                    {
                        return (ExtractKbValue(totalLine) - ExtractKbValue(availableLine)) / 1024.0;
                    }
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    var memoryStatus = new MEMORYSTATUSEX();
                    memoryStatus.dwLength = (uint)Marshal.SizeOf(memoryStatus);
                    if (GlobalMemoryStatusEx(ref memoryStatus))
                    {
                        return (memoryStatus.ullTotalPhys - memoryStatus.ullAvailPhys) / 1024.0 / 1024.0;
                    }
                }

                return 0;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "内存使用量采集失败，返回 0");
                return 0;
            }
        }

        private static long ExtractKbValue(string line)
        {
            var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 && long.TryParse(parts[1], out var value) ? value : 0;
        }

        #endregion


        #region 磁盘使用率

        private double GetDiskUsagePercent()
        {
            try
            {
                // 获取当前应用程序所在目录的根驱动器
                var appDirectory = AppContext.BaseDirectory;
                var rootPath = Path.GetPathRoot(appDirectory);

                if (string.IsNullOrEmpty(rootPath))
                {
                    // 如果获取失败，回退到当前工作目录
                    rootPath = Path.GetPathRoot(Directory.GetCurrentDirectory());
                }

                if (string.IsNullOrEmpty(rootPath))
                {
                    _logger.LogWarning("无法确定当前驱动器根路径");
                    return 0;
                }

                var drive = new DriveInfo(rootPath);

                if (!drive.IsReady)
                {
                    _logger.LogWarning("磁盘 {DriveName} 未就绪", drive.Name);
                    return 0;
                }

                var totalSpace = drive.TotalSize;
                var freeSpace = drive.AvailableFreeSpace;
                var usedSpace = totalSpace - freeSpace;

                var usagePercent = 100.0 * usedSpace / totalSpace;
                _logger.LogDebug("磁盘 {DriveName} 使用率: {UsagePercent:F2}%", drive.Name, usagePercent);
                return usagePercent;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "磁盘使用率采集失败，返回 0");
                return 0;
            }
        }

        #endregion

        #region Windows 原生 API 导入

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME
        {
            public int dwLowDateTime;
            public int dwHighDateTime;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

        #endregion
    }
}