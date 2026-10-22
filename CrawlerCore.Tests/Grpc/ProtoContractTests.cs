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
                "configuration", "max_depth", "max_pages", "status", "priority"
            };

            // Assert：无额外字段
            Assert.All(officialFields, f => Assert.Contains(f, fieldNames));
            Assert.Equal(officialFields.Count, fieldNames.Count);
        }

        [Fact]
        public void NodeRegistration_MaxConcurrentTasks_IsBackwardCompatibleExtension()
        {
            // T4 在末尾追加 max_concurrent_tasks = 6（M-06），原有字段编号不变
            var fields = NodeRegistration.Descriptor.Fields.InFieldNumberOrder()
                .Select(f => (f.FieldNumber, f.Name)).ToList();
            Assert.Equal(new[]
            {
                (1, "node_id"), (2, "machine_name"), (3, "cpu_cores"),
                (4, "total_memory_gb"), (5, "ip_address"), (6, "max_concurrent_tasks")
            }, fields);

            // 新字段往返序列化
            var request = new NodeRegistration { NodeId = "n", MaxConcurrentTasks = 2 };
            Assert.Equal(2, NodeRegistration.Parser.ParseFrom(request.ToByteArray()).MaxConcurrentTasks);

            // 旧版 Worker 不写该字段：解析结果为 0，Master 视为不限
            var legacy = new NodeRegistration { NodeId = "legacy", MachineName = "m", CpuCores = 4, TotalMemoryGb = 8, IpAddress = "127.0.0.1" };
            Assert.Equal(0, NodeRegistration.Parser.ParseFrom(legacy.ToByteArray()).MaxConcurrentTasks);
        }

        [Fact]
        public void FromMaster_ResultAck_IsBackwardCompatibleExtension()
        {
            // T4 在 oneof 末尾追加 result_ack = 4，原有分支编号不变
            var fields = FromMaster.Descriptor.Fields.InFieldNumberOrder()
                .Select(f => (f.FieldNumber, f.Name)).ToList();
            Assert.Equal(new[] { (1, "new_task"), (2, "task_control"), (3, "system_config"), (4, "result_ack") }, fields);

            var ackFields = ResultAck.Descriptor.Fields.InFieldNumberOrder()
                .Select(f => (f.FieldNumber, f.Name)).ToList();
            Assert.Equal(new[] { (1, "task_id"), (2, "accepted") }, ackFields);

            var message = new FromMaster { ResultAck = new ResultAck { TaskId = "task-1", Accepted = true } };
            var parsed = FromMaster.Parser.ParseFrom(message.ToByteArray());
            Assert.Equal(FromMaster.MessageOneofCase.ResultAck, parsed.MessageCase);
            Assert.Equal("task-1", parsed.ResultAck.TaskId);
            Assert.True(parsed.ResultAck.Accepted);
        }

        [Fact]
        public void FromMaster_UnknownOneofBranch_IsPreservedAsUnknownField()
        {
            // 旧版 Worker 不认识的分支（例如将来追加的字段）解析为 MessageCase.None，不会抛异常。
            // 用一个只含未知字段的消息模拟：字段 99、varint 类型（标签 99<<3 = 792，varint 编码为 0x98 0x06），值为 1
            var bytes = new byte[] { 0x98, 0x06, 0x01 };
            var parsed = FromMaster.Parser.ParseFrom(bytes);
            Assert.Equal(FromMaster.MessageOneofCase.None, parsed.MessageCase);
        }
    }
}