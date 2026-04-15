using Microsoft.AspNetCore.SignalR;
using CrawlerCore.Infrastructure; // 补全：LogHelper所在命名空间
using Microsoft.Extensions.Options;

namespace CrawlerMaster.WPF.Services.Hubs;

/// <summary>
/// 爬虫实时通信 Hub（服务端核心）
/// </summary>
public class CrawlerHub : Hub
{
    private readonly SignalRSettings _signalRSettings;

    public CrawlerHub(IOptions<SignalRSettings> signalRSettings)
    {
        _signalRSettings = signalRSettings.Value;
    }

    /// <summary>
    /// 客户端连接成功回调
    /// </summary>
    public override Task OnConnectedAsync()
    {
        LogHelper.Info($"SignalR 客户端连接成功: ConnectionId={Context.ConnectionId}");
        return base.OnConnectedAsync();
    }

    /// <summary>
    /// 客户端断开连接回调
    /// </summary>
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        LogHelper.Info($"SignalR 客户端断开连接: ConnectionId={Context.ConnectionId}, 异常={exception?.Message}");
        return base.OnDisconnectedAsync(exception);
    }
}