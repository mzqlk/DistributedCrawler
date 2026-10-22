# 分布式爬虫管理模块设计要点

> 来源：`项目文档.docx`、毕业论文第 3–5 章、开发计划中的设计约束。代码片段不再复制（仓库即真源），这里只记录**设计意图、流程和约定**，并标出对应代码文件。图见 `docs/thesis/figures/`。

## 1. 架构

Master-Worker 主从架构（图 [3-1](../thesis/figures/fig3-1.png)、数据流图 [3-2](../thesis/figures/fig3-2.png)）。

- **Master（`CrawlerMaster.WPF`）**：WPF + MVVM 三层（View / ViewModel / Services）。`App.xaml.cs` 用 .NET 通用主机 + `ConfigureWebHostDefaults` 让 Kestrel 同时监听 gRPC(5001, 强制 HTTP/2) 和 SignalR(5003, HTTP/1+2)，WPF 与后台服务共享一个 DI 容器；UI 线程不阻塞。后台服务的注册和启动步骤（建库、恢复 Pending 任务、启动健康检查、激活故障恢复）在 `Hosting/MasterHostingExtensions.cs`，`App.xaml.cs` 只追加界面服务；集成测试复用同一套方法。
- **Worker（`CrawlerWorker`）**：Console + `WorkerHostedService`(BackgroundService)。作为 gRPC **客户端**主动连 Master；另开 5002 的 `WorkerControl` 服务端底座（预留，未实现）。服务注册在 `Hosting/WorkerServiceCollectionExtensions.cs`（`AddCrawlerWorker`），`Program.cs` 与集成测试共用。
- **通信**：单条 gRPC 双向流承载所有消息，`oneof` 区分类型。
- **组件通信约束**：ViewModel 只通过 `IEventAggregator` 收事件，不直接碰 gRPC/SignalR 底层（阶段 3.2 的硬约束）。

### 设计原则（论文 3.1.1）
高内聚低耦合（DI、接口隔离）；插件化扩展；Master 无状态 + 持久化以便快速恢复；MVVM 便于维护；统一 UTC 时间；配置全部走 `appsettings.json`/环境变量，无硬编码。

## 2. 核心流程

### 2.1 节点注册与建流（时序图 [3-3](../thesis/figures/fig3-3.png)）
1. Worker 启动 → `NodeInfoCollector` 采集静态信息（CPU 核数、总内存、IP、机器名）。
2. Master 端口连通性预检 → `RegisterNode`（带 Token，10s Deadline，失败按配置重试）。
3. Master `GrpcAuthenticationInterceptor`（在 `AddGrpc` 的拦截器列表中，对两个 RPC 都生效）校验 Token：Master 未配置可用令牌（为空或为公开的 `test-token`）时拒绝所有节点；令牌错误返回 `Unauthenticated`。业务代码抛出的 `RpcException` 原样透传，其他异常只返回固定文本 `Internal server error`。
   → 该 NodeId **仍有活跃流**时拒绝（防止两个 NodeId 相同的 Worker 互相踢下线），否则创建或替换会话（旧会话随即失效，崩溃重启的 Worker 可以立即重新注册）→ 记下注册时声明的 `max_concurrent_tasks` → 持久化节点 → 返回 `RegistrationResponse{session_id, system_config}`。
4. Worker 携带 `node-id` + `session-id` 头调用 `StartStreaming`；Master 校验会话后登记流上下文。同一会话已有旧流时，以新流为准并关闭旧流（旧流多半是半开连接）。流断开时**不删除会话**，Worker 短暂断线后可以用旧会话重连；会话超过 30 分钟不活跃且没有活跃流时才由 `SessionManager`（托管服务）清理。
5. Worker 按 `HeartbeatIntervalSeconds`(5s) 上报心跳。

### 2.2 任务执行
UI 创建任务（名称、插件、起始 URL、MaxDepth、MaxPages、优先级 1–5）→ `TaskSchedulerHostedService` 入 `PriorityQueue` → 出队后确认任务仍为 Pending → 候选节点为库中 Online 且**当前有活跃流**的节点 → `LoadBalancingScheduler` 选节点 → **先把任务写为“Running + 节点”并通知界面，再**通过双向流发 `FromMaster.NewTask`（下发失败则撤销，换下一个节点）→ Worker 回 `TaskProgress(Running)` → `CrawlerEngine` 执行并按页上报进度 → Worker 发 Completed 进度（库中仍为 Running）→ `CrawlResult` 回传 → Master 在**同一事务**中写入结果并把任务改为 Completed → 回 `FromMaster.ResultAck`。

### 2.2.1 任务状态机（`Services/TaskDataService.cs`，T4）

