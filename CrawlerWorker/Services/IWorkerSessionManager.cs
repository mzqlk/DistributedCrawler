using CrawlerCore.Protos;

namespace CrawlerWorker.Services;

/// <summary>
/// Worker本地会话管理器，线程安全
/// </summary>
public interface IWorkerSessionManager
{
    /// <summary>
    /// 当前会话ID
    /// </summary>
    string? SessionId { get; }

    /// <summary>
    /// 系统配置（Master返回）
    /// </summary>
    SystemConfig? SystemConfig { get; }

    /// <summary>
    /// 节点注册状态
    /// </summary>
    bool IsRegistered { get; }

    /// <summary>
    /// 保存注册成功的会话信息
    /// </summary>
    void SaveSession(RegistrationResponse response);

    /// <summary>
    /// 清除会话信息
    /// </summary>
    void ClearSession();
}