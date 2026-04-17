namespace CrawlerCore.Infrastructure.Constants
{
    /// <summary>
    /// gRPC相关常量
    /// </summary>
    public static class GrpcConstants
    {
        public const int DefaultMaxMessageSizeMb = 64;
        public const int DefaultHeartbeatIntervalSeconds = 5;
        public const int DefaultMaxRetryAttempts = 5;
        public const int DefaultRetryDelayMilliseconds = 2000;
        public const int DefaultConnectionIdleTimeoutMinutes = 5;
        public const int DefaultTcpConnectTimeoutMs = 5000;
        public const int DefaultStreamCloseTimeoutMs = 2000;
        public const int DefaultGracefulShutdownTimeoutSeconds = 5;
        public const int DefaultSessionExpiredMinutes = 30;
        public const int DefaultSessionCleanupIntervalMinutes = 5;
        public const int MaxNodeIdLength = 128;
    }
}