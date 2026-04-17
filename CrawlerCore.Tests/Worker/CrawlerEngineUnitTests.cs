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
        _httpFactoryMock.Setup(f => f.CreateClient("CrawlerEngineClient")).Returns(client);

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

        _pluginManagerMock.Setup(p => p.GetPlugin(pluginName)).Returns(pluginMock.Object);
        return pluginMock;
    }
    #endregion

    #region P0 核心测试用例
    [Fact(DisplayName = "P0: 空任务入参 → 抛出空参数异常")]
    public async Task ExecuteAsync_NullTask_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _engine.ExecuteAsync(null!, CancellationToken.None));
    }

    [Fact(DisplayName = "P0: 空 TaskId → 抛出参数异常")]
    public async Task ExecuteAsync_EmptyTaskId_ThrowsArgumentException()
    {
        var task = new CrawlerTask { TaskId = string.Empty };
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _engine.ExecuteAsync(task, CancellationToken.None));
    }

    [Fact(DisplayName = "P0: 插件不存在 → 抛出 KeyNotFoundException")]
    public async Task ExecuteAsync_PluginNotFound_Throws()
    {
        var task = new CrawlerTask { PluginName = "NonExistentPlugin", StartUrls = new List<string> { "https://test.com" } };
        _pluginManagerMock.Setup(p => p.GetPlugin(It.IsAny<string>())).Returns((ICrawlerPlugin?)null);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            _engine.ExecuteAsync(task, CancellationToken.None));
    }

    [Fact(DisplayName = "P0: 正常爬取全流程 → 成功执行并返回结果")]
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

    [Fact(DisplayName = "P0: MaxPages 限制 → 只爬取指定数量页面")]
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
    #endregion

    #region P1 重要测试用例
    [Fact(DisplayName = "P1: 无有效 URL → 直接返回空结果")]
    public async Task ExecuteAsync_EmptyUrls_ReturnsEmptyResult()
    {
        var task = new CrawlerTask { PluginName = "MockPlugin", StartUrls = new List<string>() };
        SetupMockPlugin();

        var result = await _engine.ExecuteAsync(task, CancellationToken.None);

        Assert.Empty(result.PageResults);
        Assert.Equal(TaskStatus.Completed, task.Status);
    }

    [Fact(DisplayName = "P1: 进度事件触发 → 回调接收正确数据")]
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

    [Fact(DisplayName = "P1: 任务取消 → 正确处理并标记为 Failed")]
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

    [Fact(DisplayName = "P1: HTTP 404 → 跳过插件解析，返回正确状态码")]
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

    [Fact(DisplayName = "P1: 插件返回 null Data → 自动兜底为空字典")]
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
    #endregion
}