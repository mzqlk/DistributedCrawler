using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CrawlerCore.Common;
using CrawlerCore.Entities;
using CrawlerCore.Events;
using CrawlerCore.Interfaces;
using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows;

namespace CrawlerMaster.WPF.ViewModels
{
    public partial class DataViewerViewModel : ObservableObject, IDisposable
    {
        #region 依赖注入
        private readonly ITaskDataService _taskDataService;
        private readonly IEventAggregator _eventAggregator;
        #endregion

        #region 绑定属性
        [ObservableProperty]
        private ObservableCollection<CrawlResultDisplayInfo> _crawlResults = new();

        [ObservableProperty]
        private CrawlResultDisplayInfo? _selectedCrawlResult;

        [ObservableProperty]
        private ObservableCollection<PageResultDisplayInfo> _currentPageResults = new();

        [ObservableProperty]
        private PageResultDisplayInfo? _selectedPageResult;

        [ObservableProperty]
        private bool _isLoading;

        [ObservableProperty]
        private int _pageIndex = 0;

        [ObservableProperty]
        private int _pageSize = 20;

        [ObservableProperty]
        private int _totalCount;

        [ObservableProperty]
        private string _searchKeyword = string.Empty;

        public int DisplayPageIndex => PageIndex + 1;
        #endregion

