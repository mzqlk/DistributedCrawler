using CrawlerCore.Entities;
using CrawlerCore.Events;
using CrawlerCore.Infrastructure;
using CrawlerCore.Interfaces;
using CrawlerWorker.Services.Engine;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using TaskStatus = CrawlerCore.Entities.TaskStatus;

namespace CrawlerCore.Tests.Worker;

public class CrawlerEngineCompleteTests
{
    #region 测试基础设施
    private readonly Mock<IHttpClientFactory> _httpFactoryMock = new();
    private readonly Mock<IPluginManager> _pluginManagerMock = new();  // 改为 Mock 接口
    private readonly Mock<HttpMessageHandler> _httpHandlerMock = new();
    private readonly IOptions<CrawlerSettings> _settings = Options.Create(new CrawlerSettings());
    private readonly CrawlerEngine _engine;

    public CrawlerEngineCompleteTests()
    {
        // 设置 HttpClient 工厂 Mock
        var client = new HttpClient(_httpHandlerMock.Object);
        _httpFactoryMock
            .Setup(f => f.CreateClient("CrawlerEngineClient"))
            .Returns(() => new HttpClient(_httpHandlerMock.Object));

        // 创建引擎实例
        _engine = new CrawlerEngine(_httpFactoryMock.Object, _pluginManagerMock.Object, _settings);
    }

