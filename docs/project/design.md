# 分布式爬虫管理模块设计要点

> 来源：`项目文档.docx`、毕业论文第 3–5 章、开发计划中的设计约束。代码片段不再复制（仓库即真源），这里只记录**设计意图、流程和约定**，并标出对应代码文件。图见 `docs/thesis/figures/`。

## 1. 架构

Master-Worker 主从架构（图 [3-1](../thesis/figures/fig3-1.png)、数据流图 [3-2](../thesis/figures/fig3-2.png)）。

- **Master（`CrawlerMaster.WPF`）**：WPF + MVVM 三层（View / ViewModel / Services）。`App.xaml.cs` 用 .NET 通用主机 + `ConfigureWebHostDefaults` 让 Kestrel 同时监听 gRPC(5001, 强制 HTTP/2) 和 SignalR(5003, HTTP/1+2)，WPF 与后台服务共享一个 DI 容器；UI 线程不阻塞。后台服务的注册和启动步骤（建库、恢复 Pending 任务、启动健康检查、激活故障恢复）在 `Hosting/MasterHostingExtensions.cs`，`App.xaml.cs` 只追加界面服务；集成测试复用同一套方法。
- **Worker（`CrawlerWorker`）**：Console + `WorkerHostedService`(BackgroundService)。作为 gRPC **客户端**主动连 Master；`WorkerControl` 服务端底座（5002）只是预留代码，没有监听。服务注册在 `Hosting/WorkerServiceCollectionExtensions.cs`（`AddCrawlerWorker`），主机构建与启动前校验在 `Hosting/WorkerConfiguration.cs`，`Program.cs` 与测试共用。
- **通信**：单条 gRPC 双向流承载所有消息，`oneof` 区分类型。
- **组件通信约束**：ViewModel 只通过 `IEventAggregator` 收事件，不直接碰 gRPC/SignalR 底层（阶段 3.2 的硬约束）。

### 设计原则（论文 3.1.1）
高内聚低耦合（DI、接口隔离）；插件化扩展；Master 无状态 + 持久化以便快速恢复；MVVM 便于维护；统一 UTC 时间；配置全部走 `appsettings.json`/环境变量，无硬编码。

## 2. 核心流程

### 2.1 节点注册与建流（时序图 [3-3](../thesis/figures/fig3-3.png)）
1. Worker 启动 → 启动前校验配置（令牌为空或为 `test-token`、Master 地址无效时以退出码 1 退出）→ `NodeInfoCollector` 采集静态信息（CPU 核数、总内存、IP、机器名、并发上限）。
2. `RegisterNode`（带 Token，Deadline 为 `MasterResponseTimeoutSeconds`）。Master 不可达、注册被拒绝都按退避一直重试（T5 起不再做 TCP 预检，也不会因此退出）；只有令牌错误（`Unauthenticated`）、参数非法时退出，退出码 1。
3. Master `GrpcAuthenticationInterceptor`（在 `AddGrpc` 的拦截器列表中，对两个 RPC 都生效）校验 Token：Master 未配置可用令牌（为空或为公开的 `test-token`）时拒绝所有节点；令牌错误返回 `Unauthenticated`。业务代码抛出的 `RpcException` 原样透传，其他异常只返回固定文本 `Internal server error`。
   → 该 NodeId **仍有活跃流**时拒绝（防止两个 NodeId 相同的 Worker 互相踢下线），否则创建或替换会话（旧会话随即失效，崩溃重启的 Worker 可以立即重新注册）→ 记下注册时声明的 `max_concurrent_tasks` → 持久化节点 → 返回 `RegistrationResponse{session_id, system_config}`。
