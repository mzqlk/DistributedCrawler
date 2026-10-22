# 分布式爬虫管理模块设计要点

> 来源：`项目文档.docx`、毕业论文第 3–5 章、开发计划中的设计约束。代码片段不再复制（仓库即真源），这里只记录**设计意图、流程和约定**，并标出对应代码文件。图见 `docs/thesis/figures/`。

## 1. 架构

Master-Worker 主从架构（图 [3-1](../thesis/figures/fig3-1.png)、数据流图 [3-2](../thesis/figures/fig3-2.png)）。

- **Master（`CrawlerMaster.WPF`）**：WPF + MVVM 三层（View / ViewModel / Services）。`App.xaml.cs` 用 .NET 通用主机 + `ConfigureWebHostDefaults` 让 Kestrel 同时监听 gRPC(5001, 强制 HTTP/2) 和 SignalR(5003, HTTP/1+2)，WPF 与后台服务共享一个 DI 容器；UI 线程不阻塞。
- **Worker（`CrawlerWorker`）**：Console + `WorkerHostedService`(BackgroundService)。作为 gRPC **客户端**主动连 Master；另开 5002 的 `WorkerControl` 服务端底座（预留，未实现）。
- **通信**：单条 gRPC 双向流承载所有消息，`oneof` 区分类型。
- **组件通信约束**：ViewModel 只通过 `IEventAggregator` 收事件，不直接碰 gRPC/SignalR 底层（阶段 3.2 的硬约束）。

### 设计原则（论文 3.1.1）
高内聚低耦合（DI、接口隔离）；插件化扩展；Master 无状态 + 持久化以便快速恢复；MVVM 便于维护；统一 UTC 时间；配置全部走 `appsettings.json`/环境变量，无硬编码。

## 2. 核心流程

### 2.1 节点注册与建流（时序图 [3-3](../thesis/figures/fig3-3.png)）
1. Worker 启动 → `NodeInfoCollector` 采集静态信息（CPU 核数、总内存、IP、机器名）。
2. Master 端口连通性预检 → `RegisterNode`（带 Token，10s Deadline，失败按配置重试）。
3. Master `GrpcAuthenticationInterceptor` 校验 Token → 校验 NodeId 唯一 → 生成 SessionId → 返回 `RegistrationResponse{session_id, system_config}` → 发布节点注册事件。
4. Worker 携带 `node-id` + `session-id` 头调用 `StartStreaming`；Master 校验会话后绑定流上下文。
5. Worker 按 `HeartbeatIntervalSeconds`(5s) 上报心跳。

### 2.2 任务执行
UI 创建任务（名称、插件、起始 URL、MaxDepth、MaxPages、优先级 1–5）→ `TaskSchedulerHostedService` 入 `PriorityQueue` → `LoadBalancingScheduler.SelectOptimalNode` → 通过双向流发 `FromMaster.NewTask` → Worker 回 `TaskProgress(Running)` → `CrawlerEngine` 执行并按页上报进度 → `CrawlResult` 回传 → Master 落库、状态 Completed。

### 2.3 状态同步
心跳 → Master 更新 `LastHeartbeat` 与资源数据 → 发布 `NodeStatusChangedEventArgs` → `RealtimeDataSyncService` 节流后经 SignalR 推送 → ViewModel 用 Dispatcher 更新 `ObservableCollection`。

## 3. gRPC 协议（`CrawlerCore/Protos/crawler.proto`）

服务 `CrawlerCoordinator`：
- `RegisterNode(NodeRegistration) returns (RegistrationResponse)`
- `StartStreaming(stream FromWorker) returns (stream FromMaster)`

| 消息 | 关键字段 | 方向/用途 |
|---|---|---|
| `NodeRegistration` | node_id, machine_name, cpu_cores, total_memory_gb, ip_address | W→M 注册 |
| `RegistrationResponse` | success, session_id, message, system_config | M→W |
| `SystemConfig` | heartbeat_interval_seconds, max_retry_attempts, retry_delay_milliseconds | 系统参数 |
| `Heartbeat` | node_id, cpu_usage, memory_usage_mb, disk_usage_percent, active_tasks, timestamp | W→M |
| `CrawlerTask` | task_id, task_name, start_urls, plugin_name, configuration(map), max_depth, max_pages, status, priority | M→W |
| `PageResult` | url, finalurl, status_code, parsed_data(map), links | 结果子项 |
| `CrawlResult` | task_id, page_results, completed_at | W→M |
| `TaskProgress` | task_id, task_name, crawled_pages, total_pages, current_status, assigned_node_id, error_message | W→M |
| `LogEntry` | node_id, task_id, log_level, message, exception_detail, timestamp | W→M |
| `TaskControl` | task_id, command(Start/Pause/Stop/Cancel), reason | M→W |
| `FromWorker` | oneof heartbeat / log_entry / crawl_result / task_progress | 封装 |
| `FromMaster` | oneof new_task / task_control / system_config | 封装 |

