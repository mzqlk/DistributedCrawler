using CrawlerCore.Protos;
using Microsoft.Extensions.Logging;

namespace CrawlerWorker.Services;

/// <summary>
/// worker会话管理器，负责保存和管理worker的会话信息，包括SessionId、系统配置和注册状态等。
/// </summary>
public class WorkerSessionManager : IWorkerSessionManager
{
    private readonly ILogger<WorkerSessionManager> _logger;
    private readonly object _lockObj = new();

    private volatile string? _sessionId;
    private volatile SystemConfig? _systemConfig;
    private volatile bool _isRegistered;
    private int _activeTaskCount;

    public string? SessionId => _sessionId;
    public SystemConfig? SystemConfig => _systemConfig;
    public bool IsRegistered => _isRegistered;
    public int ActiveTaskCount => _activeTaskCount;

    public WorkerSessionManager(ILogger<WorkerSessionManager> logger)
    {
        _logger = logger;
    }

    public void SaveSession(RegistrationResponse response)
    {
        lock (_lockObj)
        {
            if (!response.Success)
            {
                _logger.LogWarning("注册失败，不保存会话信息，原因: {Message}", response.Message);
                return;
            }

            _sessionId = response.SessionId;
            _systemConfig = response.SystemConfig;
            _isRegistered = true;

            _logger.LogInformation("会话信息保存成功，SessionId: {SessionId}", _sessionId);
        }
    }

    public void ClearSession()
    {
        lock (_lockObj)
        {
            _logger.LogInformation("清除本地会话信息，SessionId: {SessionId}", _sessionId);
            _sessionId = null;
            _systemConfig = null;
            _isRegistered = false;
        }
    }

    public void IncrementActiveTasks() => Interlocked.Increment(ref _activeTaskCount);
    public void DecrementActiveTasks() => Interlocked.Decrement(ref _activeTaskCount);
}