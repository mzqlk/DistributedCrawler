namespace CrawlerCore.Common
{
    /// <summary>
    /// 节点ID生成帮助类
    /// </summary>
    public static class NodeIdHelper
    {
        private const string NodeIdEnvKey = "MASTER_NODE_ID";

        /// <summary>
        /// 获取节点唯一标识（优先环境变量，无则用机器名）
        /// </summary>
        public static string GetNodeId()
        {
            var envNodeId = Environment.GetEnvironmentVariable(NodeIdEnvKey);
            return string.IsNullOrWhiteSpace(envNodeId)
                ? Environment.MachineName
                : envNodeId.Trim();
        }
    }
}