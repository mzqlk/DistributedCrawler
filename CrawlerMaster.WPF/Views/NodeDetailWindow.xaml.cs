using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace CrawlerMaster.WPF.Views
{
    /// <summary>
    /// NodeDetailWindow.xaml 的交互逻辑
    /// </summary>
    public partial class NodeDetailWindow : Window
    {
        public NodeDetailWindow()
        {
            InitializeComponent();
            this.Loaded += NodeDetailWindow_Loaded;
        }

        private void NodeDetailWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // ========== 调试代码开始 ==========
            System.Diagnostics.Debug.WriteLine($"=== Window 已加载 ===");
            System.Diagnostics.Debug.WriteLine($"DataContext 类型: {this.DataContext?.GetType().Name}");

            if (this.DataContext is ViewModels.NodeDetailViewModel vm)
            {
                System.Diagnostics.Debug.WriteLine($"ViewModel 中 CpuPlotModel 是否为 null: {vm.CpuPlotModel == null}");
                System.Diagnostics.Debug.WriteLine($"ViewModel 中 TargetNode 是否为 null: {vm.TargetNode == null}");
            }
            // ========== 调试代码结束 ==========
        }
    }
}
