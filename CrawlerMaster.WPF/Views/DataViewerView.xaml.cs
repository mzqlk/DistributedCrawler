using System.Windows.Controls;

namespace CrawlerMaster.WPF.Views
{
    /// <summary>
    /// 数据查看页。DataContext 是单例 DataViewerViewModel，由容器在程序退出时释放；
    /// 左侧导航切换页面时视图会被卸载，这里不能释放 ViewModel，否则切回来后不再自动刷新
    /// </summary>
    public partial class DataViewerView : UserControl
    {
        public DataViewerView()
        {
            InitializeComponent();
        }
    }
}
