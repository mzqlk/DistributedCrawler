namespace CrawlerCore.Infrastructure
{
    /// <summary>
    /// gRPC配置项
    /// </summary>
    public class GrpcSettings
    {
        public const string SectionName = "GrpcSettings";

        public string MasterEndpoint { get; set; } = "https://localhost:5001";
        public string WorkerEndpoint { get; set; } = "https://localhost:5002";
        public int MaxRetryAttempts { get; set; } = 3;
        public int RetryDelayMilliseconds { get; set; } = 1000;
        public int HeartbeatIntervalSeconds { get; set; } = 5;
        public int ConnectionIdleTimeoutMinutes { get; set; } = 30;
        public int MaxMessageSizeMb { get; set; } = 64;
        public string NodeAuthToken { get; set; } = string.Empty;

        /// <summary>
        /// Master（Kestrel）在连接上这么久（秒）没有收到任何帧时发送 HTTP/2 PING，用于发现半开连接。默认 15
        /// </summary>
        public int KeepAlivePingDelaySeconds { get; set; } = 15;

        /// <summary>
        /// 发送 PING 后这么久（秒）没有回应就关闭连接。默认 30
        /// </summary>
        public int KeepAlivePingTimeoutSeconds { get; set; } = 30;
    }
}