using CrawlerCore.Entities;
using CrawlerCore.Events;
using CrawlerCore.Interfaces;
using CrawlerMaster.WPF.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TaskStatus = CrawlerCore.Entities.TaskStatus;

namespace CrawlerCore.Tests.Services;

/// <summary>
/// 会话管理、事件聚合器、健康检查的单元测试（T4：M-03、M-05③、M-13）
/// </summary>
public class SessionAndEventTests
{
    #region 会话管理（M-03）
    [Fact(DisplayName = "替换会话：旧会话失效，新会话有效")]
    public void ReplaceSession_InvalidatesOldSession()
    {
        var manager = new SessionManager(NullLogger<SessionManager>.Instance);
        var first = manager.ReplaceSession("node-1", maxConcurrentTasks: 2);

        var second = manager.ReplaceSession("node-1", maxConcurrentTasks: 4);

        Assert.NotEqual(first, second);
        Assert.False(manager.ValidateSession("node-1", first));
        Assert.True(manager.ValidateSession("node-1", second));
        Assert.Equal(4, manager.GetMaxConcurrentTasks("node-1"));
    }

    [Fact(DisplayName = "按会话ID移除：只移除仍是该会话的记录，不影响并发注册产生的新会话")]
    public void RemoveSessionWithId_OnlyRemovesMatchingSession()
    {
        var manager = new SessionManager(NullLogger<SessionManager>.Instance);
        var old = manager.ReplaceSession("node-1");
        var current = manager.ReplaceSession("node-1");

        Assert.False(manager.RemoveSession("node-1", old));
        Assert.True(manager.ValidateSession("node-1", current));
        Assert.True(manager.RemoveSession("node-1", current));
        Assert.False(manager.ValidateSession("node-1", current));
    }

    [Fact(DisplayName = "过期清理：清理过期且没有活跃流的会话，有活跃流的保留")]
    public void CleanupExpiredSessions_SkipsActiveStreams()
    {
        var manager = new SessionManager(NullLogger<SessionManager>.Instance);
        var active = manager.ReplaceSession("active-node");
        var idle = manager.ReplaceSession("idle-node");
        manager.ActiveStreamProbe = nodeId => nodeId == "active-node";

        var removed = manager.CleanupExpiredSessions(DateTime.UtcNow.AddHours(2));

        Assert.Equal(1, removed);
        Assert.True(manager.ValidateSession("active-node", active));
        Assert.False(manager.ValidateSession("idle-node", idle));
    }

    [Fact(DisplayName = "过期清理：未过期的会话不清理")]
    public void CleanupExpiredSessions_KeepsFreshSessions()
    {
        var manager = new SessionManager(NullLogger<SessionManager>.Instance);
        var session = manager.ReplaceSession("node-1");

        Assert.Equal(0, manager.CleanupExpiredSessions(DateTime.UtcNow));
        Assert.True(manager.ValidateSession("node-1", session));
    }
    #endregion

    #region 事件聚合器（M-13）
    [Fact(DisplayName = "一个订阅者抛异常时，其他订阅者仍会被调用，异常不传给发布方")]
    public void Publish_SubscriberFailure_DoesNotAffectOthers()
    {
        var aggregator = new EventAggregator();
        var received = new List<string>();
        aggregator.Subscribe<TaskProgressUpdatedEventArgs>((_, _) => received.Add("第一个"));
        aggregator.Subscribe<TaskProgressUpdatedEventArgs>((_, _) => throw new InvalidOperationException("订阅者出错"));
        aggregator.Subscribe<TaskProgressUpdatedEventArgs>((_, _) => received.Add("第三个"));

        var exception = Record.Exception(() => aggregator.Publish(this, new TaskProgressUpdatedEventArgs("t", "n", TaskStatus.Running, 0, 0, "node")));

        Assert.Null(exception);
        Assert.Equal(2, received.Count);
        Assert.Contains("第一个", received);
        Assert.Contains("第三个", received);
    }
    #endregion

    #region 健康检查（M-05③）
    [Fact(DisplayName = "健康检查判定离线的节点，其双向流被主动断开")]
    public async Task HealthCheck_DisconnectsNodesMarkedOffline()
    {
        var nodeService = new Mock<INodeManagementService>();
        nodeService.Setup(s => s.MarkInactiveNodesAsOfflineAsync(It.IsAny<TimeSpan>()))
            .ReturnsAsync(new[] { "lost-node" });
        var coordinator = new Mock<ICrawlerCoordinatorService>();

        var services = new ServiceCollection();
        services.AddScoped(_ => nodeService.Object);
        using var provider = services.BuildServiceProvider();
        using var healthCheck = new NodeHealthCheckService(provider, new EventAggregator(), null, coordinator.Object);

        await healthCheck.CheckOnceAsync();

        coordinator.Verify(c => c.DisconnectNode("lost-node"), Times.Once);
    }

    [Fact(DisplayName = "健康检查没有发现超时节点时不断开任何流")]
    public async Task HealthCheck_NoOfflineNodes_DisconnectsNothing()
    {
        var nodeService = new Mock<INodeManagementService>();
        nodeService.Setup(s => s.MarkInactiveNodesAsOfflineAsync(It.IsAny<TimeSpan>()))
            .ReturnsAsync(Array.Empty<string>());
        var coordinator = new Mock<ICrawlerCoordinatorService>();

        var services = new ServiceCollection();
        services.AddScoped(_ => nodeService.Object);
        using var provider = services.BuildServiceProvider();
        using var healthCheck = new NodeHealthCheckService(provider, new EventAggregator(), null, coordinator.Object);

        await healthCheck.CheckOnceAsync();

        coordinator.Verify(c => c.DisconnectNode(It.IsAny<string>()), Times.Never);
    }
    #endregion
}