        #region 私有字段
        private Guid _crawlResultSubscriptionId;
        private bool _isDisposed = false;
        // JSON序列化配置（全局复用）
        private static readonly JsonSerializerOptions _jsonSerializerOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            ReferenceHandler = ReferenceHandler.IgnoreCycles,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new SystemObjectJsonConverter() }
        };
        #endregion

        #region 构造函数
        public DataViewerViewModel(ITaskDataService taskDataService, IEventAggregator eventAggregator)
        {
            _taskDataService = taskDataService;
            _eventAggregator = eventAggregator;

            _crawlResultSubscriptionId = _eventAggregator.Subscribe<CrawlResultReceivedEventArgs>(OnCrawlResultReceived);

            _ = LoadCrawlResultsAsync();
        }
        #endregion

        #region 核心方法
        [RelayCommand]
        public async Task LoadCrawlResultsAsync()
        {
            IsLoading = true;
            try
            {
                var (items, totalCount) = await _taskDataService.GetPagedCrawlResultProjectionsAsync(
                    PageIndex,
                    PageSize,
                    SearchKeyword.Trim());
                TotalCount = totalCount;

                CrawlResults.Clear();
                foreach (var projection in items)
                {
                    CrawlResults.Add(CrawlResultDisplayInfo.FromProjection(projection));
                }
            }
            catch (Exception ex)
            {
                CrawlerCore.Infrastructure.LogHelper.Error(ex, "加载爬取结果列表失败");
                MessageBox.Show($"加载爬取结果失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsLoading = false;
            }
        }

        partial void OnSelectedCrawlResultChanged(CrawlResultDisplayInfo? oldValue, CrawlResultDisplayInfo? newValue)
        {
            _ = LoadPageResultsAsync(newValue);
        }

        private async Task LoadPageResultsAsync(CrawlResultDisplayInfo? crawlResult)
        {
            CurrentPageResults.Clear();
            SelectedPageResult = null;
            if (crawlResult == null) return;

            try
            {
                var pageResults = await _taskDataService.GetPageResultsByResultIdAsync(crawlResult.Id);
                foreach (var item in pageResults)
                {
                    CurrentPageResults.Add(PageResultDisplayInfo.FromEntity(item));
                }
            }
            catch (Exception ex)
            {
                CrawlerCore.Infrastructure.LogHelper.Error(ex, $"加载任务{crawlResult.TaskId}的单页结果失败");
                MessageBox.Show($"加载单页结果失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void OnCrawlResultReceived(object sender, CrawlResultReceivedEventArgs e)
        {
            await Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                await LoadCrawlResultsAsync();
            });
        }

        [RelayCommand(CanExecute = nameof(CanPrevPage))]
        private void PrevPage()
        {
            if (PageIndex > 0)
            {
                PageIndex--;
                OnPropertyChanged(nameof(DisplayPageIndex));
                _ = LoadCrawlResultsAsync();
            }
        }

        private bool CanPrevPage() => PageIndex > 0;

        [RelayCommand(CanExecute = nameof(CanNextPage))]
        private void NextPage()
        {
            if (PageIndex < (TotalCount / PageSize))
            {
                PageIndex++;
                OnPropertyChanged(nameof(DisplayPageIndex));
                _ = LoadCrawlResultsAsync();
            }
        }

        private bool CanNextPage() => PageIndex < (TotalCount / PageSize);

        [RelayCommand]
        private void Search()
        {
            PageIndex = 0;
            OnPropertyChanged(nameof(DisplayPageIndex));
            _ = LoadCrawlResultsAsync();
        }
        #endregion

        #region 双击查看ParsedData详情
        [RelayCommand]
        private void ShowPageDataDetail()
        {
            if (SelectedPageResult == null) return;

            try
            {
                var detailWindow = new Views.PageDataDetailWindow(SelectedPageResult)
                {
                    Owner = Application.Current.MainWindow
                };
                detailWindow.ShowDialog();
            }
            catch (Exception ex)
            {
                CrawlerCore.Infrastructure.LogHelper.Error(ex, "打开页面详情弹窗失败");
                MessageBox.Show($"打开详情失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        #endregion

        #region 导出JSON数据
        [RelayCommand]
        private async Task ExportAsync()
        {
            // 校验选中项
            if (SelectedCrawlResult == null && SelectedPageResult == null)
            {
                MessageBox.Show("请先选中要导出的爬取任务或单页结果", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                // 1. 选择导出范围
                string exportType;
                object exportData;
                string defaultFileName;

                if (SelectedCrawlResult != null && SelectedPageResult != null)
                {
                    // 两者都选中，让用户选择
                    var selectResult = MessageBox.Show(
                        "检测到您同时选中了爬取任务和单页结果\n\n点击「是」导出整个任务的所有页面数据\n点击「否」仅导出当前选中的单页数据",
                        "选择导出范围",
                        MessageBoxButton.YesNoCancel,
                        MessageBoxImage.Question);

                    if (selectResult == MessageBoxResult.Cancel) return;
                    if (selectResult == MessageBoxResult.Yes)
                    {
                        (exportData, defaultFileName) = await BuildTaskExportDataAsync(SelectedCrawlResult);
                        exportType = "任务全量数据";
                    }
                    else
                    {
                        exportData = BuildPageExportData(SelectedPageResult);
                        defaultFileName = $"单页数据_{SelectedPageResult.Id}_{DateTime.Now:yyyyMMddHHmmss}";
                        exportType = "单页数据";
                    }
                }
                else if (SelectedCrawlResult != null)
                {
                    (exportData, defaultFileName) = await BuildTaskExportDataAsync(SelectedCrawlResult);
                    exportType = "任务全量数据";
                }
                else
                {
                    exportData = BuildPageExportData(SelectedPageResult!);
                    defaultFileName = $"单页数据_{SelectedPageResult!.Id}_{DateTime.Now:yyyyMMddHHmmss}";
                    exportType = "单页数据";
                }

                // 2. 选择保存路径
                var saveDialog = new SaveFileDialog
                {
                    Title = $"导出{exportType}",
                    FileName = $"{defaultFileName}.json",
                    Filter = "JSON文件 (*.json)|*.json|所有文件 (*.*)|*.*",
                    DefaultExt = ".json",
                    RestoreDirectory = true
                };

                if (saveDialog.ShowDialog() != true) return;

                // 3. 序列化并写入文件
                var json = JsonSerializer.Serialize(exportData, _jsonSerializerOptions);
                await File.WriteAllTextAsync(saveDialog.FileName, json);

                CrawlerCore.Infrastructure.LogHelper.Info($"导出{exportType}成功，保存路径：{saveDialog.FileName}");
                MessageBox.Show($"导出成功！文件已保存至：\n{saveDialog.FileName}", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                CrawlerCore.Infrastructure.LogHelper.Error(ex, "导出数据失败");
                MessageBox.Show($"导出失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #region 导出数据构建辅助方法
        /// <summary>
        /// 构建任务全量导出数据
        /// </summary>
        private async Task<(object Data, string FileName)> BuildTaskExportDataAsync(CrawlResultDisplayInfo crawlResult)
        {
            // 获取完整的页面数据
            var pageResults = await _taskDataService.GetPageResultsByResultIdAsync(crawlResult.Id);
            var pageExportList = new List<object>();

            foreach (var page in pageResults)
            {
                pageExportList.Add(new
                {
                    page.Id,
                    page.Url,
                    page.FinalUrl,
                    page.StatusCode,
                    StatusDescription = page.StatusCode switch
                    {
                        200 => "成功",
                        404 => "页面不存在",
                        500 => "服务器错误",
                        403 => "访问被拒绝",
                        _ => "其他"
                    },
                    LinkCount = page.Links?.Count ?? 0,
                    page.ParsedData,
                    page.Links
                });
            }

            var exportData = new
            {
                TaskId = crawlResult.TaskId,
                TaskName = crawlResult.TaskName,
                TotalPages = crawlResult.TotalPages,
                CompletedAt = crawlResult.CompletedAt,
                ExportTime = DateTime.Now,
                PageResults = pageExportList
            };

            var fileName = $"任务数据_{crawlResult.TaskName}_{crawlResult.TaskId.Substring(0, 8)}_{DateTime.Now:yyyyMMddHHmmss}";
            return (exportData, fileName);
        }

        /// <summary>
        /// 构建单页导出数据
        /// </summary>
        private object BuildPageExportData(PageResultDisplayInfo pageResult)
        {
            return new
            {
                pageResult.Id,
                pageResult.Url,
                pageResult.FinalUrl,
                pageResult.StatusCode,
                pageResult.StatusDescription,
                pageResult.LinkCount,
                pageResult.ParsedData,
                pageResult.RawEntity.Links,
                ExportTime = DateTime.Now
            };
        }
        #endregion
        #endregion

        #region IDisposable实现
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_isDisposed) return;

            if (disposing)
            {
                _eventAggregator.Unsubscribe<CrawlResultReceivedEventArgs>(_crawlResultSubscriptionId);
            }

            _isDisposed = true;
        }

        ~DataViewerViewModel()
        {
            Dispose(false);
        }
        #endregion
    }
}