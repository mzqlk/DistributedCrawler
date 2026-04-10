// CrawlerMaster.WPF/Services/NodeHealthCheckService.cs
using CrawlerCore.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using System;
using System.Timers;
using Timer = System.Timers.Timer;

namespace CrawlerMaster.WPF.Services
{
    /// <summary>
    /// 节点健康检查后台服务（定时将超时节点标记为离线）
    /// </summary>
    public class NodeHealthCheckService : IDisposable
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly Timer _timer;
        private readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(30);    // 检查间隔
        private readonly TimeSpan _timeoutThreshold = TimeSpan.FromMinutes(2); // 心跳超时阈值

        public NodeHealthCheckService(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
            _timer = new Timer(_checkInterval.TotalMilliseconds);
            _timer.Elapsed += OnTimerElapsed;
            _timer.AutoReset = true;
        }

        public void Start()
        {
            _timer.Start();
            Log.Information("节点健康检查服务已启动，检查间隔 {Interval}，超时阈值 {Timeout}",
                _checkInterval, _timeoutThreshold);
        }

        public void Stop()
        {
            _timer.Stop();
            Log.Information("节点健康检查服务已停止");
        }

        private async void OnTimerElapsed(object sender, ElapsedEventArgs e)
        {
            // 创建新的 DI 作用域，因为 INodeManagementService 是 Scoped 生命周期
            using (var scope = _serviceProvider.CreateScope())
            {
                var nodeService = scope.ServiceProvider.GetRequiredService<INodeManagementService>();
                try
                {
                    int count = await nodeService.MarkInactiveNodesAsOfflineAsync(_timeoutThreshold);
                    if (count > 0)
                    {
                        Log.Information("健康检查：已将 {Count} 个超时节点标记为离线", count);
                        // 可选：触发 UI 更新事件（可通过事件聚合器或信使实现）
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "健康检查执行过程中发生异常");
                }
            }
        }

        public void Dispose()
        {
            _timer?.Dispose();
        }
    }
}