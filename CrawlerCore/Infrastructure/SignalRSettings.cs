namespace CrawlerCore.Infrastructure;

/// <summary>
/// SignalR 实时通信强类型配置
/// </summary>
public class SignalRSettings
{
    public const string SectionName = "SignalRSettings";

    /// <summary>
    /// SignalR 服务监听端口
    /// </summary>
    public int Port { get; set; } = 5003;

    /// <summary>
    /// SignalR 监听地址，默认只监听本机环回地址 127.0.0.1；
    /// 需要让其他机器访问时改为具体网卡地址，或 0.0.0.0（所有网卡）
    /// </summary>
    public string ListenAddress { get; set; } = "127.0.0.1";

    /// <summary>
    /// 允许跨域访问 Hub 的来源白名单（如 http://localhost:5173）。默认为空，即不允许任何跨域来源
    /// </summary>
    public string[] AllowedOrigins { get; set; } = [];

    /// <summary>
    /// Hub 路由地址
    /// </summary>
    public string HubPath { get; set; } = "/crawlerHub";

    /// <summary>
    /// 节点状态推送节流间隔（秒）
    /// </summary>
    public int NodeUpdateThrottleSeconds { get; set; } = 5;

    /// <summary>
    /// 任务进度推送节流间隔（秒）
    /// </summary>
    public int TaskProgressThrottleSeconds { get; set; } = 2;
}