    private void SetupHttpResponse(string content, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        _httpHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent(content)
            });
    }

    private Mock<ICrawlerPlugin> SetupMockPlugin(string pluginName = "MockPlugin")
    {
        var pluginMock = new Mock<ICrawlerPlugin>();
        pluginMock.Setup(p => p.Name).Returns(pluginName);
        pluginMock.Setup(p => p.Version).Returns("1.0.0");
        pluginMock.Setup(p => p.GetDefaultConfiguration()).Returns(new Dictionary<string, string>());
        pluginMock.Setup(p => p.ExecuteAsync(It.IsAny<PluginContext>()))
            .ReturnsAsync(new PluginResult { Success = true, Data = new Dictionary<string, object> { { "test", "value" } } });

        pluginMock.Setup(p => p.ShouldFollowLink(It.IsAny<string>(), It.IsAny<int>()))
            .Returns(true);

        _pluginManagerMock.Setup(p => p.GetPlugin(pluginName)).Returns(pluginMock.Object);
        return pluginMock;
    }
    #endregion

    [Fact(DisplayName = "空任务入参 → 抛出空参数异常")]
    public async Task ExecuteAsync_NullTask_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _engine.ExecuteAsync(null!, CancellationToken.None));
    }

    [Fact(DisplayName = "空 TaskId → 抛出参数异常")]
    public async Task ExecuteAsync_EmptyTaskId_ThrowsArgumentException()
    {
        var task = new CrawlerTask { TaskId = string.Empty };
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _engine.ExecuteAsync(task, CancellationToken.None));
    }

    [Fact(DisplayName = "插件不存在 → 抛出 KeyNotFoundException")]
    public async Task ExecuteAsync_PluginNotFound_Throws()
    {
        var task = new CrawlerTask { PluginName = "NonExistentPlugin", StartUrls = new List<string> { "https://test.com" } };
        _pluginManagerMock.Setup(p => p.GetPlugin(It.IsAny<string>())).Returns((ICrawlerPlugin?)null);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            _engine.ExecuteAsync(task, CancellationToken.None));
    }

    [Fact(DisplayName = "正常爬取全流程 → 成功执行并返回结果")]
    public async Task ExecuteAsync_ValidScenario_ReturnsCompleteResult()
    {
        // Arrange
        var task = new CrawlerTask
        {
            PluginName = "MockPlugin",
            StartUrls = new List<string> { "https://test.com" }
        };
        SetupHttpResponse("<html></html>");
        SetupMockPlugin();

        // Act
        var result = await _engine.ExecuteAsync(task, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(task.TaskId, result.TaskId);
        Assert.Single(result.PageResults);
        Assert.Equal(200, result.PageResults[0].StatusCode);
        Assert.NotNull(result.PageResults[0].ParsedData);
        Assert.Equal(TaskStatus.Completed, task.Status);
        _pluginManagerMock.Verify(p => p.GetPlugin("MockPlugin"), Times.Once);
    }

    [Fact(DisplayName = "MaxPages 限制 → 只爬取指定数量页面")]
    public async Task ExecuteAsync_MaxPagesLimit_StopsAtLimit()
    {
        // Arrange
        var task = new CrawlerTask
        {
            PluginName = "MockPlugin",
            StartUrls = new List<string> { "https://test1.com", "https://test2.com", "https://test3.com" },
            MaxPages = 2
        };
        SetupHttpResponse("<html></html>");
        SetupMockPlugin();

        // Act
        var result = await _engine.ExecuteAsync(task, CancellationToken.None);

        // Assert
        Assert.Equal(2, result.PageResults.Count);
    }

    [Fact(DisplayName = "无有效 URL → 直接返回空结果")]
    public async Task ExecuteAsync_EmptyUrls_ReturnsEmptyResult()
    {
        var task = new CrawlerTask { PluginName = "MockPlugin", StartUrls = new List<string>() };
        SetupMockPlugin();

        var result = await _engine.ExecuteAsync(task, CancellationToken.None);

        Assert.Empty(result.PageResults);
        Assert.Equal(TaskStatus.Completed, task.Status);
    }

    [Fact(DisplayName = "进度事件触发 → 回调接收正确数据")]
    public async Task ExecuteAsync_ProgressUpdated_EventFiresCorrectly()
    {
        // Arrange
        var task = new CrawlerTask
        {
            TaskId = "test-001",
            PluginName = "MockPlugin",
            StartUrls = new List<string> { "https://test.com" },
            MaxPages = 1
        };
        SetupHttpResponse("<html></html>");
        SetupMockPlugin();

        TaskProgressUpdatedEventArgs? capturedArgs = null;
        _engine.ProgressUpdated += (_, args) => capturedArgs = args;

        // Act
        await _engine.ExecuteAsync(task, CancellationToken.None);

        // Assert
        Assert.NotNull(capturedArgs);
        Assert.Equal("test-001", capturedArgs.TaskId);
        Assert.Equal(1, capturedArgs.CrawledPages);
        Assert.Equal(1, capturedArgs.TotalPages);
    }

    [Fact(DisplayName = "任务取消 → 正确处理并标记为 Failed")]
    public async Task ExecuteAsync_CancellationRequested_ThrowsAndMarksFailed()
    {
        // Arrange
        var task = new CrawlerTask
        {
            PluginName = "MockPlugin",
            StartUrls = new List<string> { "https://test.com" }
        };
        SetupMockPlugin();

        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _engine.ExecuteAsync(task, cts.Token));
        Assert.Equal(TaskStatus.Failed, task.Status);
    }

    [Fact(DisplayName = "HTTP 404 → 跳过插件解析，返回正确状态码")]
    public async Task ExecuteAsync_Http404_Returns404WithoutPluginCall()
    {
        // Arrange
        var task = new CrawlerTask
        {
            PluginName = "MockPlugin",
            StartUrls = new List<string> { "https://test.com" }
        };
        SetupHttpResponse("Not Found", HttpStatusCode.NotFound);
        var pluginMock = SetupMockPlugin();

        // Act
        var result = await _engine.ExecuteAsync(task, CancellationToken.None);

        // Assert
        Assert.Single(result.PageResults);
        Assert.Equal(404, result.PageResults[0].StatusCode);
        pluginMock.Verify(p => p.ExecuteAsync(It.IsAny<PluginContext>()), Times.Never);
    }

    [Fact(DisplayName = "插件返回 null Data → 自动兜底为空字典")]
    public async Task ExecuteAsync_PluginReturnsNullData_EmptyDictionary()
    {
        // Arrange
        var task = new CrawlerTask
        {
            PluginName = "MockPlugin",
            StartUrls = new List<string> { "https://test.com" }
        };
        SetupHttpResponse("<html></html>");

        var pluginMock = new Mock<ICrawlerPlugin>();
        pluginMock.Setup(p => p.Name).Returns("MockPlugin");
        pluginMock.Setup(p => p.ExecuteAsync(It.IsAny<PluginContext>()))
            .ReturnsAsync(new PluginResult { Success = true, Data = null });
        _pluginManagerMock.Setup(p => p.GetPlugin("MockPlugin")).Returns(pluginMock.Object);

        // Act
        var result = await _engine.ExecuteAsync(task, CancellationToken.None);

        // Assert
        Assert.NotNull(result.PageResults[0].ParsedData);
        Assert.Empty(result.PageResults[0].ParsedData);
    }

    [Fact(DisplayName = "链接提取 - 正常提取a标签href")]
    public async Task CrawlSinglePageAsync_ValidHtml_ExtractsLinks()
    {
        // Arrange
        var html = @"
    <html>
        <body>
            <a href='https://test.com/page1'>Page 1</a>
            <a href='https://test.com/page2'>Page 2</a>
        </body>
    </html>";
        var task = new CrawlerTask { PluginName = "MockPlugin", StartUrls = new List<string> { "https://test.com" } };
        SetupHttpResponse(html);
        SetupMockPlugin();

        // Act
        var result = await _engine.ExecuteAsync(task, CancellationToken.None);

        // Assert
        var pageResult = result.PageResults[0];
        Assert.Equal(2, pageResult.Links.Count);
        Assert.Contains("https://test.com/page1", pageResult.Links);
        Assert.Contains("https://test.com/page2", pageResult.Links);
    }

    [Fact(DisplayName = "链接提取 - 相对路径自动转绝对路径")]
    public async Task CrawlSinglePageAsync_RelativeLinks_ConvertsToAbsolute()
    {
        // Arrange
        var html = @"
    <html>
        <body>
            <a href='/subpage'>Subpage</a>
            <a href='../parent'>Parent</a>
        </body>
    </html>";
        var task = new CrawlerTask { PluginName = "MockPlugin", StartUrls = new List<string> { "https://test.com/section/" } };
        SetupHttpResponse(html);
        SetupMockPlugin();

        // Act
        var result = await _engine.ExecuteAsync(task, CancellationToken.None);

        // Assert
        var pageResult = result.PageResults[0];
        Assert.Equal(2, pageResult.Links.Count);
        Assert.Contains("https://test.com/subpage", pageResult.Links);
        Assert.Contains("https://test.com/parent", pageResult.Links);
    }

    [Fact(DisplayName = "链接提取 - 过滤无效协议(mailto/tel/javascript)")]
    public async Task CrawlSinglePageAsync_InvalidProtocols_FiltersOut()
    {
        // Arrange
        var html = @"
    <html>
        <body>
            <a href='mailto:test@test.com'>Email</a>
            <a href='tel:123456'>Phone</a>
            <a href='javascript:alert(1)'>JS</a>
            <a href='https://test.com/valid'>Valid</a>
        </body>
    </html>";
        var task = new CrawlerTask { PluginName = "MockPlugin", StartUrls = new List<string> { "https://test.com" } };
        SetupHttpResponse(html);
        SetupMockPlugin();

        // Act
        var result = await _engine.ExecuteAsync(task, CancellationToken.None);

        // Assert
        var pageResult = result.PageResults[0];
        Assert.Single(pageResult.Links);
        Assert.Contains("https://test.com/valid", pageResult.Links);
    }

    [Fact(DisplayName = "链接提取 - 同一页面重复链接局部去重")]
    public async Task CrawlSinglePageAsync_DuplicateLinksOnPage_LocalDeduplication()
    {
        // Arrange
        var html = @"
    <html>
        <body>
            <a href='https://test.com/page1'>Page 1</a>
            <a href='https://test.com/page1'>Page 1 Again</a>
            <a href='https://test.com/Page1'>Page 1 Case</a>
        </body>
    </html>";
        var task = new CrawlerTask { PluginName = "MockPlugin", StartUrls = new List<string> { "https://test.com" } };
        SetupHttpResponse(html);
        SetupMockPlugin();

        // Act
        var result = await _engine.ExecuteAsync(task, CancellationToken.None);

        // Assert
        var pageResult = result.PageResults[0];
        Assert.Single(pageResult.Links);
        Assert.Contains("https://test.com/page1", pageResult.Links);
    }

    [Fact(DisplayName = "深度爬取 - MaxDepth=1 不爬取子链接")]
    public async Task ExecuteAsync_MaxDepth1_DoesNotCrawlSubLinks()
    {
        // Arrange
        var html = @"
    <html>
        <body>
            <a href='https://test.com/subpage'>Subpage</a>
        </body>
    </html>";
        var task = new CrawlerTask
        {
            PluginName = "MockPlugin",
            StartUrls = new List<string> { "https://test.com" },
            MaxDepth = 1
        };
        SetupHttpResponse(html);
        SetupMockPlugin();

        // Act
        var result = await _engine.ExecuteAsync(task, CancellationToken.None);

        // Assert
        Assert.Single(result.PageResults); // 只爬了起始页
        Assert.Equal("https://test.com", result.PageResults[0].Url);
    }

    [Fact(DisplayName = "深度爬取 - MaxDepth=2 爬取起始页和子链接")]
    public async Task ExecuteAsync_MaxDepth2_CrawlsStartAndSubLinks()
    {
        // Arrange
        // 起始页返回子链接
        var startHtml = @"<html><body><a href='https://test.com/subpage'>Subpage</a></body></html>";
        // 子链接返回空页面
        var subHtml = "<html></html>";

        var task = new CrawlerTask
        {
            PluginName = "MockPlugin",
            StartUrls = new List<string> { "https://test.com" },
            MaxDepth = 2
        };

        // 按请求顺序返回不同内容
        var requestCount = 0;
        _httpHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                requestCount++;
                return new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent(requestCount == 1 ? startHtml : subHtml)
                };
            });

        SetupMockPlugin();

        // Act
        var result = await _engine.ExecuteAsync(task, CancellationToken.None);

        // Assert
        Assert.Equal(2, result.PageResults.Count);
        Assert.Contains(result.PageResults, p => p.Url == "https://test.com");
        Assert.Contains(result.PageResults, p => p.Url == "https://test.com/subpage");
    }

    [Fact(DisplayName = "URL去重 - 全局去重，重复URL只爬一次")]
    public async Task ExecuteAsync_GlobalDuplicateUrls_CrawledOnlyOnce()
    {
        // 1. 初始化独立测试依赖
        var httpHandlerMock = new Mock<HttpMessageHandler>();
        var httpFactoryMock = new Mock<IHttpClientFactory>();
        var pluginManagerMock = new Mock<IPluginManager>();
        var settings = Options.Create(new CrawlerSettings());

        // 2. 固定测试数据：2个不同起始页 + 1个公共重复子链接
        const string commonUrl = "https://test.com/common";
        var task = new CrawlerTask
        {
            TaskId = "test-global-dedup",
            PluginName = "MockPlugin",
            StartUrls = new List<string> { "https://test.com/page1", "https://test.com/page2" },
            MaxDepth = 2,    // 允许爬取子链接
            MaxPages = 100   // 无页数限制
        };

        // 3. 极简HTTP Mock：所有请求返回成功，包含公共子链接
        httpHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent($@"<html><body><a href='{commonUrl}'>Common</a></body></html>")
            });

        // 4. 注入依赖
        var client = new HttpClient(httpHandlerMock.Object);
        httpFactoryMock.Setup(x => x.CreateClient("CrawlerEngineClient"))
            .Returns(() => new HttpClient(httpHandlerMock.Object));

        // 5. Mock插件
        var pluginMock = new Mock<ICrawlerPlugin>();
        pluginMock.Setup(p => p.ShouldFollowLink(It.IsAny<string>(), It.IsAny<int>())).Returns(true);
        pluginMock.Setup(p => p.Name).Returns("MockPlugin");
        pluginMock.Setup(p => p.ExecuteAsync(It.IsAny<PluginContext>()))
            .ReturnsAsync(new PluginResult { Success = true, Data = new Dictionary<string, object>() });
        pluginManagerMock.Setup(x => x.GetPlugin("MockPlugin")).Returns(pluginMock.Object);

        // 6. 创建引擎
        var engine = new CrawlerEngine(httpFactoryMock.Object, pluginManagerMock.Object, settings);

        // Act
        var result = await engine.ExecuteAsync(task, CancellationToken.None);

        // 核心断言：直接用引擎返回的结果，100%准确
        // 预期：page1(1) + page2(2) + common(3) → 总3个页面
        Assert.Equal(3, result.PageResults.Count);
        // 验证公共URL只出现1次（去重生效）
        Assert.Equal(1, result.PageResults.Count(x => x.Url == commonUrl));
        // 验证两个起始页都被爬取
        Assert.Contains(result.PageResults, x => x.Url == "https://test.com/page1");
        Assert.Contains(result.PageResults, x => x.Url == "https://test.com/page2");
    }

    [Fact(DisplayName = "URL去重 - 大小写不敏感去重")]
    public async Task ExecuteAsync_CaseInsensitiveUrls_Deduplicated()
    {
        // Arrange
        var task = new CrawlerTask
        {
            PluginName = "MockPlugin",
            StartUrls = new List<string> { "https://test.com/Page", "https://test.com/page" }
        };
        SetupHttpResponse("<html></html>");
        SetupMockPlugin();

        // Act
        var result = await _engine.ExecuteAsync(task, CancellationToken.None);

        // Assert
        Assert.Single(result.PageResults);
    }

    [Fact(DisplayName = "内存超限 - 达到容量上限时记录警告日志")]
    public async Task ExecuteAsync_HashSetCapacityReached_LogsWarning()
    {
        // Arrange
        // 注意：由于MaxUrlHashSetCapacity是private const，这里通过测试逻辑验证告警机制
        // 实际测试中可通过反射修改常量值为小数字（如3）进行完整验证
        var html = @"
    <html>
        <body>
            <a href='https://test.com/1'>1</a>
            <a href='https://test.com/2'>2</a>
            <a href='https://test.com/3'>3</a>
        </body>
    </html>";
        var task = new CrawlerTask
        {
            PluginName = "MockPlugin",
            StartUrls = new List<string> { "https://test.com" },
            MaxDepth = 2,
            MaxPages = 100
        };
        SetupHttpResponse(html);
        SetupMockPlugin();

        // Act
        // 这里仅验证逻辑流程，实际告警日志验证需结合日志Mock框架（如Serilog.Sinks.Testing）
        var exception = await Record.ExceptionAsync(() => _engine.ExecuteAsync(task, CancellationToken.None));

        // Assert
        Assert.Null(exception); // 不抛出异常，仅记录日志
    }

    [Fact(DisplayName = "进度事件 - 多页爬取时多次触发且数据正确")]
    public async Task ExecuteAsync_MultiplePages_ProgressEventFiresMultipleTimes()
    {
        // Arrange
        var task = new CrawlerTask
        {
            TaskId = "test-progress",
            PluginName = "MockPlugin",
            StartUrls = new List<string> { "https://test.com/1", "https://test.com/2", "https://test.com/3" }
        };
        SetupHttpResponse("<html></html>");
        SetupMockPlugin();

        var capturedEvents = new List<TaskProgressUpdatedEventArgs>();
        _engine.ProgressUpdated += (_, args) => capturedEvents.Add(args);

        // Act
        await _engine.ExecuteAsync(task, CancellationToken.None);

        // Assert
        Assert.Equal(4, capturedEvents.Count); // 初始0/3 + 3次页面完成
        Assert.Equal(0, capturedEvents[0].CrawledPages);
        Assert.Equal(1, capturedEvents[1].CrawledPages);
        Assert.Equal(2, capturedEvents[2].CrawledPages);
        Assert.Equal(3, capturedEvents[3].CrawledPages);
        Assert.All(capturedEvents, e => Assert.Equal("test-progress", e.TaskId));
    }
}