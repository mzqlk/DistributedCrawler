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