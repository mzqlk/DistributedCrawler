namespace CrawlerCore.Common
{
    /// <summary>
    /// 全局规范常量（严格对齐原项目，无业务侵入）
    /// </summary>
    public static class GlobalConstants
    {
        /// <summary>
        /// Docker环境Master端点环境变量Key（原项目官方定义）
        /// </summary>
        public const string MasterEndpointEnvKey = "MASTER_ENDPOINT";

        /// <summary>
        /// 节点会话元数据Key（gRPC标准Header传递，不修改proto）
        /// </summary>
        public const string SessionIdHeaderKey = "X-Node-Session-Id";

        /// <summary>
        /// 节点认证元数据Key（gRPC标准Header传递，不修改proto）
        /// </summary>
        public const string AuthTokenHeaderKey = "X-Node-Auth-Token";

        /// <summary>
        /// UTC时间格式（全链路统一，原项目规范）
        /// </summary>
        public const string UtcDateTimeFormat = "yyyy-MM-dd HH:mm:ss";
    }
}