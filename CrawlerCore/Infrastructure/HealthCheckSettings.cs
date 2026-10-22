namespace CrawlerCore.Infrastructure;

/// <summary>
/// Master 节点健康检查配置项
/// </summary>
public class HealthCheckSettings
{
    public const string SectionName = "HealthCheckSettings";

    /// <summary>
    /// 健康检查间隔（秒），默认 15
    /// </summary>
    public int CheckIntervalSeconds { get; set; } = 15;

    /// <summary>
    /// 超过该时长（秒）没有心跳的在线节点判为离线，默认 60
    /// </summary>
    public int NodeTimeoutSeconds { get; set; } = 60;
}
