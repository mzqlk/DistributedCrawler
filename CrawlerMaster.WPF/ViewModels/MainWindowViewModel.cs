using CrawlerCore.Protos;
using CrawlerMaster.WPF.Services;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;

namespace CrawlerMaster.WPF.ViewModels
{
    public class NodeDisplayInfo
    {
        public string NodeId { get; set; }
        public string MachineName { get; set; }
        public string IpAddress { get; set; }
        public int CpuCores { get; set; }
        public double TotalMemoryGb { get; set; }
    }

    public class MainWindowViewModel
    {
        private readonly CrawlerCoordinatorService _coordinatorService;

        public ObservableCollection<NodeDisplayInfo> Nodes { get; } = new ObservableCollection<NodeDisplayInfo>();

        public MainWindowViewModel(CrawlerCoordinatorService coordinatorService)
        {
            _coordinatorService = coordinatorService ?? throw new ArgumentNullException(nameof(coordinatorService));

            // 订阅节点注册事件
            _coordinatorService.NodeRegistered += OnNodeRegistered;
        }

        private void OnNodeRegistered(NodeRegistration registration)
        {
            // 通过 Dispatcher 将更新操作调度到 UI 线程
            Application.Current.Dispatcher.Invoke(() =>
            {
                Nodes.Add(new NodeDisplayInfo
                {
                    NodeId = registration.NodeId,
                    MachineName = registration.MachineName,
                    IpAddress = registration.IpAddress,
                    CpuCores = registration.CpuCores,
                    TotalMemoryGb = registration.TotalMemoryGb
                });
            });
        }
    }
}