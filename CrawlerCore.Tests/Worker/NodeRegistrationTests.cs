using CrawlerCore.Infrastructure;
using CrawlerWorker.Services;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrawlerCore.Tests.Worker;

/// <summary>
/// Worker节点注册流程单元测试，对齐原项目测试规范
/// </summary>
public class NodeRegistrationTests
{
    private readonly NodeInfoCollector _nodeInfoCollector;

    private static NodeInfoCollector CreateCollector(int maxConcurrentTasks = 2) => new(
        NullLogger<NodeInfoCollector>.Instance,
        new WorkerSessionManager(NullLogger<WorkerSessionManager>.Instance),
        Options.Create(new GrpcSettings { MasterEndpoint = "http://localhost:5001" }),
        Options.Create(new CrawlerSettings { MaxConcurrentTasks = maxConcurrentTasks }));

    public NodeRegistrationTests()
    {
        _nodeInfoCollector = CreateCollector();
    }

    [Fact]
    public void GetNodeId_UseEnvironmentVariable_WhenSet()
    {
        // Arrange
        var expectedNodeId = "worker-fixed-001";
        Environment.SetEnvironmentVariable("MASTER_NODE_ID", expectedNodeId);
        var collector = CreateCollector();

        // Act
        var nodeId = collector.GetNodeId();

        // Assert
        Assert.Equal(expectedNodeId, nodeId);

        // Cleanup
        Environment.SetEnvironmentVariable("MASTER_NODE_ID", null);
    }

    [Fact]
    public void GetNodeId_UseMachineName_WhenEnvironmentVariableNotSet()
    {
        // Arrange
        Environment.SetEnvironmentVariable("MASTER_NODE_ID", null);
        var expectedMachineName = Environment.MachineName;

        // Act
        var nodeId = _nodeInfoCollector.GetNodeId();

        // Assert
        Assert.Equal(expectedMachineName, nodeId);
    }

    [Fact]
    public void GetNodeId_TruncateLongId_WhenExceedsMaxLength()
    {
        // Arrange
        var longNodeId = new string('a', 200);
        Environment.SetEnvironmentVariable("MASTER_NODE_ID", longNodeId);
        var collector = CreateCollector();

        // Act
        var nodeId = collector.GetNodeId();

        // Assert
        Assert.Equal(128, nodeId.Length);

        // Cleanup
        Environment.SetEnvironmentVariable("MASTER_NODE_ID", null);
    }

    [Fact]
    public void GetNodeId_FilterInvalidChars_WhenContainsSpecialChars()
    {
        // Arrange
        var invalidNodeId = "worker@#$%^&*()_+123";
        Environment.SetEnvironmentVariable("MASTER_NODE_ID", invalidNodeId);
        var collector = CreateCollector();
        var expectedNodeId = "worker___________123";

        // Act
        var nodeId = collector.GetNodeId();

        // Assert
        Assert.Equal(expectedNodeId, nodeId);

        // Cleanup
        Environment.SetEnvironmentVariable("MASTER_NODE_ID", null);
    }

    [Fact]
    public async Task CollectNodeRegistrationAsync_ReturnsValidObject_WithNoMissingFields()
    {
        // Act
        var registration = await _nodeInfoCollector.CollectNodeRegistrationAsync();

        // Assert
        Assert.NotNull(registration);
        Assert.False(string.IsNullOrWhiteSpace(registration.NodeId));
        Assert.False(string.IsNullOrWhiteSpace(registration.MachineName));
        Assert.True(registration.CpuCores > 0);
        Assert.True(registration.TotalMemoryGb >= 0);
        Assert.False(string.IsNullOrWhiteSpace(registration.IpAddress));
    }

    [Fact(DisplayName = "W-22 ②：注册信息包含本节点的并发上限（CrawlerSettings:MaxConcurrentTasks）")]
    public async Task CollectNodeRegistrationAsync_IncludesMaxConcurrentTasks()
    {
        var registration = await CreateCollector(maxConcurrentTasks: 3).CollectNodeRegistrationAsync();
        Assert.Equal(3, registration.MaxConcurrentTasks);
    }
}
