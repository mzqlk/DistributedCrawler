using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CrawlerCore.Entities;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace CrawlerMaster.WPF.ViewModels
{
    public partial class NodeDetailViewModel : ObservableObject, IAsyncDisposable
    {
        #region 私有字段
        private volatile bool _isDisposed;
        private volatile bool _isDisposing;
        private readonly object _stateLock = new();
        #endregion

        #region 详情页独立的PlotModel
        [ObservableProperty]
        private PlotModel? _cpuPlotModel;

        [ObservableProperty]
        private PlotModel? _memoryPlotModel;
        #endregion

        #region 绑定属性
        [ObservableProperty]
        private NodeDisplayInfo? _targetNode;

        public bool IsNodeOnline => TargetNode?.Status == NodeStatus.Online;
        public bool HasValidNode => TargetNode != null;
        public bool ShowNoDataPlaceholder => TargetNode == null;
        #endregion

        #region 命令与事件
        public IRelayCommand CloseCommand { get; }
        public event EventHandler? RequestClose;
        #endregion

        #region 构造函数
        public NodeDetailViewModel()
        {
            CloseCommand = new RelayCommand(ExecuteCloseCommand, CanExecuteCloseCommand);
        }

        public NodeDetailViewModel(NodeDisplayInfo targetNode) : this()
        {
            TargetNode = targetNode ?? throw new ArgumentNullException(nameof(targetNode));
        }
        #endregion

        #region TargetNode变更处理（核心修复：强制同步历史数据）
        partial void OnTargetNodeChanging(NodeDisplayInfo? oldValue)
        {
            lock (_stateLock)
            {
                if (_isDisposed || _isDisposing) return;

                if (oldValue != null)
                {
                    oldValue.PropertyChanged -= OnNodeInstancePropertyChanged;
                    oldValue.CpuHistory.CollectionChanged -= OnDetailCpuHistoryChanged;
                    oldValue.MemoryHistory.CollectionChanged -= OnDetailMemoryHistoryChanged;
                }
            }
        }

        partial void OnTargetNodeChanged(NodeDisplayInfo? newValue)
        {
            lock (_stateLock)
            {
                if (_isDisposed || _isDisposing) return;

                OnPropertyChanged(nameof(IsNodeOnline));
                OnPropertyChanged(nameof(HasValidNode));
                OnPropertyChanged(nameof(ShowNoDataPlaceholder));

                if (newValue != null)
                {
                    newValue.PropertyChanged += OnNodeInstancePropertyChanged;
                    newValue.CpuHistory.CollectionChanged += OnDetailCpuHistoryChanged;
                    newValue.MemoryHistory.CollectionChanged += OnDetailMemoryHistoryChanged;

                    // 核心修复1：先创建PlotModel
                    CpuPlotModel = CreateLinePlotModel("CPU使用率(%)", 0, 100, "%");
                    MemoryPlotModel = CreateLinePlotModel("内存占用(MB)", null, null, "MB");

                    // 核心修复2：强制同步已有历史数据
                    ForceRefreshCharts();
                }
                else
                {
                    CpuPlotModel = null;
                    MemoryPlotModel = null;
                }
            }
        }

        // 核心修复3：新增强制刷新图表方法
        private void ForceRefreshCharts()
        {
            if (TargetNode == null) return;

            // 刷新CPU图表
            if (CpuPlotModel != null && CpuPlotModel.Series.Count > 0 && CpuPlotModel.Series[0] is LineSeries cpuSeries)
            {
                cpuSeries.Points.Clear();
                int i = 0;
                // 直接访问历史数据，填充图表
                foreach (var v in TargetNode.CpuHistory)
                {
                    cpuSeries.Points.Add(new DataPoint(i++, v));
                }
                CpuPlotModel.InvalidatePlot(true);
            }

            // 刷新内存图表
            if (MemoryPlotModel != null && MemoryPlotModel.Series.Count > 0 && MemoryPlotModel.Series[0] is LineSeries memorySeries)
            {
                memorySeries.Points.Clear();
                int i = 0;
                // 直接访问历史数据，填充图表
                foreach (var v in TargetNode.MemoryHistory)
                {
                    memorySeries.Points.Add(new DataPoint(i++, v));
                }
                MemoryPlotModel.InvalidatePlot(true);
            }
        }

        // CPU历史数据变化，增量更新
        private void OnDetailCpuHistoryChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (TargetNode == null || CpuPlotModel == null) return;

            // 每次变化都强制全量刷新，确保数据一致性
            ForceRefreshCharts();
        }

        // 内存历史数据变化，增量更新
        private void OnDetailMemoryHistoryChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (TargetNode == null || MemoryPlotModel == null) return;

            // 每次变化都强制全量刷新，确保数据一致性
            ForceRefreshCharts();
        }
        #endregion

        #region 命令执行逻辑
        private bool CanExecuteCloseCommand()
        {
            lock (_stateLock)
            {
                return !_isDisposed && !_isDisposing;
            }
        }

        private void ExecuteCloseCommand()
        {
            lock (_stateLock)
            {
                if (!CanExecuteCloseCommand()) return;
            }
            RequestClose?.Invoke(this, EventArgs.Empty);
        }
        #endregion

        #region 节点属性变更处理
        private void OnNodeInstancePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            lock (_stateLock)
            {
                if (_isDisposed || _isDisposing) return;
            }

            if (e.PropertyName == nameof(NodeDisplayInfo.Status))
            {
                OnPropertyChanged(nameof(IsNodeOnline));
            }
        }
        #endregion

        #region PlotModel辅助方法（简化：不再接收初始数据）
        private PlotModel CreateLinePlotModel(string title, double? min, double? max, string unit)
        {
            var model = new PlotModel { Title = title };
            var series = new LineSeries { MarkerType = MarkerType.None };
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
        #endregion

        #region 生命周期管理
        public async ValueTask DisposeAsync()
        {
            lock (_stateLock)
            {
                if (_isDisposed) return;
                _isDisposing = true;
            }

            try
            {
                if (TargetNode != null)
                {
                    TargetNode.PropertyChanged -= OnNodeInstancePropertyChanged;
                    TargetNode.CpuHistory.CollectionChanged -= OnDetailCpuHistoryChanged;
                    TargetNode.MemoryHistory.CollectionChanged -= OnDetailMemoryHistoryChanged;
                }

                TargetNode = null;
                CpuPlotModel = null;
                MemoryPlotModel = null;

                OnPropertyChanged(nameof(IsNodeOnline));
                OnPropertyChanged(nameof(HasValidNode));
                OnPropertyChanged(nameof(ShowNoDataPlaceholder));

                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                CrawlerCore.Infrastructure.LogHelper.Warn($"节点详情ViewModel释放异常: {ex.Message}");
            }
            finally
            {
                lock (_stateLock)
                {
                    _isDisposed = true;
                    _isDisposing = false;
                }
                GC.SuppressFinalize(this);
            }
        }
        #endregion
    }
}