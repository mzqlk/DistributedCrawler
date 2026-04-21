using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;

namespace CrawlerMaster.WPF.Views
{
    public partial class DataViewerView : UserControl
    {
        public DataViewerView()
        {
            InitializeComponent();
            this.Unloaded += DataViewerView_Unloaded;
        }

        private void DataViewerView_Unloaded(object sender, System.Windows.RoutedEventArgs e)
        {
            if (this.DataContext is System.IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}