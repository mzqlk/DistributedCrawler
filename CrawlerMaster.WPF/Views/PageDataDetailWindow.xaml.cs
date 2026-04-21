using CrawlerMaster.WPF.ViewModels;
using System.Windows;

namespace CrawlerMaster.WPF.Views
{
    public partial class PageDataDetailWindow : Window
    {
        public PageDataDetailWindow(PageResultDisplayInfo pageResult)
        {
            InitializeComponent();
            DataContext = pageResult;
            // 绑定ParsedData字典到DataGrid
            ParsedDataGrid.ItemsSource = pageResult.ParsedData;
        }

        private void CloseButton_OnClick(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}