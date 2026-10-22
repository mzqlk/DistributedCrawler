using Grpc.Core;
using System;

namespace CrawlerWorker.Communication
{
    /// <summary>双向流结束后的处理方式（W-02）</summary>
    public enum StreamEndAction
    {
        /// <summary>暂时性问题（连接断开、Master 不可用等）：计入失败次数，退避后用原会话重连</summary>
        Retry,

        /// <summary>会话已失效（例如 Master 重启）：清除会话，立即重新注册，不计入失败次数</summary>
        ReRegister,

        /// <summary>配置错误（令牌被拒绝）：重试没有意义，以非 0 退出码退出</summary>
        Exit
    }

    /// <summary>
    /// Worker 连接管理的决策规则，写成纯函数便于测试（W-02、W-11）
    /// </summary>
    public static class ConnectionPolicy
    {
        /// <summary>按双向流结束时的状态码决定下一步；null 表示流正常结束（例如 Master 主动关闭）</summary>
        public static StreamEndAction ClassifyStreamEnd(StatusCode? status) => status switch
        {
            StatusCode.PermissionDenied or StatusCode.NotFound => StreamEndAction.ReRegister,
            StatusCode.Unauthenticated => StreamEndAction.Exit,
            _ => StreamEndAction.Retry
        };

        /// <summary>注册失败的状态码：令牌错误、参数非法时退出，其余（Master 不可达、超时、内部错误等）一直重试</summary>
        public static bool IsFatalRegistrationError(StatusCode status)
            => status is StatusCode.Unauthenticated or StatusCode.InvalidArgument;

        /// <summary>
        /// 第 <paramref name="failures"/> 次连续失败后的等待时间：从 <paramref name="initial"/> 开始翻倍，不超过 <paramref name="max"/>，
        /// 再乘以 0.8–1.2 的随机抖动（<paramref name="jitter"/> 取 [0, 1)），避免多个 Worker 同时冲击刚恢复的 Master
        /// </summary>
        public static TimeSpan ComputeBackoff(int failures, TimeSpan initial, TimeSpan max, double jitter)
        {
            if (failures <= 0 || initial <= TimeSpan.Zero) return TimeSpan.Zero;
            if (max < initial) max = initial;
            var exponential = initial.TotalMilliseconds * Math.Pow(2, Math.Min(failures - 1, 30));
            var capped = Math.Min(exponential, max.TotalMilliseconds);
            var withJitter = capped * (0.8 + 0.4 * Math.Clamp(jitter, 0, 1));
            return TimeSpan.FromMilliseconds(Math.Min(withJitter, max.TotalMilliseconds));
        }
    }
}