4. Worker 携带 `node-id` + `session-id` 头调用 `StartStreaming`；Master 校验会话后登记流上下文，**随即发送响应头**，Worker 收到响应头（超时 `MasterResponseTimeoutSeconds`）才认为流已建立（T5）。同一会话已有旧流时，以新流为准并关闭旧流（旧流多半是半开连接）。流断开时**不删除会话**，Worker 短暂断线后可以用旧会话重连；会话超过 30 分钟不活跃且没有活跃流时才由 `SessionManager`（托管服务）清理。会话无效时返回 `PermissionDenied`，Worker 据此立即重新注册（例如 Master 重启后）。
5. Worker 按 `HeartbeatIntervalSeconds`(5s) 上报心跳。

### 2.2 任务执行
UI 创建任务（名称、插件、起始 URL（默认 `https://arxiv.org/list/cs.LG/recent`）、MaxDepth、MaxPages、优先级 1–5）→ `TaskSchedulerHostedService` 入 `PriorityQueue` → 出队后确认任务仍为 Pending → 候选节点为库中 Online 且**当前有活跃流**的节点 → `LoadBalancingScheduler` 选节点 → **先把任务写为“Running + 节点”并通知界面，再**通过双向流发 `FromMaster.NewTask`（下发失败则撤销，换下一个节点）→ Worker 按 TaskId 去重后放入本地队列（不上报 Pending）→ 开始执行时回 `TaskProgress(Running)` → `CrawlerEngine` 执行并按页上报进度 → Worker 先发 `CrawlResult`、再发 Completed 进度（T5 起的顺序）→ Master 在**同一事务**中写入结果并把任务改为 Completed → 回 `FromMaster.ResultAck` → Worker 删除待确认的结果（没收到回执时重发，见 §4）。

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

结果去重：`CrawlResults.TaskId` 有唯一索引，写入前也检查；重复结果丢弃。结果回执 `ResultAck.accepted`：已落库（包括此前已落库的重复结果）为 true，被丢弃为 false，落库失败不回执。Worker 收到回执（true 或 false）后不再重发；没有收到时在重连后或超过回执超时后重发（T5，见 §4）。

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

Worker 侧（`Services/WorkerHostedService.cs`、`Communication/WorkerOutbox.cs`、`Communication/ConnectionPolicy.cs`，T5 重写）：

- **连接循环** `RunConnectionLoopAsync`：确保已注册 → 建流 → 等 Master 的响应头确认 → 启动**读循环**（按到达顺序处理 NewTask/ResultAck/TaskControl/SystemConfig，单条出错不影响流）、**写循环**、**心跳循环** → 任一结束即关闭整条流，等三个循环都结束并取得断开原因。每条流的 CTS、定时器、读写任务都是本轮的局部变量，不存在新旧流共用字段。
- **流结束后的处理**（`ConnectionPolicy`）：`PermissionDenied`/`NotFound`（会话失效）→ 清除会话、立即重新注册，不计入失败次数；`Unauthenticated` → Fatal，退出码 1；其余 → 失败次数加 1，指数退避（`RetryDelayMilliseconds` 起翻倍，上限 `MaxRetryDelaySeconds`，±20% 抖动），连续失败超过 `MaxRetryAttempts` 次后清除会话重新注册。失败次数只在流被 Master 确认后清零。
- **发件箱** `WorkerOutbox`（进程级，跨越重连）：所有消息都经过它，由当前流的单一写循环串行写出；写入失败的那条由下一条流先发。心跳与运行中的进度是即时消息，只在有流时入队。任务的最终结果按 TaskId 保存：完成时是 `[CrawlResult, Completed 进度]`，保留到收到 `ResultAck`；新流被确认后重发已写出未确认的结果，写出后超过 `ResultAckTimeoutSeconds`（之后翻倍，上限 10 分钟）也重发；失败时是 `[Failed 进度]`，写出即完成（Master 不回执）。重复发送由 Master 按 TaskId 去重。
- **任务**：`_acceptedTasks` 按 TaskId 去重（执行中的任务再次下发时补发当前进度，已完成、结果待确认的补发结果）；本地队列是 `Channel<CrawlerTask>`，消费循环按 `MaxConcurrentTasks` 的信号量启动任务，每个任务一个链接到停止令牌的 CTS（供 T9 使用）。
- **半开连接**：gRPC 客户端开启 HTTP/2 keepalive（`KeepAlivePingDelaySeconds`/`KeepAlivePingTimeoutSeconds`，与 Master 共用配置项），Master 消失但没有 RST 时，Worker 在一个 keepalive 周期内发现并重连。
- **停止**（`StopAsync`）：不再接收任务、不再重连 → 取消执行中的任务（**不上报任何终态**，任务在 Master 上保持 Running）→ 等任务结束 → 等已完成任务的回执（最多 3 秒）→ 写完发件箱、`RequestStream.CompleteAsync()`、等 Master 关闭流 → 停止后台循环 → 关闭通道。Master 随即判节点离线、重置任务，由其他节点重做。每一步限时并捕获异常。

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
BFS：`Queue<(Url, Depth)>` + `HashSet` 去重（URL 规范化：去掉片段与末尾斜杠、整体小写；集合达到上限 `MaxUrlHashSetCapacity` 后只停止入队）。`MaxPages<=0` 表示不限。每页（T5）：

