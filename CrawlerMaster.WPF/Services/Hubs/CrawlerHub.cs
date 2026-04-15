using Microsoft.AspNetCore.SignalR;
using CrawlerCore.Infrastructure;
using Microsoft.Extensions.Options;

namespace CrawlerMaster.WPF.Services.Hubs
{
    /// <summary>
    /// 爬虫实时通信Hub（强类型版本，兼容原有逻辑）
    /// </summary>
    public class CrawlerHub : Hub<ICrawlerHubClient>
    {
        private readonly SignalRSettings _signalRSettings;

        public CrawlerHub(IOptions<SignalRSettings> signalRSettings)
        {
            _signalRSettings = signalRSettings.Value;
        }

        public override Task OnConnectedAsync()
        {
            LogHelper.Info($"SignalR 客户端连接成功: ConnectionId={Context.ConnectionId}");
            return base.OnConnectedAsync();
        }

        public override Task OnDisconnectedAsync(Exception? exception)
        {
            LogHelper.Info($"SignalR 客户端断开连接: ConnectionId={Context.ConnectionId}, 异常={exception?.Message}");
            return base.OnDisconnectedAsync(exception);
        }
    }
}