using CommunityToolkit.Mvvm.ComponentModel;
using CrawlerCore.Events;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace CrawlerMaster.WPF.ViewModels
{
    public partial class MainWindowViewModel : ObservableObject, IAsyncDisposable
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IEventAggregator _eventAggregator;
        private Guid _nodeStatusSubscriptionId;

        [ObservableProperty]
        private object _currentViewModel;

        [ObservableProperty]
        private ObservableCollection<MenuItemViewModel> _menuItems;

        [ObservableProperty]
        private MenuItemViewModel _selectedMenuItem;

        [ObservableProperty]
        private string _masterStatus = "在线";

        public MainWindowViewModel(IServiceProvider serviceProvider, IEventAggregator eventAggregator)
        {
            _serviceProvider = serviceProvider;
            _eventAggregator = eventAggregator;

            MenuItems = new ObservableCollection<MenuItemViewModel>
            {
                new() { Title = "节点监控", Icon = "Monitor", ViewModelType = typeof(NodeMonitorViewModel) },
                new() { Title = "任务管理", Icon = "FormatListBulleted", ViewModelType = typeof(TaskManagementViewModel) },
                new() { Title = "数据查看", Icon = "Database", ViewModelType = typeof(DataViewerViewModel) }
            };

            _nodeStatusSubscriptionId = _eventAggregator.Subscribe<NodeStatusChangedEventArgs>(OnNodeStatusChanged);

            // 手动触发初始导航
            SelectedMenuItem = MenuItems[0];
        }

        partial void OnSelectedMenuItemChanged(MenuItemViewModel value)
        {
            if (value?.ViewModelType != null)
            {
                CurrentViewModel = _serviceProvider.GetRequiredService(value.ViewModelType);
            }
        }

        private void OnNodeStatusChanged(object? sender, NodeStatusChangedEventArgs e)
        {
            MasterStatus = $"最后节点心跳: {DateTime.Now:HH:mm:ss}";
        }

        public async ValueTask DisposeAsync()
        {
            _eventAggregator.Unsubscribe<NodeStatusChangedEventArgs>(_nodeStatusSubscriptionId);
            await Task.CompletedTask;
        }
    }

    public class MenuItemViewModel : ObservableObject
    {
        public string Title { get; set; } = string.Empty;
        public string Icon { get; set; } = string.Empty;
        public Type? ViewModelType { get; set; }
    }
}