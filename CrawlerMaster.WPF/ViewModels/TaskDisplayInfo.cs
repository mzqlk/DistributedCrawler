using CrawlerCore.Entities;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CrawlerMaster.WPF.ViewModels
{
    /// <summary>
    /// 任务进度UI展示模型，与TaskProgressMessage完全映射
    /// </summary>
    public class TaskDisplayInfo : INotifyPropertyChanged
    {
        private string _taskId = string.Empty;
        private string _taskName = string.Empty;
        private CrawlerCore.Entities.TaskStatus _status;
        private int _completedPages;
        private int _totalPages;
        private string _assignedNode = string.Empty;

        public string TaskId
        {
            get => _taskId;
            set => SetProperty(ref _taskId, value);
        }

        public string TaskName
        {
            get => _taskName;
            set => SetProperty(ref _taskName, value);
        }

        public CrawlerCore.Entities.TaskStatus Status
        {
            get => _status;
            set => SetProperty(ref _status, value);
        }

        public int CompletedPages
        {
            get => _completedPages;
            set
            {
                if (SetProperty(ref _completedPages, value))
                {
                    OnPropertyChanged(nameof(ProgressPercent));
                }
            }
        }

        public int TotalPages
        {
            get => _totalPages;
            set
            {
                if (SetProperty(ref _totalPages, value))
                {
                    OnPropertyChanged(nameof(ProgressPercent));
                }
            }
        }

        /// <summary>
        /// 进度百分比（与TaskProgressMessage计算逻辑完全一致）
        /// </summary>
        public double ProgressPercent => TotalPages == 0 ? 0 : Math.Round((double)CompletedPages / TotalPages * 100, 2);

        public string AssignedNode
        {
            get => _assignedNode;
            set => SetProperty(ref _assignedNode, value);
        }

        // INotifyPropertyChanged 实现（与NodeDisplayInfo完全统一）
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