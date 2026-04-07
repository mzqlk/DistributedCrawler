using CrawlerCore.Protos;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace CrawlerWorker.Services;

public class NodeInfoCollector : INodeInfoCollector
{
    private readonly ILogger<NodeInfoCollector> _logger;
    private const string NodeIdEnvVar = "MASTER_NODE_ID";
    private const int MaxNodeIdLength = 128;
    // 【关键修复】NodeId合法字符正则，仅保留字母、数字、下划线、连字符，确保主键兼容
    private static readonly Regex NodeIdValidCharRegex = new(@"[^a-zA-Z0-9_\-]", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    public NodeInfoCollector(ILogger<NodeInfoCollector> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 严格对齐原项目节点ID规则
    /// 优先读取环境变量，Docker环境可注入固定值；默认使用机器名
    /// 【关键修复】增加非法字符过滤、长度限制，确保全链路主键唯一
    /// </summary>
    public string GetNodeId()
    {
        string rawNodeId;
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
        var finalNodeId = cleanedNodeId[..Math.Min(cleanedNodeId.Length, MaxNodeIdLength)];

        if (finalNodeId != rawNodeId)
        {
            _logger.LogWarning("节点ID已规范化，原始: {RawNodeId}，最终: {FinalNodeId}", rawNodeId, finalNodeId);
        }

        return finalNodeId;
    }

    /// <summary>
    /// 采集完整节点注册信息，严格匹配proto定义，无字段遗漏
    /// 【关键修复】IP采集多层兜底，Master不可达时不会导致采集失败
    /// </summary>
    public async Task<NodeRegistration> CollectNodeRegistrationAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("开始采集节点注册信息");
        var nodeRegistration = new NodeRegistration
        {
            NodeId = GetNodeId(),
            MachineName = Environment.MachineName,
            CpuCores = GetCpuCoreCount(),
            TotalMemoryGb = await GetTotalMemoryGbAsync(cancellationToken).ConfigureAwait(false),
            IpAddress = await GetLocalIpAddressAsync(cancellationToken).ConfigureAwait(false)
        };

        _logger.LogInformation("节点信息采集完成: {@NodeInfo}", new
        {
            nodeRegistration.NodeId,
            nodeRegistration.MachineName,
            nodeRegistration.CpuCores,
            nodeRegistration.TotalMemoryGb,
            nodeRegistration.IpAddress
        });

        return nodeRegistration;
    }

    /// <summary>
    /// 获取CPU核心数，适配Docker容器cgroup限制
    /// </summary>
    private int GetCpuCoreCount()
    {
        try
        {
            // Linux容器环境：读取cgroup v2 CPU限制
            if (File.Exists("/sys/fs/cgroup/cpu.max"))
            {
                var cpuMaxContent = File.ReadAllText("/sys/fs/cgroup/cpu.max").Trim();
                var parts = cpuMaxContent.Split(' ');
                if (parts.Length >= 2 && int.TryParse(parts[0], out var cpuQuota) && int.TryParse(parts[1], out var cpuPeriod) && cpuPeriod > 0)
                {
                    var coreCount = Math.Max(1, (int)Math.Ceiling((double)cpuQuota / cpuPeriod));
                    _logger.LogDebug("容器环境CPU核心数: {CoreCount}", coreCount);
                    return coreCount;
                }
            }

            // 全平台通用：读取系统CPU核心数
            var systemCoreCount = Environment.ProcessorCount;
            _logger.LogDebug("系统CPU核心数: {CoreCount}", systemCoreCount);
            return systemCoreCount;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CPU核心数采集异常，使用默认值");
            return Math.Max(1, Environment.ProcessorCount);
        }
    }

    /// <summary>
    /// 获取总内存大小，全平台兼容，严格平台隔离
    /// </summary>
    private async Task<double> GetTotalMemoryGbAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Linux容器/系统环境：读取cgroup v2内存限制
            if (File.Exists("/sys/fs/cgroup/memory.max"))
            {
                var memoryMaxContent = await File.ReadAllTextAsync("/sys/fs/cgroup/memory.max", cancellationToken).ConfigureAwait(false);
                memoryMaxContent = memoryMaxContent.Trim();
                if (long.TryParse(memoryMaxContent, out var memoryBytes) && memoryBytes > 0)
                {
                    var memoryGb = Math.Round((double)memoryBytes / 1024 / 1024 / 1024, 2);
                    _logger.LogDebug("容器环境总内存: {MemoryGb} GB", memoryGb);
                    return memoryGb;
                }
            }

            // Linux非容器环境：读取/proc/meminfo
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && File.Exists("/proc/meminfo"))
            {
                var memInfoLines = await File.ReadAllLinesAsync("/proc/meminfo", cancellationToken).ConfigureAwait(false);
                var totalMemLine = memInfoLines.FirstOrDefault(l => l.StartsWith("MemTotal:", StringComparison.OrdinalIgnoreCase));
                if (totalMemLine != null)
                {
                    var parts = totalMemLine.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && long.TryParse(parts[1], out var memoryKb))
                    {
                        var memoryGb = Math.Round((double)memoryKb / 1024 / 1024, 2);
                        _logger.LogDebug("系统总内存: {MemoryGb} GB", memoryGb);
                        return memoryGb;
                    }
                }
            }

            // Windows环境：仅在Windows平台调用Win32 API，严格隔离
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var memoryStatus = new MEMORYSTATUSEX();
                memoryStatus.dwLength = (uint)Marshal.SizeOf(memoryStatus);
                if (GlobalMemoryStatusEx(ref memoryStatus))
                {
                    var memoryGb = Math.Round((double)memoryStatus.ullTotalPhys / 1024 / 1024 / 1024, 2);
                    _logger.LogDebug("Windows系统总内存: {MemoryGb} GB", memoryGb);
                    return memoryGb;
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "总内存采集异常，使用默认值");
            return 0;
        }
    }

    /// <summary>
    /// 【关键修复】多层兜底IP采集，Master不可达时也能正常获取IP，不会导致进程启动失败
    /// </summary>
    private async Task<string> GetLocalIpAddressAsync(CancellationToken cancellationToken)
    {
        try
        {
            // 优先获取与Master通信的出口IP，确保可访问
            var masterEndpoint = Environment.GetEnvironmentVariable("MASTER_ENDPOINT") ?? "http://localhost:5001";
            var masterUri = new Uri(masterEndpoint);

            using var udpClient = new UdpClient();
            // UdpClient 没有接受 CancellationToken 的 ConnectAsync，改为同步 Connect
            udpClient.Connect(masterUri.Host, masterUri.Port);
            var localEndPoint = udpClient.Client.LocalEndPoint as IPEndPoint;

            if (localEndPoint != null && !IPAddress.IsLoopback(localEndPoint.Address))
            {
                var ipAddress = localEndPoint.Address.ToString();
                _logger.LogDebug("获取到出口IP地址: {IpAddress}", ipAddress);
                return ipAddress;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "出口IP采集失败，降级到主机IP采集");
        }

        try
        {
            // 兜底：获取第一个非环回IPv4地址
            var hostEntry = await Dns.GetHostEntryAsync(Dns.GetHostName(), AddressFamily.InterNetwork, cancellationToken).ConfigureAwait(false);
            var ipAddressFallback = hostEntry.AddressList
                .FirstOrDefault(ip => !IPAddress.IsLoopback(ip))?.ToString() ?? "127.0.0.1";

            _logger.LogDebug("使用兜底IP地址: {IpAddress}", ipAddressFallback);
            return ipAddressFallback;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "主机IP采集失败，使用环回地址");
            return "127.0.0.1";
        }
    }

    #region Windows Win32 API 内存采集，严格平台隔离
    [StructLayout(LayoutKind.Sequential)]
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
    #endregion
}