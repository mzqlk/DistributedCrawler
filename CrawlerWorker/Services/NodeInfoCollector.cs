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

        // CPU 采样状态
        private readonly object _cpuLock = new();
        private DateTime _lastCpuCheckTime = DateTime.UtcNow;
        private long _lastIdleTime;
        private long _lastTotalTime;
        private bool _isFirstCpuSample = true;

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

        private async Task<double> GetSystemCpuUsageAsync()
        {
            try
            {
                long currentIdleTime = 0;
                long currentTotalTime = 0;

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    // Linux: 读取 /proc/stat
                    var statLines = await File.ReadAllLinesAsync("/proc/stat").ConfigureAwait(false);
                    var cpuLine = statLines.FirstOrDefault(l => l.StartsWith("cpu ", StringComparison.OrdinalIgnoreCase));
                    if (cpuLine != null)
                    {
                        var parts = cpuLine.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 8)
                        {
                            long total = 0;
                            for (int i = 1; i < parts.Length; i++)
                            {
                                if (long.TryParse(parts[i], out var value))
                                {
                                    total += value;
                                }
                            }
                            // Linux: idle = idle + iowait (第4和第5个字段，索引 4 和 5)
                            currentIdleTime = (long.TryParse(parts[4], out var idle) ? idle : 0) +
                                              (long.TryParse(parts[5], out var iowait) ? iowait : 0);
                            currentTotalTime = total;
                        }
                    }
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // Windows: 使用 NtQuerySystemInformation 获取每个处理器的性能数据
                    int processorCount = Environment.ProcessorCount;
                    int structSize = Marshal.SizeOf<SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>();
                    int bufferSize = structSize * processorCount;
                    IntPtr buffer = Marshal.AllocHGlobal(bufferSize);

                    try
                    {
                        int returnLength = 0;
                        var status = NtQuerySystemInformation(
                            SYSTEM_INFORMATION_CLASS.SystemProcessorPerformanceInformation,
                            buffer,
                            bufferSize,
                            ref returnLength);

                        if (status == 0)
                        {
                            long totalIdleTime = 0;
                            long totalKernelTime = 0;
                            long totalUserTime = 0;

                            for (int i = 0; i < processorCount; i++)
                            {
                                IntPtr currentStruct = buffer + i * structSize;
                                var perfInfo = Marshal.PtrToStructure<SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>(currentStruct);
                                totalIdleTime += perfInfo.IdleTime;
                                totalKernelTime += perfInfo.KernelTime;
                                totalUserTime += perfInfo.UserTime;
                            }

                            currentIdleTime = totalIdleTime;
                            currentTotalTime = totalKernelTime + totalUserTime;
                        }
                        else
                        {
                            _logger.LogWarning("NtQuerySystemInformation 调用失败，状态码: {Status}", status);
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }

                // 采样计算
                lock (_cpuLock)
                {
                    if (_isFirstCpuSample)
                    {
                        _lastCpuCheckTime = DateTime.UtcNow;
                        _lastIdleTime = currentIdleTime;
                        _lastTotalTime = currentTotalTime;
                        _isFirstCpuSample = false;
                        return 0;
                    }

                    var timeDelta = DateTime.UtcNow - _lastCpuCheckTime;
                    var idleDelta = currentIdleTime - _lastIdleTime;
                    var totalDelta = currentTotalTime - _lastTotalTime;

                    // 处理时间回退或异常数据
                    if (timeDelta.TotalMilliseconds <= 0 || totalDelta <= 0 || idleDelta < 0)
                    {
                        _logger.LogDebug("检测到时间回退或计数器异常，重置 CPU 采样状态");
                        _lastCpuCheckTime = DateTime.UtcNow;
                        _lastIdleTime = currentIdleTime;
                        _lastTotalTime = currentTotalTime;
                        return 0;
                    }

                    // 更新历史值
                    _lastCpuCheckTime = DateTime.UtcNow;
                    _lastIdleTime = currentIdleTime;
                    _lastTotalTime = currentTotalTime;

                    var cpuUsage = 100.0 * (1 - (double)idleDelta / totalDelta);
                    return Math.Clamp(cpuUsage, 0, 100);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CPU 使用率采集失败，返回 0");
                return 0;
            }
        }

        #endregion

        #region 内存使用量

        private async Task<double> GetSystemMemoryUsageMbAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    // 容器环境：优先使用 cgroup v2 内存统计
                    if (File.Exists("/sys/fs/cgroup/memory.current") && File.Exists("/sys/fs/cgroup/memory.max"))
                    {
                        var currentStr = await File.ReadAllTextAsync("/sys/fs/cgroup/memory.current", cancellationToken).ConfigureAwait(false);
                        var maxStr = await File.ReadAllTextAsync("/sys/fs/cgroup/memory.max", cancellationToken).ConfigureAwait(false);

                        if (long.TryParse(currentStr.Trim(), out var currentBytes))
                        {
                            // 处理 "max" 情况：使用系统物理内存作为总内存参考
                            if (maxStr.Trim() == "max")
                            {
                                var systemTotalGb = await GetSystemPhysicalMemoryGbAsync(cancellationToken).ConfigureAwait(false);
                                var systemTotalBytes = (long)(systemTotalGb * 1024 * 1024 * 1024);
                                var usedMb = (double)currentBytes / 1024 / 1024;
                                _logger.LogDebug("容器环境内存使用 (cgroup v2, 无限制): {UsedMb:F2} MB", usedMb);
                                return usedMb;
                            }

                            if (long.TryParse(maxStr.Trim(), out var maxBytes) && maxBytes > 0)
                            {
                                var usedMb = (double)currentBytes / 1024 / 1024;
                                _logger.LogDebug("容器环境内存使用 (cgroup v2): {UsedMb:F2} MB", usedMb);
                                return usedMb;
                            }
                        }
                    }

                    // 物理机：读取 /proc/meminfo
                    var memInfo = await File.ReadAllLinesAsync("/proc/meminfo", cancellationToken).ConfigureAwait(false);
                    var totalLine = memInfo.FirstOrDefault(l => l.StartsWith("MemTotal:", StringComparison.OrdinalIgnoreCase));
                    var availableLine = memInfo.FirstOrDefault(l => l.StartsWith("MemAvailable:", StringComparison.OrdinalIgnoreCase));

                    if (totalLine != null && availableLine != null)
                    {
                        var totalKb = ExtractKbValue(totalLine);
                        var availableKb = ExtractKbValue(availableLine);
                        var usedKb = totalKb - availableKb;
                        var usedMb = usedKb / 1024.0;
                        _logger.LogDebug("Linux 系统内存使用: {UsedMb:F2} MB", usedMb);
                        return usedMb;
                    }
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    var memoryStatus = new MEMORYSTATUSEX();
                    memoryStatus.dwLength = (uint)Marshal.SizeOf(memoryStatus);
                    if (GlobalMemoryStatusEx(ref memoryStatus))
                    {
                        var usedMemory = memoryStatus.ullTotalPhys - memoryStatus.ullAvailPhys;
                        var usedMb = usedMemory / 1024.0 / 1024.0;
                        _logger.LogDebug("Windows 系统内存使用: {UsedMb:F2} MB", usedMb);
                        return usedMb;
                    }
                }

                _logger.LogWarning("无法获取内存使用量，返回 0");
                return 0;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "内存使用量采集失败，返回 0");
                return 0;
            }
        }

        private long ExtractKbValue(string line)
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

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION
        {
            public long IdleTime;
            public long KernelTime;
            public long UserTime;
            public long DpcTime;
            public long InterruptTime;
            public uint InterruptCount;
        }

        private enum SYSTEM_INFORMATION_CLASS
        {
            SystemProcessorPerformanceInformation = 8
        }

        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(
            SYSTEM_INFORMATION_CLASS SystemInformationClass,
            IntPtr SystemInformation,
            int SystemInformationLength,
            ref int ReturnLength);

        #endregion
    }
}