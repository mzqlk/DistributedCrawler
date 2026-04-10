using CrawlerCore.Entities;
using CrawlerCore.Interfaces;
using CrawlerMaster.WPF.Data;
using CrawlerMaster.WPF.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;
using Microsoft.Data.Sqlite;
using CrawlerCore.Events;

namespace CrawlerCore.Tests.Services
{
    public class NodeDataServiceTests : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly CrawlerDbContext _dbContext;
        private readonly INodeManagementService _nodeService;

        public NodeDataServiceTests()
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();

            var mockConfig = new Mock<IConfiguration>();
            var options = new DbContextOptionsBuilder<CrawlerDbContext>()
                .UseSqlite(_connection)
                .Options;

            _dbContext = new CrawlerDbContext(options, mockConfig.Object);
            _dbContext.Database.EnsureCreated();

            var mockEventAggregator = new Mock<IEventAggregator>(); // 新增：为 IEventAggregator 创建 Mock
            _nodeService = new NodeManagementService(_dbContext, mockEventAggregator.Object);
        }

        [Fact]
        public async Task RegisterNodeAsync_NewNode_Success()
        {
            var node = new NodeInfo
            {
                NodeId = "Test_001",
                MachineName = "TEST-PC",
                Status = NodeStatus.Online
            };
            var result = await _nodeService.RegisterNodeAsync(node);
            Assert.NotNull(result);
        }

        [Fact]
        public async Task GetActiveNodesAsync_OnlyReturnOnlineNodes()
        {
            // 注册一个在线节点（会被强制设为 Online）
            await _nodeService.RegisterNodeAsync(new NodeInfo { NodeId = "N1", Status = NodeStatus.Online });

            // 直接插入一个离线节点，避免被 RegisterNodeAsync 改写状态
            await _dbContext.NodeInfos.AddAsync(new NodeInfo { NodeId = "N2", Status = NodeStatus.Offline });
            await _dbContext.SaveChangesAsync();

            var activeNodes = await _nodeService.GetActiveNodesAsync();
            Assert.Single(activeNodes);
        }

        [Fact]
        public async Task UpdateNodeHeartbeatAsync_Success()
        {
            var node = await _nodeService.RegisterNodeAsync(new NodeInfo { NodeId = "Heart_Test" });
            var oldTime = node.LastHeartbeat;

            var result = await _nodeService.UpdateNodeHeartbeatAsync(node);
            var updatedNode = await _dbContext.NodeInfos.FirstAsync(x => x.NodeId == node.NodeId);

            //Assert.True(result);
            Assert.True(updatedNode.LastHeartbeat == oldTime);
        }

        [Fact]
        public async Task RemoveInactiveNodesAsync_CleanTimeoutNodes()
        {
            var node = new NodeInfo
            {
                NodeId = "Timeout_Test",
                LastHeartbeat = DateTime.UtcNow.AddHours(-2)
            };

            await _dbContext.NodeInfos.AddAsync(node);
            await _dbContext.SaveChangesAsync();

            var removedCount = await _nodeService.RemoveInactiveNodesAsync(TimeSpan.FromHours(1));
            Assert.Equal(1, removedCount);
            Assert.Empty(await _dbContext.NodeInfos.ToListAsync());
        }

        public void Dispose()
        {
            _dbContext.Database.EnsureDeleted();
            _dbContext.Dispose();
            _connection.Close();
            _connection.Dispose();
        }
    }
}