开发计划规定 proto 定稿后**不得私自修改字段**，变更需评审。Worker 另有 `CrawlerWorker/Protos/worker_control.proto`（`GetWorkerStatus`、`ExecuteTaskCommand`，目前抛 `NotImplementedException`）。

## 4. 双向流可靠性（论文 4.1.3.2、5.1.2；图 [4-9](../thesis/figures/fig4-9.png) [4-10](../thesis/figures/fig4-10.png) [5-2](../thesis/figures/fig5-2.png)）

- Worker `StartStreamingAsync` 启动三个循环：**读循环**（`ResponseStream.ReadAllAsync` → switch 处理 NewTask/TaskControl/SystemConfig）、**写循环**（从 `Channel<FromWorker>` 读，串行 `WriteAsync`）、**心跳循环**。
- 所有发送先写 `Channel<T>`（`SingleReader=true`，MPSC），由单一写循环写流 → 保证有序、避免并发写异常。Master 端同样用 `Channel<FromMaster>` 解耦。
- 重连状态机：Disconnected →（计数+1，固定延迟 `RetryDelayMilliseconds`）Reconnecting → Connected；超过 `MaxRetryAttempts` → 清空会话、Registering（重新注册）→ Connected。
- 生命周期由 `CancellationTokenSource` 联动；`CleanupStreamResourcesAsync` 等待发送队列清空（超时 `GrpcConstants.DefaultStreamCloseTimeoutMs`）后关闭。

## 5. Master 服务

| 服务 | 文件 | 要点 |
|---|---|---|
| 节点管理 | `Services/NodeManagementService.cs`（`INodeManagementService`） | 注册、心跳更新、标记离线；节点状态 Pending→Online→Offline（图 [4-2](../thesis/figures/fig4-2.png)） |
| 健康检查 | `Services/NodeHealthCheckService.cs` | 每 15s 检查，1 分钟无心跳→Offline，独立 DI 作用域 |
| 会话 | `Services/SessionManager.cs` | `ConcurrentDictionary` 存 NodeId↔SessionId↔流 |
| 调度 | `Services/TaskSchedulerHostedService.cs`（`ITaskSchedulerService`） | `PriorityQueue`；启动时 `RecoverPendingTasksFromDatabaseAsync` 按创建时间恢复 Pending 任务；任务状态 Pending→Running→Completed/Failed（图 [4-4](../thesis/figures/fig4-4.png)） |
| 负载均衡 | `Services/LoadBalancingScheduler.cs` | 过滤：Online 且 CPU<50% 且内存使用率<60%；无可用节点返回 null，任务留队列；评分见 overview §5 |
| 故障恢复 | `Services/FailedTaskRecoveryService.cs` | 订阅 Offline 事件；`_processingNodes` 同节点处理锁 + 30s 内重复离线忽略；`BatchResetFailedTasksAsync` 事务内 Running→Pending 并清空 AssignedNode；按原优先级重新入队；每 5 分钟清理过期记录（时序图 [4-6](../thesis/figures/fig4-6.png)） |
| 实时推送 | `Services/RealtimeDataSyncService.cs` + `Hubs/CrawlerHub.cs` | `Channel<Func<IHubContext,Task>>` 缓冲；`ConcurrentDictionary<string,DateTime>` 节流：节点 5s、任务进度 2s（`SignalRSettings`） |
| 数据访问 | `Services/TaskDataService.cs`（`ITaskDataService`） | 大量 `AsNoTracking()`；`GetPagedCrawlResultProjectionsAsync` 用 Join/GroupJoin 返回 `CrawlResultProjection`（含 TaskName、页数），支持 TaskId/TaskName 模糊搜索 |

### 数据库（`Data/CrawlerDbContext.cs`，SQLite `crawler.db`）
表 `NodeInfos`、`CrawlerTasks`、`CrawlResults`、`PageResults`。`CrawlerTasks.AssignedNode`、`Status` 建索引；`Url/FinalUrl` 最大 2048；JSON 集合列自定义 `ValueComparer`；`CrawlResult`→`PageResult` 级联删除。启动时若缺表则 `EnsureDeleted + EnsureCreated` 重建（不用迁移）。

## 6. Worker 组件

### 爬虫引擎（`Services/Engine/CrawlerEngine.cs`，接口 `ICrawlerEngine`；流程图 [4-7](../thesis/figures/fig4-7.png)）
BFS：`Queue<(Url, Depth)>` + `HashSet` 去重（URL 规范化；重定向后的 FinalUrl 也加入集合；集合上限 `MaxUrlHashSetCapacity` 告警停止）。每页：HTTP GET → `plugin.ExecuteAsync(PluginContext)` → 链接提取 → `plugin.ShouldFollowLink(url, depth)` 过滤 → 入队。爬取间隔优先取插件配置 `CrawlDelay`，否则 `CrawlerSettings.DelayBetweenRequestsMs`。`MaxPages<=0` 表示不限。进度通过 `OnProgressUpdated` 事件 → `WorkerHostedService` → Channel → 流。并发任务数由 `SemaphoreSlim`(`MaxConcurrentTasks`) 控制。

