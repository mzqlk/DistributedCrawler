using CrawlerCore.Protos;
using Google.Protobuf;
using Xunit;
using TaskStatus = CrawlerCore.Protos.TaskStatus;

namespace CrawlerCore.Tests.Grpc
{
    /// <summary>
    /// Proto协议契约测试（验证协议生成正确性，符合工作日11要求）
    /// </summary>
    public class ProtoContractTests
    {
        [Fact]
        public void NodeRegistrationMessage_Serialization_ShouldMatchOfficialDefinition()
        {
            // Arrange：完全使用原项目官方定义的字段，无扩展
            var request = new NodeRegistration
            {
                NodeId = "test-node-001",
                MachineName = "TEST-MACHINE",
                CpuCores = 8,
                TotalMemoryGb = 16.0,
                IpAddress = "127.0.0.1"
            };

            // Act
            var bytes = request.ToByteArray();
            var deserialized = NodeRegistration.Parser.ParseFrom(bytes);

            // Assert：验证所有官方字段序列化正常
            Assert.Equal(request.NodeId, deserialized.NodeId);
            Assert.Equal(request.MachineName, deserialized.MachineName);
            Assert.Equal(request.CpuCores, deserialized.CpuCores);
            Assert.Equal(request.TotalMemoryGb, deserialized.TotalMemoryGb);
            Assert.Equal(request.IpAddress, deserialized.IpAddress);
        }

        [Fact]
        public void HeartbeatMessage_Serialization_ShouldMatchOfficialDefinition()
        {
            // Arrange：完全使用原项目官方定义的字段
            var heartbeat = new Heartbeat
            {
                NodeId = "test-node-001",
                CpuUsage = 20.5,
                MemoryUsageMb = 1024.0,
                DiskUsagePercent = 30.0,
                ActiveTasks = 2,
                Timestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow)
            };

            // Act
            var bytes = heartbeat.ToByteArray();
            var deserialized = Heartbeat.Parser.ParseFrom(bytes);

            // Assert
            Assert.Equal(heartbeat.NodeId, deserialized.NodeId);
            Assert.Equal(heartbeat.CpuUsage, deserialized.CpuUsage);
            Assert.Equal(heartbeat.MemoryUsageMb, deserialized.MemoryUsageMb);
            Assert.Equal(heartbeat.DiskUsagePercent, deserialized.DiskUsagePercent);
            Assert.Equal(heartbeat.ActiveTasks, deserialized.ActiveTasks);
            Assert.Equal(heartbeat.Timestamp, deserialized.Timestamp);
        }

        [Fact]
        public void TaskStatusEnum_ShouldMatchOfficialCSharpEntityDefinition()
        {
            // 验证枚举与原项目C#实体100%对齐
            Assert.Equal(0, (int)TaskStatus.Pending);
            Assert.Equal(1, (int)TaskStatus.Running);
            Assert.Equal(2, (int)TaskStatus.Completed);
            Assert.Equal(3, (int)TaskStatus.Failed);
        }

        [Fact]
        public void CrawlerTaskMessage_ShouldContainOnlyOfficialDefinedFields()
        {
            // 验证未私自添加任何官方未定义的字段
            var task = new CrawlerTask();
            var fieldNames = CrawlerTask.Descriptor.Fields.InFieldNumberOrder().Select(f => f.Name).ToList();

            // 官方定义的字段列表
            var officialFields = new List<string>
            {
                "task_id", "task_name", "start_urls", "plugin_name",
                "configuration", "max_depth", "max_pages", "status"
            };

            // Assert：无额外字段
            Assert.All(officialFields, f => Assert.Contains(f, fieldNames));
            Assert.Equal(officialFields.Count, fieldNames.Count);
        }
    }
}