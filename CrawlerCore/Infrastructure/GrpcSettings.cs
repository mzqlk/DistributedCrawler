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
    }
}