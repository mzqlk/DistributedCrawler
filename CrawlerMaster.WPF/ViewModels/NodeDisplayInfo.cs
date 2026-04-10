using CrawlerCore.Entities;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CrawlerMaster.WPF.ViewModels
{
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
        private DateTime _lastHeartbeat;

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
            set => SetProperty(ref _status, value);
        }

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

        public DateTime LastHeartbeat
        {
            get => _lastHeartbeat;
            set => SetProperty(ref _lastHeartbeat, value);
        }

        // INotifyPropertyChanged 实现
        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(field, value))
                return false;

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}