using CrawlerCore.Entities;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CrawlerMaster.WPF.ViewModels
{
    /// <summary>
    /// 节点显示信息类（重构版：剥离PlotModel，仅保留纯数据）
    /// </summary>
    public class NodeDisplayInfo : INotifyPropertyChanged
    {
        private string _nodeId = string.Empty;
        private string _machineName = string.Empty;
        private string _ipAddress = string.Empty;
        private int _cpuCores;
        private double _totalMemoryGb;
        private NodeStatus _status;
        private double _cpuUsage;
        private double _memoryUsageMb;
        private double _diskUsagePercent;
        private int _activeTasks;
        private DateTime _lastHeartbeat;

        #region 历史数据配置（纯数据，无视图绑定）
        private const int MaxHistoryPoints = 60;
        private readonly object _historyLock = new();
        // 仅保留历史数据集合，PlotModel由各视图独立创建
        public ObservableCollection<double> CpuHistory { get; } = new();
        public ObservableCollection<double> MemoryHistory { get; } = new();
        #endregion

        #region 基础业务属性
        public string NodeId
        {
            get => _nodeId;
            set => SetProperty(ref _nodeId, value);
        }

        public string MachineName
        {
            get => _machineName;
            set => SetProperty(ref _machineName, value);
        }

        public string IpAddress
        {
            get => _ipAddress;
            set => SetProperty(ref _ipAddress, value);
        }

        public int CpuCores
        {
            get => _cpuCores;
            set => SetProperty(ref _cpuCores, value);
        }

        public double TotalMemoryGb
        {
            get => _totalMemoryGb;
            set => SetProperty(ref _totalMemoryGb, value);
        }

        public NodeStatus Status
        {
            get => _status;
            set
            {
                if (SetProperty(ref _status, value))
                {
                    OnPropertyChanged(nameof(IsOnline));
                }
            }
        }

        public bool IsOnline => Status == NodeStatus.Online;

        public double CpuUsage
        {
            get => _cpuUsage;
            set => SetProperty(ref _cpuUsage, value);
        }

        public double MemoryUsageMb
        {
            get => _memoryUsageMb;
            set => SetProperty(ref _memoryUsageMb, value);
        }

        public double DiskUsagePercent
        {
            get => _diskUsagePercent;
            set => SetProperty(ref _diskUsagePercent, value);
        }

        public int ActiveTasks
        {
            get => _activeTasks;
            set => SetProperty(ref _activeTasks, value);
        }

        public DateTime LastHeartbeat
        {
            get => _lastHeartbeat;
            set => SetProperty(ref _lastHeartbeat, value);
        }
        #endregion

        #region 历史数据操作（线程安全）
        /// <summary>
        /// 初始化历史数据
        /// </summary>
        public void InitializeHistory(double cpuUsage, double memoryUsageMb)
        {
            lock (_historyLock)
            {
                CpuHistory.Clear();
                MemoryHistory.Clear();
                CpuHistory.Add(Math.Clamp(cpuUsage, 0, 100));
                MemoryHistory.Add(Math.Max(memoryUsageMb, 0));
            }
        }

        /// <summary>
        /// 添加资源数据点，自动淘汰超量旧数据
        /// </summary>
        public void AddResourceDataPoint(double cpuUsage, double memoryUsageMb)
        {
            lock (_historyLock)
            {
                cpuUsage = Math.Clamp(cpuUsage, 0, 100);
                memoryUsageMb = Math.Max(memoryUsageMb, 0);

                CpuHistory.Add(cpuUsage);
                if (CpuHistory.Count > MaxHistoryPoints)
                    CpuHistory.RemoveAt(0);

                MemoryHistory.Add(memoryUsageMb);
                if (MemoryHistory.Count > MaxHistoryPoints)
                    MemoryHistory.RemoveAt(0);
            }
        }

        /// <summary>
        /// 清空历史数据
        /// </summary>
        public void ClearHistory()
        {
            lock (_historyLock)
            {
                CpuHistory.Clear();
                MemoryHistory.Clear();
            }
        }
        #endregion

        #region INotifyPropertyChanged 标准实现
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
    }
}