任务状态的所有写入都集中在 `TaskDataService`，用带条件的 `ExecuteUpdate` 实现，条件就是“允许的来源状态”，并发下也是原子的：

| 转换 | 触发 | 条件 |
|---|---|---|
| Pending → Running + 节点 | 调度器下发前（`TryMarkTaskDispatchedAsync`） | 仍为 Pending |
| Running + 节点 → Pending | 下发失败撤销（`RevertTaskDispatchAsync`） | 仍为 Running 且是该节点 |
| Pending（未分配）→ Running + 节点 | 被重置的任务，原节点重连后继续上报进度（**认领**），调度器出队时发现已不是 Pending 就不再下发 | 未分配或分配给该节点 |
| Running → Failed | 当前执行节点上报 Failed | Running 且是该节点 |
| Pending/Running/Failed → Completed | 结果落库（`SaveCrawlResultAsync`，与写入结果同一事务） | 该任务还没有结果，且未分配给其他节点 |
| Pending/Running → Failed | 结果落库失败（`MarkTaskFailedAsync`） | 未分配给其他节点 |
| Pending/Running + 节点 → Pending（未分配） | 节点离线的故障恢复（`ResetUnfinishedTasksOfNodeAsync`）、Master 重启恢复（`ResetTasksForStartupRecoveryAsync`） | 见 §5 |

其余情况一律忽略：终态不回退；Worker 本地排队时上报的 Pending 不覆盖 Running；任务已分配给其他节点时，该节点（被隔离的旧节点）的进度与结果都丢弃；未知任务的进度与结果丢弃。只有生效的变化才发布 `TaskProgressUpdatedEventArgs`。

结果去重：`CrawlResults.TaskId` 有唯一索引，写入前也检查；重复结果丢弃。结果回执 `ResultAck.accepted`：已落库（包括此前已落库的重复结果）为 true，被丢弃为 false，落库失败不回执（Worker 侧的发件箱与重发在 T5 实现）。

### 2.3 状态同步
心跳 → Master 更新 `LastHeartbeat` 与资源数据 → 发布 `NodeStatusChangedEventArgs` → `RealtimeDataSyncService` 节流后经 SignalR 推送 → ViewModel 用 Dispatcher 更新 `ObservableCollection`。

## 3. gRPC 协议（`CrawlerCore/Protos/crawler.proto`）

服务 `CrawlerCoordinator`：
- `RegisterNode(NodeRegistration) returns (RegistrationResponse)`
- `StartStreaming(stream FromWorker) returns (stream FromMaster)`

| 消息 | 关键字段 | 方向/用途 |
|---|---|---|
| `NodeRegistration` | node_id, machine_name, cpu_cores, total_memory_gb, ip_address, max_concurrent_tasks（6，T4 追加；0 表示不限） | W→M 注册 |
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
| `ResultAck` | task_id, accepted（T4 新增） | M→W 结果回执：结果已落库（true）或被丢弃（false），Worker 收到后不必重发 |
| `FromMaster` | oneof new_task / task_control / system_config / result_ack（4，T4 追加） | 封装 |

开发计划规定 proto 定稿后**不得私自修改字段**，变更需评审。路线图规定只做向后兼容的扩展（在消息或 oneof 末尾追加），并同步更新 `ProtoContractTests`。Worker 另有 `CrawlerWorker/Protos/worker_control.proto`（`GetWorkerStatus`、`ExecuteTaskCommand`，目前抛 `NotImplementedException`）。

## 4. 双向流可靠性（论文 4.1.3.2、5.1.2；图 [4-9](../thesis/figures/fig4-9.png) [4-10](../thesis/figures/fig4-10.png) [5-2](../thesis/figures/fig5-2.png)）

- Worker `StartStreamingAsync` 启动三个循环：**读循环**（`ResponseStream.ReadAllAsync` → switch 处理 NewTask/TaskControl/SystemConfig）、**写循环**（从 `Channel<FromWorker>` 读，串行 `WriteAsync`）、**心跳循环**。
- 所有发送先写 `Channel<T>`（`SingleReader=true`，MPSC），由单一写循环写流 → 保证有序、避免并发写异常。Master 端同样用 `Channel<FromMaster>` 解耦。
- 重连状态机：Disconnected →（计数+1，固定延迟 `RetryDelayMilliseconds`）Reconnecting → Connected；超过 `MaxRetryAttempts` → 清空会话、Registering（重新注册）→ Connected。
- 生命周期由 `CancellationTokenSource` 联动；`CleanupStreamResourcesAsync` 等待发送队列清空（超时 `GrpcConstants.DefaultStreamCloseTimeoutMs`）后关闭。