1. **robots.txt**（`Robots/RobotsPolicy.cs`、`Robots/RobotsTxt.cs`，RFC 9309）：每个站点首次访问时获取并缓存 `RobotsCacheHours`；按 User-Agent 的产品名 `DistributedCrawler` 或 `*` 选规则组，最长匹配优先、支持 `*`/`$`；4xx 视为没有限制，5xx/429/网络错误视为暂时不可用（1 分钟内不请求该站点，页面记状态码 0）。被禁止的 URL 不请求、不计入 MaxPages。
2. **按主机限速**（`HostRateLimiter`，进程内单例）：所有任务对同一主机的请求（含 robots.txt、每一跳重定向、重试）串行发放许可，相邻间隔取 max(`DelayBetweenRequestsMs`, 插件 `CrawlDelay`, 任务配置 `CrawlDelay`, robots `Crawl-delay`)。跨节点的总速率不在这里控制（T6）。
3. **请求**：手动跟随重定向（最多 10 次），每一跳先做循环检测与作用域检查（起始页只要求同主机，跟随而来的页面还要经过 `ShouldFollowLink`），再全局去重（已存在就放弃本页）。每个请求一个超时令牌（`RequestTimeoutSeconds`），覆盖读取正文；408、429、5xx、超时、网络错误按退避重试 `MaxRequestRetries` 次，429/503 的 `Retry-After` 推迟该主机的所有请求；没有 HTTP 响应的页面状态码记 0。只读取 HTML，正文超过 `MaxResponseBytes` 不解析。
4. **解析**：`plugin.ExecuteAsync(PluginContext)` 有超时（`PluginTimeoutSeconds`），失败、超时或异常时该页没有解析数据、保留 HTTP 状态码。
5. **链接**：AngleSharp 提取 → `SameHostOnly`（默认只跟随与起始 URL 同主机的链接）→ `plugin.ShouldFollowLink(url, depth)` → 入队。

请求被取消（Worker 停止）时直接抛出 `OperationCanceledException`，不记成失败页面。进度通过 `ProgressUpdated` 事件交给 `WorkerHostedService`：运行中的进度按 `SignalRSettings:TaskProgressThrottleSeconds`（默认 2s，Worker 端读取）节流，每个任务的第一条总会上报；总页数为 min(已爬 + 队列, MaxPages)；最终状态由 `WorkerHostedService` 经发件箱可靠上报。爬虫请求默认校验证书（`IgnoreCertificateErrors` 默认 false），User-Agent 为 `CrawlerSettings:DefaultUserAgent`（`DistributedCrawler/1.0 (+项目地址)`）。

