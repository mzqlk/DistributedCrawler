using CrawlerCore.Entities;
using CrawlerCore.Events;
using CrawlerCore.Infrastructure;
using CrawlerCore.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace CrawlerMaster.WPF.ViewModels
{
    public class MainWindowViewModel : IDisposable
    {
        private readonly IEventAggregator _eventAggregator;
        private readonly IServiceProvider _serviceProvider;
        private readonly Dispatcher _uiDispatcher;
        private Guid _nodeStatusSubscriptionId = Guid.Empty;
        private bool _disposed = false;

        // 绑定到UI的节点列表
        public ObservableCollection<NodeDisplayInfo> Nodes { get; } = new ObservableCollection<NodeDisplayInfo>();

        // 构造函数：注入必要服务
        public MainWindowViewModel(IEventAggregator eventAggregator, IServiceProvider serviceProvider)
        {
            _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _uiDispatcher = Application.Current.Dispatcher;

            // 1. 订阅全局节点状态变更事件
            _nodeStatusSubscriptionId = _eventAggregator.Subscribe<NodeStatusChangedEventArgs>(OnNodeStatusChanged);

            // 2. 初始化加载现有节点列表
            _ = LoadNodesAsync();
        }

        // 核心事件处理：响应所有节点状态变更（添加异常处理）
        private void OnNodeStatusChanged(object? sender, NodeStatusChangedEventArgs args)
        {
            try
            {
                _uiDispatcher.Invoke(() =>
                {
                    try
                    {
                        // 查找现有节点
                        var existingNode = Nodes.FirstOrDefault(n => n.NodeId == args.NodeId);

                        if (existingNode != null)
                        {
                            // 更新现有节点（自动触发UI更新）
                            UpdateNodeDisplayInfo(existingNode, args.NodeInfo);
                        }
                        else
                        {
                            // 添加新节点
                            Nodes.Add(CreateNodeDisplayInfo(args.NodeInfo));
                        }
                    }
                    catch (Exception ex)
                    {
                        LogHelper.Error(ex, "UI线程更新节点信息失败");
                    }
                });
            }
            catch (Exception ex)
            {
                LogHelper.Error(ex, "调度UI线程更新节点信息失败");
            }
        }

        // 辅助方法：从 NodeInfo 创建 NodeDisplayInfo
        private NodeDisplayInfo CreateNodeDisplayInfo(NodeInfo nodeInfo)
        {
            return new NodeDisplayInfo
            {
                NodeId = nodeInfo.NodeId,
                MachineName = nodeInfo.MachineName,
                IpAddress = nodeInfo.IpAddress,
                CpuCores = nodeInfo.CpuCores,
                TotalMemoryGb = nodeInfo.TotalMemoryGb,
                Status = nodeInfo.Status,
                CpuUsage = nodeInfo.CpuUsage,
                MemoryUsageMb = nodeInfo.MemoryUsageMb,
                LastHeartbeat = nodeInfo.LastHeartbeat
            };
        }

        // 辅助方法：更新现有 NodeDisplayInfo
        private void UpdateNodeDisplayInfo(NodeDisplayInfo displayInfo, NodeInfo nodeInfo)
        {
            // 使用 SetProperty 方法，自动触发 PropertyChanged 事件
            displayInfo.MachineName = nodeInfo.MachineName;
            displayInfo.IpAddress = nodeInfo.IpAddress;
            displayInfo.CpuCores = nodeInfo.CpuCores;
            displayInfo.TotalMemoryGb = nodeInfo.TotalMemoryGb;
            displayInfo.Status = nodeInfo.Status;
            displayInfo.CpuUsage = nodeInfo.CpuUsage;
            displayInfo.MemoryUsageMb = nodeInfo.MemoryUsageMb;
            displayInfo.LastHeartbeat = nodeInfo.LastHeartbeat;
        }

        // 公共方法：手动刷新节点列表（使用规范日志）
        public async Task LoadNodesAsync()
        {
            try
            {
                LogHelper.Info("开始加载节点列表");

                using var scope = _serviceProvider.CreateScope();
                var nodeService = scope.ServiceProvider.GetRequiredService<INodeManagementService>();

                // 从数据库获取所有活跃节点
                var activeNodes = await nodeService.GetActiveNodesAsync();

                _uiDispatcher.Invoke(() =>
                {
                    Nodes.Clear();
                    foreach (var node in activeNodes)
                    {
                        Nodes.Add(CreateNodeDisplayInfo(node));
                    }
                });

                LogHelper.Info("节点列表加载完成，共 {Count} 个活跃节点", activeNodes.Count);
            }
            catch (Exception ex)
            {
                LogHelper.Error(ex, "加载节点列表失败");
            }
        }

        // IDisposable 实现
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;

            if (disposing)
            {
                // 取消事件订阅
                if (_nodeStatusSubscriptionId != Guid.Empty)
                {
                    try
                    {
                        _eventAggregator.Unsubscribe<NodeStatusChangedEventArgs>(_nodeStatusSubscriptionId);
                        _nodeStatusSubscriptionId = Guid.Empty;
                        LogHelper.Info("MainWindowViewModel 已取消节点状态变更事件订阅");
                    }
                    catch (Exception ex)
                    {
                        LogHelper.Error(ex, "取消节点状态变更事件订阅失败");
                    }
                }
            }

            _disposed = true;
        }

        ~MainWindowViewModel()
        {
            Dispose(false);
        }
    }
}