Master 侧（`CrawlerCoordinatorService.StartStreaming`，T4）：

- 每条流一个链接到调用取消令牌的 CTS。读循环、写循环任一结束（客户端断开、写入失败、读到超限消息、被健康检查断开），立即取消另一个并返回，不会挂起。
- 流上下文按 NodeId 登记。同一会话的新流替换旧流；清理只执行一次，并且只在字典里登记的仍是这条流时才移除它、把节点标为离线，被替换的旧流不影响新流。
- 发现半开连接：Kestrel 开启 HTTP/2 keepalive（`GrpcSettings:KeepAlivePingDelaySeconds`/`KeepAlivePingTimeoutSeconds`，默认 15/30 秒）；健康检查判离线后调用 `DisconnectNode` 主动关闭该节点的流，节点恢复后可以重新建流。
- 节点状态事件只由 `NodeManagementService` 在库中状态变化、保存之后发布，协调服务不再自己发布。

## 5. Master 服务

| 服务 | 文件 | 要点 |
|---|---|---|
| 节点管理 | `Services/NodeManagementService.cs`（`INodeManagementService`） | 注册、心跳更新、标记离线；节点状态 Pending→Online→Offline（图 [4-2](../thesis/figures/fig4-2.png)） |
| 健康检查 | `Services/NodeHealthCheckService.cs` | 默认每 15s 检查，1 分钟无心跳→Offline（`HealthCheckSettings:CheckIntervalSeconds` / `NodeTimeoutSeconds`），每次检查独立 DI 作用域，检查不重叠执行；判离线的节点随即被断开双向流 |
| 会话 | `Services/SessionManager.cs` | `ConcurrentDictionary` 存 NodeId↔SessionId 与注册时声明的并发上限；同时是托管服务，定期清理过期且没有活跃流的会话。流上下文由协调服务管理 |
| 调度 | `Services/TaskSchedulerHostedService.cs`（`ITaskSchedulerService`） | `PriorityQueue`；每次分配的结论为已分配/无可用节点/下发失败/不可调度，只有中间两种重新入队，分配中出异常也重新入队；状态机见 §2.2.1；启动时 `RecoverPendingTasksFromDatabaseAsync` 在库中重置并恢复所有 Pending 与执行节点不在线的 Running 任务（图 [4-4](../thesis/figures/fig4-4.png)） |
| 负载均衡 | `Services/LoadBalancingScheduler.cs` | 只考虑在线且有活跃流的节点；任务数已达节点并发上限的不分配；资源阈值 `LoadBalancingSettings`（默认 CPU、内存都 <90%）满足的节点优先，都不满足时退而选择负载最低的；先按已分配且未结束的任务数从少到多，再按得分（见 overview §5）；没有可用节点时返回 null，任务留在队列 |
| 故障恢复 | `Services/FailedTaskRecoveryService.cs` | 订阅 Offline 事件；同一节点的事件用一把 `SemaphoreSlim` 依次处理（等待而不是跳过）；`ResetUnfinishedTasksOfNodeAsync` 在事务内把分配给该节点的 Pending 与 Running 重置为“Pending + 未分配”（幂等）；通知界面并按原优先级重新入队（时序图 [4-6](../thesis/figures/fig4-6.png)） |
| 实时推送 | `Services/RealtimeDataSyncService.cs` + `Hubs/CrawlerHub.cs` | `Channel<Func<IHubContext,Task>>` 缓冲；`ConcurrentDictionary<string,DateTime>` 节流：节点 5s、任务进度 2s（`SignalRSettings`） |
| 数据访问 | `Services/TaskDataService.cs`（`ITaskDataService`） | 大量 `AsNoTracking()`；`GetPagedCrawlResultProjectionsAsync` 用 Join/GroupJoin 返回 `CrawlResultProjection`（含 TaskName、页数），支持 TaskId/TaskName 模糊搜索 |

### 数据库（`Data/CrawlerDbContext.cs`，SQLite `crawler.db`）
表 `NodeInfos`、`CrawlerTasks`、`CrawlResults`、`PageResults`。`CrawlerTasks.AssignedNode`、`Status` 建索引；`CrawlResults.TaskId` 唯一索引（每个任务只有一份结果，T4）；`Url/FinalUrl` 最大 2048；JSON 集合列自定义 `ValueComparer`；`CrawlResult`→`PageResult` 级联删除。数据库文件的相对路径相对于程序所在目录。启动时按实体模型逐表逐列检查（不检查索引）：结构一致直接使用；结构不符或无法读取时，库中有数据先备份为 `crawler.db.bak-yyyyMMddTHHmmssZ` 再 `EnsureDeleted + EnsureCreated` 重建，并在界面提示备份位置（不用迁移）。