### 插件（`ICrawlerPlugin`，图 [4-8](../thesis/figures/fig4-8.png)）
`Name`（全局唯一，等于 `CrawlerTask.PluginName`）、`Version`、`ExecuteAsync(PluginContext)→PluginResult`、`GetDefaultConfiguration()`、`ShouldFollowLink(url, depth)`（默认实现 true）。

打包与加载（T5，W-04、W-18）：插件项目开启 `EnableDynamicLoading`，对 CrawlerCore 的引用 `Private=false`；Worker 构建/发布后把 `<CrawlerPlugin>` 列出的插件复制到 `Plugins/<插件名>/`（插件程序集、pdb、deps.json 与插件自己的依赖，不含 CrawlerCore）。`PluginManager` 在 Worker 启动时加载 `CrawlerSettings:PluginDirectory`（默认 `Plugins`，相对程序目录）：先加载各子目录中与目录同名的 DLL，再加载直接放置的 DLL；每个插件一个 `AssemblyLoadContext`，依赖按插件的 deps.json 解析，CrawlerCore 始终用 Worker 的版本（保证 `ICrawlerPlugin` 类型一致）；部分类型无法加载时使用其余类型；重名时保留按路径排序的第一个并记错误；重新加载时整体替换插件表。

`ArXivThesisPlugin`（v1.4.0）以页面结构为准解析：标题 `h1.title`、作者 `div.authors` 下的每个链接、摘要 `blockquote.abstract`（去掉 `span.descriptor`）、分类 `td.subjects`、备注 `td.comments`、日期 `div.dateline`；结构缺失时回退到 `citation_*` 元数据；标题取不到时解析失败。输出的 `parsedData` 键：`ArXivId, Title, Authors, AuthorsList, Abstract, Subjects, SubjectsList, PublicationDate, DOI, PdfUrl, SourceUrl, NormalizedUrl, Comment`——这是数据分析模块的输入契约，改键名会破坏下游。上报时值的转换规则在 `CrawlerCore/Common/ParsedDataConverter.cs`（字符串原样，其他标量按不变区域性、时间用 “O” 格式，列表为 JSON 数组）；契约断言在 `CrawlerCore.Tests/Plugins/ParsedDataContract.cs`。

### 资源监控（`Services/NodeInfoCollector.cs`，`INodeInfoCollector`）

| 指标 | 采集方式 |
|---|---|
| NodeId | 环境变量 `MASTER_NODE_ID`，否则 `Environment.MachineName`；字母数字 `_-` 以外的字符替换为 `_`，截断 128 字符。同一台机器上运行多个 Worker、或 compose 多副本时要分别设置 |
| 并发上限 | `CrawlerSettings:MaxConcurrentTasks`，注册时上报（`max_concurrent_tasks`），Master 不会分配超过这个数的任务 |
| CPU 核数 | cgroup v2 `/sys/fs/cgroup/cpu.max` → `ceil(quota/period)`；`max` 时回退 `Environment.ProcessorCount` |
| 总内存 | cgroup v2 `memory.max`；Linux `/proc/meminfo` MemTotal；Windows `GlobalMemoryStatusEx` |
| IP | ① UDP 连 `GrpcSettings:MasterEndpoint` 取出口 IP ② DNS 取首个非环回 IPv4 ③ `127.0.0.1` |
| CPU 使用率 | 两次采样差分，首次返回 0：Windows `GetSystemTimes`；Linux 有 cgroup v2 `cpu.stat` 时为 `usage_usec` 增量 ÷（时间 × `cpu.max` 可用核数），受容器配额限制；否则 `/proc/stat`（idle+iowait，总数不含 guest 列，整机口径，启动时警告一次） |
| 内存使用量 | 容器 `memory.current − inactive_file`（与 `docker stats` 一致）；Linux `MemTotal−MemAvailable`；Windows 上报**整机**已用物理内存 `ullTotalPhys−ullAvailPhys`（既定口径，Master 的内存阈值据此判断） |
| 磁盘使用率 | `DriveInfo`(应用根目录所在盘) `(Total−AvailableFree)/Total` |
| 活跃任务数 | 由业务逻辑赋值 |

