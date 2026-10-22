using CommunityToolkit.Mvvm.Input;
using CrawlerCore.Entities;
using CrawlerCore.Events;
using CrawlerCore.Interfaces;
using CrawlerMaster.WPF.Views;
using Microsoft.Extensions.DependencyInjection;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace CrawlerMaster.WPF.ViewModels
{
    /// <summary>
    /// 节点监控视图模型
    /// </summary>
    public class NodeMonitorViewModel : INotifyPropertyChanged, IAsyncDisposable
    {
        #region 私有字段
        private readonly IEventAggregator _eventAggregator;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly Guid _nodeStatusSubscriptionId;
        private readonly SemaphoreSlim _updateLock = new(1, 1);
        private readonly Dispatcher _uiDispatcher;
        private NodeDisplayInfo? _selectedNode;
        private volatile bool _isDisposed;
        private volatile bool _isDisposing;
        #endregion

        #region 列表页独立的PlotModel
        private PlotModel? _cpuPlotModel;
        public PlotModel? CpuPlotModel
        {
            get => _cpuPlotModel;
            set => SetProperty(ref _cpuPlotModel, value);
        }

        private PlotModel? _memoryPlotModel;
        public PlotModel? MemoryPlotModel
        {
            get => _memoryPlotModel;
            set => SetProperty(ref _memoryPlotModel, value);
        }
        #endregion

        #region 绑定属性
        public ObservableCollection<NodeDisplayInfo> Nodes { get; } = new();
        public NodeDisplayInfo? SelectedNode
        {
            get => _selectedNode;
            set
            {
                // 先保存旧值：SetProperty 之后字段已是新节点，退订必须作用在旧节点上
                var oldNode = _selectedNode;
                if (SetProperty(ref _selectedNode, value))
                {
                    // 选中节点变化时，重新绑定历史数据，创建新的PlotModel
                    OnSelectedNodeChanged(oldNode);
                }
            }
        }
        public bool IsEmpty => Nodes.Count == 0;
        #endregion

        public IRelayCommand<NodeDisplayInfo> OpenDetailCommand { get; }

        #region 构造函数
        public NodeMonitorViewModel(IEventAggregator eventAggregator, IServiceScopeFactory scopeFactory)
        {
            _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
            _uiDispatcher = Application.Current.Dispatcher;

            _nodeStatusSubscriptionId = _eventAggregator.Subscribe<NodeStatusChangedEventArgs>(OnNodeStatusChanged);
            OpenDetailCommand = new RelayCommand<NodeDisplayInfo>(ExecuteOpenDetailCommand, CanExecuteOpenDetailCommand);

            // 加载 ViewModel 创建前已在线的节点（此前没有任何地方调用，导致早于界面注册的节点要等下一次心跳才出现）
            _ = InitializeAsync();
        }
        #endregion

        #region 选中节点变化处理（创建独立PlotModel）
        private void OnSelectedNodeChanged(NodeDisplayInfo? oldNode)
        {
            // 先取消旧节点的事件订阅，避免订阅泄漏（每次切换都多一份订阅，心跳时整图重绘多次）
            if (oldNode != null)
            {
                oldNode.CpuHistory.CollectionChanged -= OnMonitorCpuHistoryChanged;
                oldNode.MemoryHistory.CollectionChanged -= OnMonitorMemoryHistoryChanged;
            }

            if (_selectedNode == null)
            {
                CpuPlotModel = null;
                MemoryPlotModel = null;
                return;
            }

            // 为列表页创建独立的PlotModel实例
            CpuPlotModel = CreateLinePlotModel("CPU使用率(%)", _selectedNode.CpuHistory, 0, 100, "%");
            MemoryPlotModel = CreateLinePlotModel("内存占用(MB)", _selectedNode.MemoryHistory, null, null, "MB");

            ForceRefreshMonitorCharts();

            // 订阅历史数据变化，同步更新图表
            _selectedNode.CpuHistory.CollectionChanged += OnMonitorCpuHistoryChanged;
            _selectedNode.MemoryHistory.CollectionChanged += OnMonitorMemoryHistoryChanged;
        }

        // 列表页强制刷新图表方法
        private void ForceRefreshMonitorCharts()
        {
            if (_selectedNode == null) return;

            if (CpuPlotModel != null && CpuPlotModel.Series.Count > 0 && CpuPlotModel.Series[0] is LineSeries cpuSeries)
            {
                cpuSeries.Points.Clear();
                int i = 0;
                foreach (var v in _selectedNode.CpuHistory)
                {
                    cpuSeries.Points.Add(new DataPoint(i++, v));
                }
                CpuPlotModel.InvalidatePlot(true);
            }

            if (MemoryPlotModel != null && MemoryPlotModel.Series.Count > 0 && MemoryPlotModel.Series[0] is LineSeries memorySeries)
            {
                memorySeries.Points.Clear();
                int i = 0;
                foreach (var v in _selectedNode.MemoryHistory)
                {
                    memorySeries.Points.Add(new DataPoint(i++, v));
                }
                MemoryPlotModel.InvalidatePlot(true);
            }
        }

        // 列表页CPU历史数据变化，更新图表
        private void OnMonitorCpuHistoryChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (_selectedNode == null || CpuPlotModel == null) return;
            ForceRefreshMonitorCharts();
        }

        // 列表页内存历史数据变化，更新图表
        private void OnMonitorMemoryHistoryChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (_selectedNode == null || MemoryPlotModel == null) return;
            ForceRefreshMonitorCharts();
        }
        #endregion

        #region 节点状态变更事件处理
        private void OnNodeStatusChanged(object? sender, NodeStatusChangedEventArgs e)
        {
            if (_isDisposed || _isDisposing || e.NodeInfo == null) return;

            // 直接投递到 UI 调度队列：调度器单线程、按 FIFO 执行，天然串行且保序。
            // 原实现在 500ms 内抢不到锁就丢弃事件，UI 繁忙时“节点离线”等关键变更会丢失，界面长期显示错误状态
            _ = _uiDispatcher.InvokeAsync(() =>
            {
                if (_isDisposed || _isDisposing) return;

                try
                {
                    var existNode = Nodes.FirstOrDefault(n => n.NodeId == e.NodeId);
                    if (existNode == null && e.NewStatus == NodeStatus.Online)
                    {
                        var newNode = MapNodeInfoToDisplay(e.NodeInfo);
                        newNode.InitializeHistory(e.NodeInfo.CpuUsage, e.NodeInfo.MemoryUsageMb);
                        Nodes.Add(newNode);
                        OnPropertyChanged(nameof(IsEmpty));
                    }
                    else if (existNode != null)
                    {
                        UpdateNodeDynamicFields(existNode, e.NodeInfo);
                        existNode.Status = e.NewStatus;

                        if (e.NewStatus == NodeStatus.Online)
                        {
                            existNode.AddResourceDataPoint(e.NodeInfo.CpuUsage, e.NodeInfo.MemoryUsageMb);
                        }
                        else
                        {
                            existNode.ClearHistory();
                        }
                    }
                }
                catch (Exception ex)
                {
                    CrawlerCore.Infrastructure.LogHelper.Error(ex, "节点状态更新异常");
                }
            }, DispatcherPriority.Background);
        }
        #endregion

        #region 初始化方法
        public async Task InitializeAsync()
        {
            if (_isDisposed || _isDisposing) return;

            try
            {
                List<NodeInfo> activeNodes;
                using (var scope = _scopeFactory.CreateScope())
                {
                    var nodeManagementService = scope.ServiceProvider.GetRequiredService<INodeManagementService>();
                    activeNodes = await nodeManagementService.GetActiveNodesAsync();
                }
                if (!activeNodes.Any() || _isDisposed || _isDisposing) return;

                await _uiDispatcher.InvokeAsync(() =>
                {
                    if (_isDisposed || _isDisposing) return;

                    // 合并而非清空：加载结果可能晚于实时事件到达，已由事件添加的节点保留其最新数据和历史曲线
                    foreach (var node in activeNodes)
                    {
                        if (Nodes.Any(n => n.NodeId == node.NodeId)) continue;

                        var displayNode = MapNodeInfoToDisplay(node);
                        displayNode.InitializeHistory(node.CpuUsage, node.MemoryUsageMb);
                        Nodes.Add(displayNode);
                    }
                    OnPropertyChanged(nameof(IsEmpty));
                }, DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                if (!_isDisposed)
                {
                    CrawlerCore.Infrastructure.LogHelper.Error(ex, "节点初始化加载失败");
                }
            }
        }
        #endregion

        #region 私有辅助方法
        private NodeDisplayInfo MapNodeInfoToDisplay(NodeInfo node)
        {
            return new NodeDisplayInfo
            {
                NodeId = node.NodeId,
                MachineName = node.MachineName,
                IpAddress = node.IpAddress,
                CpuCores = node.CpuCores,
                TotalMemoryGb = node.TotalMemoryGb,
                Status = node.Status,
                CpuUsage = node.CpuUsage,
                MemoryUsageMb = node.MemoryUsageMb,
                DiskUsagePercent = node.DiskUsagePercent,
                ActiveTasks = node.ActiveTasks,
                LastHeartbeat = node.LastHeartbeat
            };
        }

        private void UpdateNodeDynamicFields(NodeDisplayInfo target, NodeInfo source)
        {
            target.CpuUsage = source.CpuUsage;
            target.MemoryUsageMb = source.MemoryUsageMb;
            target.DiskUsagePercent = source.DiskUsagePercent;
            target.ActiveTasks = source.ActiveTasks;
            target.LastHeartbeat = source.LastHeartbeat;
        }

        // 创建PlotModel实例
        private PlotModel CreateLinePlotModel(string title, ObservableCollection<double> data, double? min, double? max, string unit)
        {
            var model = new PlotModel { Title = title };
            var series = new LineSeries { MarkerType = MarkerType.None };
            foreach (var v in data) series.Points.Add(new DataPoint(series.Points.Count, v));
            model.Series.Add(series);
            model.Axes.Add(new LinearAxis
            {
                Position = AxisPosition.Left,
                Minimum = min ?? double.NaN,
                Maximum = max ?? double.NaN,
                Title = unit
            });
            model.Axes.Add(new LinearAxis
            {
                Position = AxisPosition.Bottom,
                IsAxisVisible = false
            });
            return model;
        }

        // 更新PlotModel数据
        private void UpdatePlotModel(PlotModel model, ObservableCollection<double> data)
        {
            if (model.Series.Count > 0 && model.Series[0] is LineSeries series)
            {
                series.Points.Clear();
                int i = 0;
                foreach (var v in data) series.Points.Add(new DataPoint(i++, v));
                model.InvalidatePlot(true);
            }
        }
        #endregion

        #region 生命周期管理
        public async ValueTask DisposeAsync()
        {
            if (_isDisposed) return;
            _isDisposing = true;

            try
            {
                _eventAggregator.Unsubscribe<NodeStatusChangedEventArgs>(_nodeStatusSubscriptionId);

                if (await _updateLock.WaitAsync(1000))
                {
                    try
                    {
                        await _uiDispatcher.InvokeAsync(() =>
                        {
                            // 取消事件订阅
                            if (_selectedNode != null)
                            {
                                _selectedNode.CpuHistory.CollectionChanged -= OnMonitorCpuHistoryChanged;
                                _selectedNode.MemoryHistory.CollectionChanged -= OnMonitorMemoryHistoryChanged;
                            }

                            SelectedNode = null;
                            foreach (var node in Nodes)
                            {
                                node.ClearHistory();
                            }
                            Nodes.Clear();
                            OnPropertyChanged(nameof(IsEmpty));

                            // 释放PlotModel
                            CpuPlotModel = null;
                            MemoryPlotModel = null;
                        }, DispatcherPriority.Background);
                    }
                    finally
                    {
                        if (_updateLock.CurrentCount == 0)
                            _updateLock.Release();
                    }
                }

                _updateLock.Dispose();
            }
            catch (Exception ex)
            {
                CrawlerCore.Infrastructure.LogHelper.Warn($"Dispose异常: {ex.Message}");
            }
            finally
            {
                _isDisposed = true;
                _isDisposing = false;
                GC.SuppressFinalize(this);
            }
        }
        #endregion

        #region INotifyPropertyChanged 实现
        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
        #endregion

        #region 命令执行逻辑
        private bool CanExecuteOpenDetailCommand(NodeDisplayInfo? node)
        {
            return !_isDisposed && !_isDisposing && node != null;
        }

        private void ExecuteOpenDetailCommand(NodeDisplayInfo? node)
        {
            if (!CanExecuteOpenDetailCommand(node) || node == null) return;

            if (!_uiDispatcher.CheckAccess())
            {
                _uiDispatcher.BeginInvoke(() => ExecuteOpenDetailCommand(node));
                return;
            }

            try
            {
                var detailViewModel = new NodeDetailViewModel(node);
                var detailWindow = new NodeDetailWindow
                {
                    DataContext = detailViewModel,
                    Owner = Application.Current.MainWindow
                };

                detailWindow.Closed += async (s, e) =>
                {
                    await detailViewModel.DisposeAsync();
                    detailWindow.DataContext = null;
                };

                detailWindow.ShowDialog();
            }
            catch (Exception ex)
            {
                CrawlerCore.Infrastructure.LogHelper.Error(ex, "打开节点详情窗口失败");
            }
        }
        #endregion
    }
}