## 6. Worker 组件

### 爬虫引擎（`Services/Engine/CrawlerEngine.cs`，接口 `ICrawlerEngine`；流程图 [4-7](../thesis/figures/fig4-7.png)）
BFS：`Queue<(Url, Depth)>` + `HashSet` 去重（URL 规范化；重定向后的 FinalUrl 也加入集合；集合上限 `MaxUrlHashSetCapacity` 告警停止）。每页：HTTP GET → `plugin.ExecuteAsync(PluginContext)` → 链接提取 → `plugin.ShouldFollowLink(url, depth)` 过滤 → 入队。爬取间隔优先取插件配置 `CrawlDelay`，否则 `CrawlerSettings.DelayBetweenRequestsMs`。`MaxPages<=0` 表示不限。进度通过 `OnProgressUpdated` 事件 → `WorkerHostedService` → Channel → 流；非终态进度按 `SignalRSettings:TaskProgressThrottleSeconds`（默认 2s，Worker 端读取）节流，终态强制上报。并发任务数由 `SemaphoreSlim`(`MaxConcurrentTasks`) 控制。

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

## 9. 端到端集成测试（`CrawlerCore.Tests/Integration/`）

目的：不启动 WPF 界面，自动验证 Master–Worker 全链路（注册、心跳、调度、爬取、落库、故障恢复、重启恢复）。运行方式见 CLAUDE.md。

| 组件 | 文件 | 要点 |
|---|---|---|
| 进程内 Master | `Infrastructure/TestMaster.cs` | 与 `App.OnStartup` 相同的步骤和注册（`MasterHostingExtensions`），不注册界面服务；Kestrel 只监听 127.0.0.1 的随机端口，只开 gRPC；配置全部来自内存（不读输出目录的 `appsettings.json`）；临时 SQLite 文件；开启 DI 的 `ValidateScopes`/`ValidateOnBuild`，校验单例不直接持有 Scoped 服务 |
| 进程内 Worker | `Infrastructure/TestWorker.cs` | `AddCrawlerWorker` 注册的真实 Worker；插件由 `PluginManager` 从测试输出目录 `Plugins/` 反射加载（测试工程构建后把 `MockPlugin.dll` 复制进去） |
| 节点信息采集 | `Infrastructure/TestNodeInfoCollector.cs` | 唯一被替换的组件：委托真实采集器，但改写 NodeId（同一进程内两个 Worker 的 NodeId 会相同），并上报固定的低负载（负载均衡会过滤 CPU≥50%、内存≥60% 的节点） |
| 故障注入 | `Infrastructure/TcpFaultProxy.cs` | 每个 Worker 经一个 TCP 代理连接 Master。`Sever` 用 RST 断开连接，模拟进程崩溃，Master 的流立即结束；`BlackHole` 保持连接但丢弃数据，模拟失联，只能靠健康检查发现；`Restore` 可切换上游端口，用于 Master 重启 |
| 测试网站 | `Infrastructure/TestWebSite.cs` + `TestSite/*.html` | Kestrel 静态站点，记录请求路径；`HoldRequests` 扣住请求，让任务停在 Running，以便在执行中注入故障 |
| 等待 | `Infrastructure/Poll.cs` | 轮询加超时（默认 20s），不使用固定 sleep |
| 假节点 | `Infrastructure/RawNodeClient.cs` | 直接调用 Master gRPC 接口：由测试决定注册参数（如并发上限）、何时发心跳/进度/结果，并记录 Master 下发的全部消息（任务、结果回执）。用于只与 Master 有关、需要精确控制消息顺序的场景（T4） |

约定：
- 测试中缩短的参数：心跳 1s；健康检查每 1s，5s 无心跳判离线；请求间隔 20ms；进度不节流。生产默认值不变。
- “进度不回退”的判定依据：Master 发布给界面的 `TaskProgressUpdatedEventArgs`（状态 Pending→Running→Completed、已爬页数都单调不减）。还要等任务完成后的下一次心跳（同一条流按序处理，心跳之前的任务消息必然已处理完），再确认库中状态仍为 Completed。
- 集成测试集合禁止并行（共享全局 Serilog 与插件目录，并且对时间敏感）。日志写到测试输出，`Component` 属性标出来自 Master 还是哪个 Worker。
- 测试发现的生产缺陷写成“修复后应有的行为”的测试，先用 Skip 跳过（`MasterWorkerKnownDefectTests`），修复时去掉 Skip。T4 的回归测试在 `MasterReliabilityTests`。
- Kestrel 的 HTTP/2 keepalive 与生产共用 `MasterHostingExtensions.ConfigureMasterHttp2KeepAlive`。