### 插件（`ICrawlerPlugin`，图 [4-8](../thesis/figures/fig4-8.png)）
`Name`（全局唯一，等于 `CrawlerTask.PluginName`）、`Version`、`ExecuteAsync(PluginContext)→PluginResult`、`GetDefaultConfiguration()`、`ShouldFollowLink(url, depth)`（默认实现 true）。`PluginManager` 启动时扫描 `AppContext.BaseDirectory/Plugins/*.dll`，反射实例化，缓存在大小写不敏感的 `ConcurrentDictionary`。

`ArXivThesisPlugin` 输出的 `parsedData` 键：`ArXivId, Title, Authors, AuthorsList, Abstract, Subjects, SubjectsList, PublicationDate, DOI, PdfUrl, SourceUrl, NormalizedUrl, Comment`——这是数据分析模块的输入契约，改键名会破坏下游。

### 资源监控（`Services/NodeInfoCollector.cs`，`INodeInfoCollector`）

| 指标 | 采集方式 |
|---|---|
| NodeId | 环境变量 `MASTER_NODE_ID`，否则 `Environment.MachineName`；字母数字 `_-` 以外的字符替换为 `_`，截断 128 字符 |
| CPU 核数 | cgroup v2 `/sys/fs/cgroup/cpu.max` → `ceil(quota/period)`；`max` 时回退 `Environment.ProcessorCount` |
| 总内存 | cgroup v2 `memory.max`；Linux `/proc/meminfo` MemTotal；Windows `GlobalMemoryStatusEx` |
| IP | ① UDP 连 Master 端点取出口 IP ② DNS 取首个非环回 IPv4 ③ `127.0.0.1` |
| CPU 使用率 | 两次采样差分：Linux `/proc/stat`（idle+iowait），Windows `NtQuerySystemInformation`；首次返回 0 |
| 内存使用量 | 容器 `memory.current`；Linux `MemTotal−MemAvailable`；Windows `ullTotalPhys−ullAvailPhys` |
| 磁盘使用率 | `DriveInfo`(应用根目录所在盘) `(Total−AvailableFree)/Total` |
| 活跃任务数 | 由业务逻辑赋值 |

### 会话（`Services/WorkerSessionManager.cs`）
保存 SessionId、SystemConfig、IsRegistered、ActiveTaskCount；Master 返回会话无效时自动重新注册。

## 7. WPF UI（阶段 3.2–3.4 约束）

- 主窗口：左侧导航（节点监控 / 任务管理 / 数据查看）+ `ContentControl` + DataTemplate 切换视图；code-behind 除构造函数外不写业务。
- 节点监控：DataGrid 必须开启行/列虚拟化 + Recycling；Online 绿 / Offline 红，CPU>80%、内存>90% 标黄；选中节点显示 OxyPlot CPU/内存曲线（环形缓冲 60 点，Y 轴固定 0–100）；双击打开 `NodeDetailWindow`；计划要求心跳更新 500ms 防抖（代码中为 `_updateLock.WaitAsync(500)`）。
- 任务管理：`CreateTaskDialog` 表单校验（必填、URL 格式）；按状态筛选。
- 数据查看：分页（每页 20）+ 关键词搜索（TaskId/TaskName）；`PageDataDetailWindow` 查看完整 ParsedData；导出 JSON（camelCase、`UnsafeRelaxedJsonEscaping`、文件名 `任务数据_{TaskName}_{TaskId前8位}_{yyyyMMddHHmmss}.json`）。
- ViewModel 订阅事件必须保存订阅令牌并在 Dispose 中注销；禁止 `async void` 事件处理。

## 8. 原设计中未落地的部分（项目文档 3.4 / 4.3）

- **可视化流程设计器**：`WorkflowDesigner : Canvas`（网格背景、拖拽、贝塞尔连线）、`DesignElement` 子类（`StartUrlElement`、`ParseListElement`(CssSelector)、ExtractData、Export）、`WorkflowCompiler`（校验必须有起始节点、无环 → 拓扑排序 → 生成 `PluginName="WorkflowExecutor"` 的任务）、XML 保存/加载。
- `MemoryManager`：进程工作集 >1GB 时清缓存 + GC。
- 插件 `ValidateConfig`、Master 插件管理 UI、AppDomain/AssemblyLoadContext 隔离、NewsPlugin 示例。
- Master Dockerfile（Master 是 WPF，原约束为“仅 Windows exe 发布”，与阶段五计划冲突）。