### 会话（`Services/WorkerSessionManager.cs`）
保存 SessionId、SystemConfig、IsRegistered、ActiveTaskCount（排队中与执行中的任务数，随心跳上报）；Master 返回会话无效（`PermissionDenied`）时连接循环清除会话并立即重新注册。

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
| 进程内 Worker | `Infrastructure/TestWorker.cs` | `AddCrawlerWorker` 注册的真实 Worker；插件由 `PluginManager` 从测试输出目录 `Plugins/MockPlugin/` 加载（测试工程构建后把 `MockPlugin.dll` 复制进去）。可以指定令牌、上报的并发上限、以“断开”状态启动；停止与释放时的异常直接抛给测试（T5） |
| 节点信息采集 | `Infrastructure/TestNodeInfoCollector.cs` | 唯一被替换的组件：委托真实采集器，但改写 NodeId（同一进程内两个 Worker 的 NodeId 会相同），并上报固定的低负载（负载均衡会过滤 CPU≥50%、内存≥60% 的节点） |
| 故障注入 | `Infrastructure/TcpFaultProxy.cs` | 每个 Worker 经一个 TCP 代理连接 Master。`Sever` 用 RST 断开连接，模拟进程崩溃，Master 的流立即结束；`BlackHole` 保持连接但丢弃数据，模拟失联，Master 只能靠健康检查发现，Worker 靠 keepalive 发现；`Restore` 可切换上游端口，用于 Master 重启；`TotalConnectionCount` 统计累计连接数（判断 Worker 是否放弃了旧连接） |
| 测试网站 | `Infrastructure/TestWebSite.cs` + `TestSite/*.html` | Kestrel 静态站点，记录请求路径；`HoldRequests` 扣住请求（包括 `/robots.txt`），让任务停在 Running，以便在执行中注入故障；`RobotsTxt` 设置 `/robots.txt` 的内容，默认 404 |
| 等待 | `Infrastructure/Poll.cs` | 轮询加超时（默认 20s），不使用固定 sleep |
| 假节点 | `Infrastructure/RawNodeClient.cs` | 直接调用 Master gRPC 接口：由测试决定注册参数（如并发上限）、何时发心跳/进度/结果，并记录 Master 下发的全部消息（任务、结果回执）。用于只与 Master 有关、需要精确控制消息顺序的场景（T4） |

约定：
- 测试中缩短的参数：心跳 1s；健康检查每 1s，5s 无心跳判离线；请求间隔 20ms；进度不节流；Worker 重连退避上限 1s、等待 Master 响应 3s、keepalive 2s/2s。生产默认值不变。
- “进度不回退”的判定依据：Master 发布给界面的 `TaskProgressUpdatedEventArgs`（状态 Pending→Running→Completed、已爬页数都单调不减）。还要等任务完成后的下一次心跳（同一条流按序处理，心跳之前的任务消息必然已处理完），再确认库中状态仍为 Completed。
- 集成测试集合禁止并行（共享全局 Serilog 与插件目录，并且对时间敏感）。日志写到测试输出，`Component` 属性标出来自 Master 还是哪个 Worker。
- 测试发现的生产缺陷写成“修复后应有的行为”的测试，先用 Skip 跳过（`MasterWorkerKnownDefectTests`），修复时去掉 Skip（D1–D4 均已修复，没有 Skip）。T4 的回归测试在 `MasterReliabilityTests`，T5 的在 `WorkerReliabilityTests`（robots、失联期间完成的结果、Master 不可达时启动、令牌错误退出）。
- Kestrel 的 HTTP/2 keepalive 与生产共用 `MasterHostingExtensions.ConfigureMasterHttp2KeepAlive`。
