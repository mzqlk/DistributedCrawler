namespace CrawlerCore.Infrastructure
{
    /// <summary>
    /// gRPC配置项
    /// </summary>
    public class GrpcSettings
    {
        public const string SectionName = "GrpcSettings";

        /// <summary>
        /// Master 的 gRPC 地址。Master 只提供明文 HTTP/2，默认值用 http（W-13）
        /// </summary>
        public string MasterEndpoint { get; set; } = "http://localhost:5001";
        public string WorkerEndpoint { get; set; } = "http://localhost:5002";

        /// <summary>
        /// Worker：双向流连续失败这么多次后清除会话、重新注册（W-02）
        /// </summary>
        public int MaxRetryAttempts { get; set; } = 3;

        /// <summary>
        /// Worker：重连与重新注册的初始退避间隔（毫秒），之后每次失败翻倍，上限见 <see cref="MaxRetryDelaySeconds"/>
        /// </summary>
        public int RetryDelayMilliseconds { get; set; } = 1000;

        /// <summary>
        /// Worker：重连退避间隔的上限，秒（W-02）
        /// </summary>
        public int MaxRetryDelaySeconds { get; set; } = 30;

        /// <summary>
        /// Worker：注册时等待 Master 响应、建流后等待 Master 确认（响应头）的超时，秒。超时视为这次尝试失败，退避后重试（W-02）
        /// </summary>
        public int MasterResponseTimeoutSeconds { get; set; } = 10;

        /// <summary>
        /// Worker：结果发出后这么久（秒）没有收到 Master 的回执就重发（W-03）
        /// </summary>
        public int ResultAckTimeoutSeconds { get; set; } = 60;

        public int HeartbeatIntervalSeconds { get; set; } = 5;
        public int ConnectionIdleTimeoutMinutes { get; set; } = 30;
        public int MaxMessageSizeMb { get; set; } = 64;
        public string NodeAuthToken { get; set; } = string.Empty;

        /// <summary>
        /// 连接上这么久（秒）没有收到任何帧时发送 HTTP/2 PING，用于发现半开连接。
        /// Master（Kestrel）与 Worker（客户端，W-12）都使用。默认 15
        /// </summary>
        public int KeepAlivePingDelaySeconds { get; set; } = 15;

        /// <summary>
        /// 发送 PING 后这么久（秒）没有回应就关闭连接。默认 30
        /// </summary>
        public int KeepAlivePingTimeoutSeconds { get; set; } = 30;
    }
}
