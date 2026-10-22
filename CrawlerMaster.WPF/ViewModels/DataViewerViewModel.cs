using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CrawlerCore.Common;
using CrawlerCore.Entities;
using CrawlerCore.Events;
using CrawlerCore.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows;

namespace CrawlerMaster.WPF.ViewModels
{
    public partial class DataViewerViewModel : ObservableObject, IDisposable
    {
        #region 依赖注入
        // ViewModel 是单例，不能直接持有 Scoped 的 ITaskDataService（其 DbContext 会被多处并发使用），每次访问创建作用域
        private readonly IServiceScopeFactory _scopeFactory;
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
        [NotifyCanExecuteChangedFor(nameof(PrevPageCommand), nameof(NextPageCommand))]
        private int _pageIndex = 0;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
        private int _pageSize = 20;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
        private int _totalCount;

        [ObservableProperty]
        private string _searchKeyword = string.Empty;

        public int DisplayPageIndex => PageIndex + 1;
        #endregion

        #region 私有字段
        private Guid _crawlResultSubscriptionId;
        private bool _isDisposed = false;
        // 列表加载版本号：并发加载时只采用最后一次请求的结果（仅在 UI 线程读写）
        private int _crawlResultsLoadVersion;
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
        public DataViewerViewModel(IServiceScopeFactory scopeFactory, IEventAggregator eventAggregator)
        {
            _scopeFactory = scopeFactory;
            _eventAggregator = eventAggregator;

            _crawlResultSubscriptionId = _eventAggregator.Subscribe<CrawlResultReceivedEventArgs>(OnCrawlResultReceived);

            _ = LoadCrawlResultsAsync();
        }
        #endregion

        #region 核心方法
        [RelayCommand]
        public async Task LoadCrawlResultsAsync()
        {
            var loadVersion = ++_crawlResultsLoadVersion;
            IsLoading = true;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var taskDataService = scope.ServiceProvider.GetRequiredService<ITaskDataService>();
                var (items, totalCount) = await taskDataService.GetPagedCrawlResultProjectionsAsync(
                    PageIndex,
                    PageSize,
                    SearchKeyword.Trim());

                // 期间又发起了新的加载（翻页/搜索/新结果到达），丢弃本次过期结果
                if (loadVersion != _crawlResultsLoadVersion) return;

                TotalCount = totalCount;

                // 刷新后恢复原选中项，避免新结果到达时右侧单页列表被清空
                var selectedId = SelectedCrawlResult?.Id;
                CrawlResults.Clear();
                foreach (var projection in items)
                {
                    CrawlResults.Add(CrawlResultDisplayInfo.FromProjection(projection));
                }
                if (selectedId != null)
                {
                    SelectedCrawlResult = CrawlResults.FirstOrDefault(r => r.Id == selectedId);
                }
            }
            catch (Exception ex)
            {
                CrawlerCore.Infrastructure.LogHelper.Error(ex, "加载爬取结果列表失败");
                MessageBox.Show($"加载爬取结果失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (loadVersion == _crawlResultsLoadVersion)
                {
                    IsLoading = false;
                }
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
                using var scope = _scopeFactory.CreateScope();
                var taskDataService = scope.ServiceProvider.GetRequiredService<ITaskDataService>();
                var pageResults = await taskDataService.GetPageResultsByResultIdAsync(crawlResult.Id);

                // 加载期间用户已切换到其他任务：丢弃过期结果，否则两个任务的单页数据会混在一起
                if (!ReferenceEquals(SelectedCrawlResult, crawlResult)) return;

                CurrentPageResults.Clear();
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
            if (CanNextPage())
            {
                PageIndex++;
                OnPropertyChanged(nameof(DisplayPageIndex));
                _ = LoadCrawlResultsAsync();
            }
        }

        // 原写法 PageIndex < TotalCount / PageSize 在总数恰为页大小整数倍时会多出一个空页
        private bool CanNextPage() => (PageIndex + 1) * PageSize < TotalCount;

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
            using var scope = _scopeFactory.CreateScope();
            var taskDataService = scope.ServiceProvider.GetRequiredService<ITaskDataService>();
            var pageResults = await taskDataService.GetPageResultsByResultIdAsync(crawlResult.Id);
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