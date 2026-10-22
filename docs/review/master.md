# Master 端代码审阅报告（路线图 T2）

> 审阅日期：2026-10-22。代码基线：`82173ae`（与当时的 master 相同），文中行号都对应这一提交。
>
> 范围：`CrawlerMaster.WPF` 全部代码（含 XAML、配置），以及 Master 用到的 `CrawlerCore` 部分：实体、接口、事件聚合器、配置类、`LogHelper`/`LogConfiguration`、`Protos/crawler.proto`、`SystemObjectJsonConverter`。Worker 侧的问题只在影响 Master 时提到，完整审阅见 T3（`docs/review/worker.md`）。
>
> 本报告只记录问题，不改代码。T4 的默认处理范围：P0/P1 全部修复，P2 修复范围局限、风险可控的，P3 只记录。
>
> **T4 处理状态见 [§7](#7-t4-处理状态)**：每条 M 编号的状态、提交与对应测试。§4 中的行号仍对应审阅时的代码基线。

## 1. 方法与证据等级

每条问题都读过代码，并写出了具体的触发场景。“证据”分四级，写在“触发场景”一栏开头：

- **已验证**：用临时探针测试（基于 T1 的集成测试基础设施，运行后已删除，未提交）或本机实测复现过。
- **T1**：T1 集成测试已经发现（`docs/project/overview.md` §4 第 8 条）。有 Skip 测试的写出了测试名。
- **代码确认**：按代码逻辑必然发生，没有运行复现。
- **待验证**：推断，需要按“验证”一栏的方法确认。

临时探针及结果：

| 探针 | 做法 | 结果 |
|---|---|---|
| 探针 1：短暂断流 | 1 个 Worker。任务执行中用 `TcpFaultProxy.Sever()` 断开连接，等 Master 判离线后立即 `Restore()`，Worker 用旧会话重连 | 同一任务执行了两次，库中该 TaskId 有 **2 份** `CrawlResult`。进度事件为 `Pending → Running → Pending → Running → … → Completed → Completed`（M-04） |
| 探针 2：突发任务 | 2 个空闲 Worker（上报固定低负载，`MaxConcurrentTasks=2`）。扣住测试网站的请求，连续创建 4 个任务，然后让任务最多的节点崩溃 | **4 个任务全部分给 it-node-a**，it-node-b 空闲（M-06）。其中 2 个在 Worker 本地排队，库中为“Pending + AssignedNode=it-node-a”。节点崩溃后故障恢复只重置了 2 个 Running 任务，**另外 2 个一直停在 Pending/it-node-a**，再等 25 秒也没有被处理（M-02） |
| 本机实测 | 用一个最小控制台程序测试 `dotnet run --project <项目>` 时的工作目录；查看系统内存占用 | 工作目录是执行命令时所在的目录，不是项目目录或输出目录（M-22）。本机内存 16 GB，系统占用 58%（M-07） |

## 2. 严重度定义

| 级别 | 含义 |
|---|---|
| P0 | 数据丢失、安全漏洞，或常见的崩溃 |
| P1 | 功能错误，或较常见的可靠性问题 |
| P2 | 边界情况，或对可维护性影响较大 |
| P3 | 代码风格、轻微问题 |

“验证”一栏写建议的测试类型：
- **单元**：xUnit + Moq 或 SQLite 临时库就够。
- **集成**：需要 `CrawlerCore.Tests/Integration/` 的进程内 Master + 真实 Worker。
- **界面**：只能人工在界面上复测，T4 交付时要列出步骤。

## 3. 统计与修复顺序

| 严重度 | 数量 | 编号 |
|---|---|---|
| P0 | 2 | M-01、M-02 |
| P1 | 7 | M-03 – M-09 |
| P2 | 15 | M-10 – M-24 |
| P3 | 16 | M-25 – M-40 |

问题之间的依赖，以及建议 T4 采用的修复顺序：

1. **M-01 与 M-09 一起改**：启用认证拦截器之前，先改掉它“所有异常都转成 Internal”的处理，否则会话失效等状态码全部变成 Internal。
2. **任务状态流转一起设计**：M-02、M-10、M-11、M-17 都改任务状态的写入。建议先在 `TaskDataService` 中确定状态机（M-10），再调整调度写库顺序与故障恢复范围（M-02），最后让 Completed 与结果落库同时发生（M-11）。
3. **双向流一起改**：M-05 与 M-16 都改 `StartStreaming` 及其清理逻辑。
4. **M-03 不依赖 Worker 侧 D1**：推荐的修法是“注册时替换没有活跃流的旧会话”。不要在断流时直接删除会话：Worker 目前用旧会话重连，删掉会话会让它陷入 D1 的无限重连。
5. **实体结构变化**：M-04 要给 `CrawlResults.TaskId` 加唯一索引，M-34 可能新增列。按 CLAUDE.md 约定，旧 `crawler.db` 需要删除重建。T4 交付时要提醒用户，最好与 M-23（重建前自动备份）一起做。

## 4. 问题清单

### P0

| 编号 | 严重度 | 文件:行号 | 问题 | 触发场景 | 修复建议 | 验证 |
|---|---|---|---|---|---|---|
| M-01 | P0 | `Hosting/MasterHostingExtensions.cs:65-72`（`AddGrpc` 未加拦截器）、`:144`；`Infrastructure/GrpcAuthenticationInterceptor.cs` | **节点令牌完全不校验**。拦截器只注册到了容器，没有加入 `AddGrpc` 的 `Interceptors` 列表，只有未被使用的 `GrpcHostedService` 用到它。gRPC 端口监听所有网卡（`:45`），并且是明文传输。 | **T1（D4）**，Skip 测试 `令牌错误的节点注册被拒绝`。同一局域网内的任何人都能调用 `RegisterNode`/`StartStreaming`：冒充节点领取任务（包括任务配置），或者对任意 TaskId 发送伪造的 `TaskProgress`/`CrawlResult`（Master 不校验任务归属，见 M-04）。这样可以把任务标为完成、写入伪造数据，这些数据会随导出 JSON 进入数据分析。 | 在 `AddMasterWebServices` 的 `AddGrpc(opt => …)` 中加入 `opt.Interceptors.Add<GrpcAuthenticationInterceptor>()`，容器里现有的单例注册可以保留。**同时完成 M-09。** 令牌为空时启动打 Error 日志，并拒绝所有节点（这是拦截器现有的行为）。默认令牌的处理见 M-21。 | 集成：去掉 D4 测试的 Skip；补一个“没有 authorization 头的 `StartStreaming` 被拒绝”的用例；全部端到端测试仍需通过（`TestMaster`/`TestWorker` 已经使用同一令牌）。 |
| M-02 | P0 | `Services/TaskSchedulerHostedService.cs:175-184`（先下发、后写 Running）、`:261-263`（重启恢复只取未分配的 Pending）；`Services/TaskDataService.cs:98-99`、`:125-126`（故障恢复只查询、只重置 Running）；`Services/CrawlerCoordinatorService.cs:562`（Worker 上报的状态无条件落库） | **在 Worker 本地排队的任务，会在节点离线后永久丢失**。Worker 收到任务放入本地队列时会上报 Pending（`CrawlerWorker/Services/WorkerHostedService.cs:906-918`），覆盖调度器刚写入的 Running，库中就成了“Pending + 已分配节点”。节点离线时，故障恢复只处理 Running；Master 重启时，恢复逻辑只处理未分配的 Pending。这些任务从此不会再被调度。 | **已验证**（探针 2）。两个 Worker、`MaxConcurrentTasks=2`，连续创建 4 个任务（M-06 让它们都落在同一个节点上），其中 2 个在 Worker 本地排队。该节点崩溃后，这 2 个任务永久停在 Pending/原节点。Worker 正常停止时（Ctrl+C、`docker stop`、改代码后重启）本地队列被直接丢弃，结果相同。 | **Master 侧（T4）**：<br>① `GetRunningTasksByNodeIdAsync`/`BatchResetFailedTasksAsync` 改为处理“分配给该节点且尚未结束（Pending 或 Running）”的任务，方法名同步改掉；<br>② `RecoverPendingTasksFromDatabaseAsync` 改为恢复两类任务：所有 Pending 任务（不论是否已分配，清空 AssignedNode 后入队），以及所分配节点在库中已是 Offline 的 Running 任务。用 `Where` 在数据库里筛选，不要先 `GetAllTasksAsync` 再在内存里过滤；<br>③ `AssignTaskToOptimalNodeAsync` 先用一次 `SaveChanges` 写入 AssignedNode 和 Running，再下发；下发失败时改回 Pending/未分配；<br>④ `HandleTaskProgressMessage` 收到同一节点上报的 Pending，而库中已是 Running 时，不降级（见 M-10）。<br>**Worker 侧（T5）**：任务排队时不再上报 Pending。 | 集成：把探针 2 写成正式测试。两个 Worker、`maxConcurrentTasks: 1`，扣住请求后创建 3 个任务，让有排队任务的节点崩溃，断言所有任务都变为 Completed，且各有 1 份结果。单元：`TaskDataService` 的新查询，以及重启恢复的筛选条件。 |

### P1

| 编号 | 严重度 | 文件:行号 | 问题 | 触发场景 | 修复建议 | 验证 |
|---|---|---|---|---|---|---|
| M-03 | P1 | `Services/SessionManager.cs:15`、`:29-48`；`Hosting/MasterHostingExtensions.cs:143`；`Services/CrawlerCoordinatorService.cs:295-362`（清理时不移除会话） | **同一 NodeId 的 Worker 崩溃后，30 分钟内无法重新注册**。流断开时不移除会话；对于 30 分钟内活跃过的会话，`RegisterNode` 一律拒绝。`SessionManager` 继承了 `BackgroundService`，却只注册为单例，过期清理循环从不运行。 | **T1（D3）**，Skip 测试 `Worker崩溃后以相同NodeId重启能重新注册上线`。Worker 崩溃后被 `restart_policy` 拉起，或用户手动重开（NodeId 不变），注册返回 “Node already has an active session”，Worker 随即退出。 | `RegisterNode` 发现已有会话时：<br>• 该节点**没有活跃流**（协调服务的 `_nodeStreamContexts` 中没有它）→ 直接换成新会话；<br>• 仍有活跃流 → 照旧拒绝，防止两个 NodeId 相同的 Worker 互相踢下线。半开连接由 M-05 的 keepalive 清理。<br>为了让协调服务判断“有没有活跃流”，`SessionManager` 不要反过来依赖协调服务，可以由 `CrawlerCoordinatorService.RegisterNode` 先判断，再调用 `SessionManager.ReplaceSession`。<br>另外，加上 `AddHostedService(sp => sp.GetRequiredService<SessionManager>())`；过期清理只清理没有活跃流的会话。 | 集成：去掉 D3 测试的 Skip；补一个“Worker 短暂断流后用旧会话重连仍然可用”的用例（与 M-04 的测试合并）。 |
| M-04 | P1 | `Services/CrawlerCoordinatorService.cs:295-335`（任何断流都立即标为离线）、`:464-535`、`:540-601`（不校验任务归属）；`Services/NodeManagementService.cs:102-115`（心跳把 Offline 拉回 Online）；`Data/CrawlerDbContext.cs:69-80`（`CrawlResults.TaskId` 没有唯一约束） | **节点被判离线后，旧节点不会被隔离，任务被重复执行，结果重复入库**。故障恢复把离线节点的任务改派出去，但既不通知旧节点，也不拒绝它之后发来的消息：旧节点仍在执行，之后的进度和结果照单全收。 | **已验证**（探针 1）。Worker 与 Master 之间的连接短暂断开（网络抖动、Wi-Fi 切换、连接被重置）→ Master 立即标离线并重置任务 → Worker 几百毫秒后用旧会话重连 → 任务再次下发给同一节点 → 两次执行各交一份结果。库中同一 TaskId 有 2 份 `CrawlResult`，数据查看页出现两行，进度在 Running 和 Pending 之间来回跳。改派给另一个节点时同理：两个节点都交结果，后到的进度会覆盖状态。 | ① `HandleTaskProgressMessage`/`HandleCrawlResultMessage` 先查任务：`AssignedNode` 非空且不等于发送方 nodeId 时，丢弃消息并记 Warn（T9 实现 TaskControl 后，再给旧节点下发 Cancel）；任务不存在时也丢弃。<br>② 结果去重：给 `CrawlResults.TaskId` 加唯一索引（属于实体结构变化，需要重建 crawler.db）；插入前检查，已存在则丢弃后到的一份并记 Warn。<br>③ 可选：断流后给节点一个宽限期（例如 `NodeTimeoutSeconds`），宽限期内重连就不重置任务。<br>④ Worker 侧（T5）：收到自己正在执行的同一个 TaskId 时忽略。 | 集成：把探针 1 写成正式测试，断言 `CountCrawlResultsAsync == 1` 且进度单调；再加一个双节点版本，被断开的节点恢复后，任务最终只在幸存节点上有 1 份结果。 |
| M-05 | P1 | `Hosting/MasterHostingExtensions.cs:31-57`（未配置 HTTP/2 keepalive）；`Services/CrawlerCoordinatorService.cs:240-247`（同一节点已有流时拒绝新流）；`Services/NodeHealthCheckService.cs:86-106`（判离线只改库） | **Master 发现不了半开连接，失联节点恢复后可能永久无法重新上线**。Kestrel 默认不发 HTTP/2 PING，gRPC 流式调用也不受最小数据速率的限制。健康检查判离线后，既不关闭流，也不移除会话。之后同一节点的新流会被 `AlreadyExists` 拒绝，重新注册会被会话拒绝（M-03）。 | **待验证**。T1 的黑洞测试已证明：失联期间 Master 侧的流不会结束（`ActiveConnectionCount > 0`）。场景：Worker 网络中断（拔网线、VPN 断开、休眠唤醒）超过 1 分钟，期间 Master 没有向它写数据，所以收不到 RST。恢复后有两种结局：<br>• 旧 TCP 连接恢复 → 心跳把节点拉回 Online，已改派的任务被执行两次（M-04）；<br>• Worker 已放弃旧连接、新建连接 → `StartStreaming` 一直返回 `AlreadyExists`，重新注册又被拒绝，Worker 退出。在 Master 重启之前，这个节点都无法再上线。 | ① 在 `UseMasterKestrel` 中设置 `options.Limits.Http2.KeepAlivePingDelay`（如 15s）和 `KeepAlivePingTimeout`（如 30s），做成配置项；<br>② `StartStreaming` 遇到同一节点的旧流且会话有效时，取消旧流（旧 `Cts.Cancel()`）后换成新流，不再拒绝；<br>③ 健康检查把节点判为离线时，同时断开该节点的流：给 `ICrawlerCoordinatorService` 增加 `DisconnectNode(nodeId)`，健康检查服务在 `MarkInactiveNodesAsOfflineAsync` 返回的节点上调用它（需要让该方法返回节点 ID 列表）。<br>Worker 侧的 keepalive 见 T3。 | 集成：`BlackHole` → 等待离线 → `Restore`，断言节点在 N 秒内重新 Online，且任务结果只有 1 份。注意 `TcpFaultProxy` 的黑洞会丢弃数据，恢复后旧连接会因帧错乱而断开，不能完全模拟 TCP 重传，所以还需要**人工复测**：禁用网卡 2 分钟后再启用。 |
| M-06 | P1 | `Services/LoadBalancingScheduler.cs:22-61`、`:82-101`；`Services/TaskSchedulerHostedService.cs:159-175` | **一次创建多个任务时，全部分给同一个节点**。负载均衡只看心跳上报的 CPU 和内存，不看节点上已分配的任务数。心跳 5 秒一次，调度循环每 100 毫秒就分配一个任务，同一个心跳周期内各节点得分不变，所有任务都落到得分最高的节点上。 | **已验证**（探针 2）。两个空闲节点，连续创建 4 个任务，4 个全部分给 it-node-a，it-node-b 一直空闲。a 的 `MaxConcurrentTasks=2`，多出来的 2 个在本地排队，并触发 M-02。日常使用中，一次创建多个任务就会遇到。 | 选节点时以“已分配且尚未结束的任务数”为主要依据：<br>• `AssignTaskToOptimalNodeAsync` 用一条分组查询统计各节点 `Status in (Pending, Running)` 的任务数，传给 `SelectOptimalNode`（新增参数或重载）；<br>• 先选任务数最少的节点，再用现有得分打破平局。<br>协议里目前没有节点的并发上限。可以在 `NodeRegistration` 末尾追加 `max_concurrent_tasks`（向后兼容的扩展，需要同步更新 `ProtoContractTests`）；任务数已满的节点不再分配，任务留在 Master 的队列里。 | 单元：在 `LoadBalancingSchedulerTests` 中增加“任务数少的节点优先”。集成：两个 Worker、4 个任务，断言每个节点各分到 2 个。 |
| M-07 | P1 | `Services/LoadBalancingScheduler.cs:31-46` | **常见的 Windows 开发机上，节点永远不满足资源阈值，任务永远不会下发**。过滤阈值写死为 CPU<50%、内存使用率<60%，没有节点满足时返回 null，任务一直留在队列里，只是每秒打一条 Warn。Windows 上 Worker 上报的是整机已用内存（`CrawlerWorker/Services/NodeInfoCollector.cs:478`，`ullTotalPhys - ullAvailPhys`）。 | **已验证**（本机实测）。本机内存 16 GB，系统占用已经 58%，再开一个浏览器或 Visual Studio 就会超过 60%。在 Windows 上直接运行 Worker 时，节点永远不合格，任务一直显示“待执行”，界面上没有任何提示。（另外，Windows 上 CPU 使用率恒为 0（T1 已发现，Worker 侧），CPU 阈值在 Windows 上实际不起作用。） | ① 阈值改为配置项，例如新增 `LoadBalancingSettings:MaxCpuPercent`、`MaxMemoryPercent`，默认放宽到 90；<br>② 没有节点满足阈值时，退而选择负载最低的在线节点，并记 Warn。不要让任务无限期等待。 | 单元：只有一个内存占用 70% 的节点时，它仍能被选中；阈值能从配置读取。 |
| M-08 | P1 | `Views/DataViewerView.xaml.cs:11-20` | **切换过一次页面后，数据查看页不再自动刷新**。视图在 `Unloaded` 时把 `DataContext`（也就是单例 `DataViewerViewModel`）Dispose 掉，取消了 `CrawlResultReceivedEventArgs` 订阅。而左侧导航切换页面时，`ContentControl` 会卸载旧视图。 | **代码确认**。打开“数据查看”→ 切到“节点监控”→ 再切回来（拿到的是同一个已经 Dispose 的 ViewModel）→ 之后有新结果到达也不会自动刷新，只能手动点“刷新列表”。这是“视图数据偶发不同步”问题残留的原因之一。 | 删除 `Unloaded` 中的 Dispose（单例由容器在程序退出时释放）。如果确实需要按视图的生命周期订阅事件，就在 `Loaded` 时订阅、`Unloaded` 时退订，而不是 Dispose。 | 界面：切换页面后再创建任务，结果到达时数据查看页自动出现新行。 |
| M-09 | P1（M-01 的前置条件） | `Infrastructure/GrpcAuthenticationInterceptor.cs:52-58`、`:86-92` | 拦截器捕获所有异常，统一转成 `StatusCode.Internal`，消息里还带着 `ex.Message`。拦截器目前没有启用，但 M-01 启用它之后：<br>• `StartStreaming` 抛出的 `PermissionDenied`（会话无效）、`AlreadyExists` 都会变成 `Internal`；<br>• Worker 断开时产生的 `OperationCanceledException`/`IOException` 会被记成 Error。 | **代码确认**。修完 M-01 后：Master 重启，Worker 用旧会话重连，收到的是 Internal 而不是 PermissionDenied，Worker 按状态码区分的逻辑（T5 修 D1 时会用到）就失效了；每次 Worker 断开都打一条 Error 日志；内部异常信息也会返回给客户端。 | ① `catch (RpcException) { throw; }`，原样抛出；<br>② `OperationCanceledException`/`IOException`（客户端断开）记 Information 后重新抛出；<br>③ 其余异常转成 Internal 时只返回固定文本，不带 `ex.Message`，详细信息只写日志。 | 单元：RpcException 原样透传；普通异常只返回固定消息。集成：M-01 的测试一并覆盖。 |

### P2

| 编号 | 严重度 | 文件:行号 | 问题 | 触发场景 | 修复建议 | 验证 |
|---|---|---|---|---|---|---|
| M-10 | P2 | `Services/TaskDataService.cs:57-64`；`Services/CrawlerCoordinatorService.cs:548-579`；`Services/TaskSchedulerHostedService.cs:183-184` | **任务状态没有状态机**。`UpdateTaskStatusAsync` 读出实体后直接覆盖状态：不管来自哪里、先后顺序如何，都会落库。未知 TaskId 的进度也会发布事件。 | **T1 + 代码确认**：<br>• Worker 排队时上报的 Pending 晚于调度器的 Running 落库（T1 日志可见），导致 M-02；<br>• 被改派节点迟到的 Completed 覆盖新节点的 Running（M-04）；<br>• **待验证、概率低**：SQLite 繁忙时，调度器写 Running 晚于 Worker 的 Completed/Failed，任务永远停在 Running；<br>• 数据库重建后，旧 Worker 的进度会在任务列表里加出名称为空的行。 | 在 `TaskDataService` 中集中实现允许的状态转换：Pending→Running→Completed/Failed，终态不能回退。“重置为 Pending”只允许故障恢复和重启恢复通过专门的方法完成（例如现有的 `BatchResetFailedTasksAsync`）。用带条件的更新保证原子性，例如 `ExecuteUpdateAsync`，条件为 `TaskId == id` 且当前状态属于允许的来源状态，返回是否生效。`HandleTaskProgressMessage` 对没有生效的更新和未知任务不发布事件。 | 单元：`TaskDataService` 的各种转换（终态不回退、Running 不降为 Pending）。集成：现有的“进度不回退”测试。 |
| M-11 | P2 | `Services/CrawlerCoordinatorService.cs:540-565`（Completed 由进度消息落库）、`:464-502`（结果另行落库） | **“已完成”不代表“结果已落库”**。Worker 先发 Completed 进度、后发 `CrawlResult`，Master 一收到进度就把任务标为完成。结果只要没到，任务就永久是“已完成、无数据”，故障恢复也不会再处理。结果没到的情况有：Worker 停止时先取消流、再等发送队列（D2）；结果超过消息上限（M-19）；落库失败（M-12）。 | **T1**：D2 测试中，Master 显示已完成，但没有任何数据。正常情况下两条消息之间也有一个短暂窗口（T1 记为 P3）。 | Master 收到 Completed 进度时只更新页数，库中保持 Running，发布的事件可以带“收尾中”的含义。在 `HandleCrawlResultMessage` 成功写入结果的**同一个事务**里，再把任务改为 Completed，并发布进度事件。这样“已完成”一定有数据；结果丢失的任务保持 Running，节点离线时能被故障恢复重做。 | 单元：协调服务收到 Completed 进度、但还没收到结果时，任务不是 Completed。集成：D2 测试去掉 Skip（需要 T5 先修好 Worker）。 |
| M-12 | P2 | `Services/CrawlerCoordinatorService.cs:499-502` | **结果落库失败时，数据静默丢失**。失败时只记一条 Error，随后照常发布“收到结果”事件。Worker 那边已经丢弃了结果，数据找不回来。 | **代码确认**。SQLite 繁忙超时、磁盘已满、文件被其他程序锁定时：任务显示已完成（M-11 未修时），数据查看页没有数据，界面上也没有任何提示。 | 落库失败时把任务标为 Failed 并记录原因，不发布“收到结果”事件，改为发布任务进度事件，让界面显示失败。也可以保持 Running，交给故障恢复重做；这一点与 M-11 一起设计。 | 单元：用缺表的临时 SQLite 让 `SaveChanges` 抛异常，断言任务变为 Failed。 |
| M-13 | P2 | `CrawlerCore/Events/EventAggregator.cs:51-58` | **订阅者的异常被完全吞掉**。订阅者抛出的异常只写了 `Debug.WriteLine`，而这个方法带 `[Conditional("DEBUG")]`，Release 构建中连这一行都没有。 | **代码确认**。任何订阅者（故障恢复、实时推送、各 ViewModel）出错都不会留下日志，“视图偶发不同步”这类问题无从排查。 | 改为 `LogHelper.Error(ex, $"事件处理程序执行失败: {typeof(T).Name}")`，并继续保证一个订阅者出错不影响其他订阅者。 | 单元：一个订阅者抛异常时，其他订阅者仍会被调用。 |
| M-14 | P2 | `ViewModels/TaskManagementViewModel.cs:121-127`；`Services/TaskSchedulerHostedService.cs:183-184`；`Services/FailedTaskRecoveryService.cs:143-178` | **界面上的任务状态与库中不一致**。Master 自己修改的任务状态（调度器写 Running、故障恢复重置为 Pending）不发布任何事件；任务列表的“刷新”只更新名称和节点，不更新状态。 | **代码确认**。节点离线后，任务被重置为 Pending、等待调度：界面仍显示“运行中”；刷新后节点一栏变成“--”，状态还是“运行中”。没有可用节点时会一直这样。 | 调度器和故障恢复在改变状态后发布 `TaskProgressUpdatedEventArgs`（或新增一个任务状态事件）。`LoadTaskListAsync` 对已有任务同时更新 `Status`（M-10 修好后，库中状态可以作为准）。 | 单元：故障恢复重置任务后发布事件。界面：断开 Worker 后，任务状态变为“待执行”。 |
| M-15 | P2 | `ViewModels/NodeMonitorViewModel.cs:57-67`、`:90-115` | **切换选中节点时，事件订阅泄漏**。`SetProperty` 已经把字段改成了新节点，`OnSelectedNodeChanged` 第 93-97 行的“取消旧订阅”实际作用在新节点上。旧节点的 `CollectionChanged` 订阅永远不会被移除。 | **代码确认**。在节点列表里来回点选，每点一次就多一个订阅，此后每次心跳都触发多次整图重绘（每个数据点还会因 Add 加 RemoveAt 触发两次通知）。长时间运行、节点较多时，界面越来越卡。 | 在 setter 中先保存旧值：`var old = _selectedNode; if (SetProperty(ref _selectedNode, value)) OnSelectedNodeChanged(old, value);`，对 old 退订，对 value 订阅。 | 单元（可选，要绕开 `Application.Current`）。界面：反复切换选中节点后，CPU 占用不随切换次数上升。 |
| M-16 | P2 | `Services/CrawlerCoordinatorService.cs:258-269`、`:271-292`、`:295-303` | 流的清理有三个问题：<br>① 取消回调（`async void`）和 `finally` 各调用一次 `CleanupNodeStreamAsync`，两次可能并发执行，离线事件和写库都会重复（这是 T1 记录的重复事件的来源之一）；<br>② `TryRemove(nodeId, out _)` 只按键删除，旧流的第二次清理可能删掉同一节点刚建立的新流；<br>③ 读循环异常结束时（例如消息超限），写循环还在等通道，`StartStreaming` 不会返回，清理也不会执行。 | ① **T1**。② **待验证**：Worker 断开后很快重连，旧流的清理晚于新流的 `TryAdd` 时，新流的上下文被删除，Master 把在线节点标为离线、重置它的任务，之后给它下发任务也一直失败。③ **待验证**：见 M-19，Master 不再读这个节点的消息，也不结束调用，Worker 察觉不到。 | ① 每个 `NodeStreamContext` 用 `Interlocked` 标志保证清理只执行一次；<br>② 改用 `_nodeStreamContexts.TryRemove(new KeyValuePair<string, NodeStreamContext>(nodeId, streamContext))`，只有删掉的确实是自己时，才标离线、发事件；<br>③ 用 `CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken)` 代替 `Register(async …)`；`await Task.WhenAny(readTask, writeTask)` 之后取消 `streamCts`，再等两个循环都结束。 | 单元：在 `Grpc/StreamingAndHeartbeatTests.cs` 中用假流验证“读循环抛异常时 `StartStreaming` 会返回”和“旧流的清理不会删除新流”。集成：断流重连（即探针 1 的场景）。 |
| M-17 | P2 | `Services/TaskSchedulerHostedService.cs:150-193`、`:208-241` | 调度循环的错误处理有四个问题：<br>① 查询任务或节点时抛异常，任务已经出队，却不会重新入队，要到 Master 重启才会恢复；<br>② 下发成功后写库返回 false，也返回 null，任务被重新入队、再次下发；<br>③ 不检查任务的当前状态，任务不存在时也每秒重新入队一次；<br>④ 候选节点取自库中的 Online 节点，不管有没有活跃流。选择是确定性的，下发失败后，下一轮还会选同一个节点。 | **代码确认**：<br>• ① SQLite 繁忙超时一次，任务就停在 Pending；<br>• ④ Master 重启后的 60 秒内，库中仍为 Online 的旧节点得分最高，任务反复下发失败，已经重新连上的节点却被忽略。最长约 75 秒，直到健康检查把旧节点判为离线。 | ① 把单个任务的分配包在 try/catch 里，出异常时重新入队；<br>② 用枚举区分结果：已分配、无可用节点、下发失败、任务不存在或已不是 Pending。只对“无可用节点”和“下发失败”重新入队；<br>③ 下发前确认 `Status == Pending`；<br>④ 给 `ICrawlerCoordinatorService` 加 `IsNodeConnected(nodeId)`，先用它过滤候选节点；下发失败的节点在下一次尝试中排除。 | 单元：在 `TaskSchedulerHostedServiceTests` 中用 Mock 覆盖各个分支。 |
| M-18 | P2 | `Services/FailedTaskRecoveryService.cs:105-119` | 同一节点 30 秒内的第二次离线事件被直接跳过；正在处理中的节点也直接跳过，而不是排队等待。M-03 修好后，节点可以很快重新上线，这个窗口就会吞掉真实的第二次离线。 | **代码确认（M-03 修好后才会出现）**。某个任务会让 Worker 崩溃（“毒任务”）：节点离线 → 恢复 → Worker 被拉起并重新注册 → 又领到任务 → 30 秒内再次崩溃 → 这次的离线事件被跳过，任务永久停在“Running + 离线节点”。健康检查只处理 Online 节点，之后也不会再触发。 | 去掉这个时间窗口：`BatchResetFailedTasksAsync` 只重置仍为 Running 的任务，本身就是幂等的。同一节点的事件改为用每个节点一把的 `SemaphoreSlim` 串行处理，等待而不是跳过。 | 单元：在 `FailedTaskRecoveryServiceTests` 中增加“30 秒内第二次离线，仍会重置新分配的任务”。 |
| M-19 | P2 | `Hosting/MasterHostingExtensions.cs:65-72`（单条消息上限 64 MB）；`ViewModels/CreateTaskViewModel.cs:33-35`（MaxPages 最大 10000） | 一个任务的所有页面都放在一条 `CrawlResult` 里回传。按估算，arXiv 每页约 10 KB（摘要加上约 100 个链接），约 6000 页就超过 64 MB。超限后 Master 读取失败，流中断或挂起（M-16 ③）。 | **待验证**。创建 MaxPages 很大的任务：爬完后结果被拒，节点被判离线，任务被重置，换一个节点从头再爬，如此循环。T6 的 API 插件一次返回多篇论文，更容易碰到这个上限。 | 协议层分批回传结果，例如在 `CrawlResult` 末尾追加 `bool is_last`：Worker 每 N 页发一批，Master 追加写入（向后兼容的扩展，需要更新 `ProtoContractTests`）。或者，让 MaxPages 的上限与消息上限对齐，超限时把任务标为 Failed。再给任务加一个重试次数，超过后标为 Failed，避免无限重做。建议与 T6 一起设计。 | 集成：把 `MaxMessageSizeMb` 配成 1，运行一个结果超过 1 MB 的任务。 |
| M-20 | P2 | `Hosting/MasterHostingExtensions.cs:52-56`、`:75`、`:78-87`、`:101`、`:108`；`Services/Hubs/CrawlerHub.cs` | **SignalR 对外完全开放**。Hub 没有认证，监听所有网卡；CORS 允许任意来源并允许携带凭据；还开启了详细错误。仓库里并没有任何 SignalR 客户端。 | **代码确认**。局域网内的任何人，或者 Master 机器上打开的任意网页（通过浏览器里的脚本），都能连上 `/crawlerHub`，持续收到节点 IP、机器名、任务名和进度。 | ① 默认只监听 `127.0.0.1`，监听地址和端口做成配置；<br>② CORS 改用配置中的来源白名单，去掉 `SetIsOriginAllowed(_ => true)` 和 `AllowCredentials`；<br>③ `EnableDetailedErrors` 只在 Development 环境开启。<br>如果决定不保留 SignalR，也可以整体移除，并记录这个决定。 | 单元或集成：非白名单 Origin 的 negotiate 请求被拒绝；只监听环回地址。 |
| M-21 | P2 | `CrawlerMaster.WPF/appsettings.json:18`（`CrawlerWorker/appsettings.json` 也一样） | 公开仓库里提交了默认令牌 `test-token`，gRPC 又是明文 HTTP/2。M-01 修好之后，按默认配置部署时，令牌仍然是公开的。 | **代码确认**。用户没改配置就部署，任何看过这个仓库的人都能通过认证。 | ① appsettings 中的令牌留空，通过环境变量提供（Master 用 `GrpcSettings__NodeAuthToken`，Worker 用 `NODE_AUTH_TOKEN`）；<br>② Master 启动时，令牌为空或等于 `test-token` 就记 Error 并拒绝所有节点；<br>③ 在 `docs/project/deployment.md` 中说明如何设置令牌。TLS 作为可选项记录在案。 | 单元：启动时的令牌校验逻辑。 |
| M-22 | P2 | `App.xaml.cs:35`（`Host.CreateDefaultBuilder` 的内容根是当前工作目录）；`CrawlerCore/Infrastructure/DatabaseSettings.cs:10`（`Data Source=crawler.db` 是相对路径） | **配置文件和数据库的位置取决于启动时的工作目录**，而不是程序所在的目录。 | **已验证**（本机实测了 `dotnet run --project` 的工作目录）。按 CLAUDE.md 在仓库根目录执行 `dotnet run --project CrawlerMaster.WPF` 时：<br>• `appsettings.json` 不会被加载，所有配置都用默认值，令牌为空；<br>• 在仓库根目录新建一个空的 `crawler.db`，看起来就像“数据全丢了”；<br>• `.gitignore` 没有忽略 `*.db`，这个文件还可能被提交到公开仓库。 | ① `App.xaml.cs` 中加 `.UseContentRoot(AppContext.BaseDirectory)`；<br>② `AddMasterCoreServices` 中用 `SqliteConnectionStringBuilder` 把相对的 Data Source 解析到 `AppContext.BaseDirectory` 下；<br>③ `.gitignore` 增加 `*.db`。<br>从 VS 启动的现有用户（工作目录本来就是输出目录）数据库位置不变。 | 单元：连接字符串的解析。界面：在仓库根目录 `dotnet run` 后，原有数据仍在。 |
| M-23 | P2 | `Hosting/MasterHostingExtensions.cs:165-191` | 只用 `NodeInfos` 表是否存在来判断数据库是否可用：缺这张表，或者 `CanConnectAsync` 返回 false，就用 `EnsureDeletedAsync` 删除整个文件；而缺列（实体加了字段、库没有重建）的情况完全检测不到。 | **代码确认**：<br>• 用其他工具改过库，或旧版本的库缺某张表时，一启动就删除全部爬取数据，无法恢复；<br>• 实体结构变化后（例如 M-04 的唯一索引、M-34 的新列）用旧库启动，运行中各处报 “no such column”。 | ① 删除前先把文件改名备份为 `crawler.db.bak-yyyyMMddHHmmss`；<br>② 检查四张表及关键列（`PRAGMA table_info`）。结构不符时：库里没有数据就直接重建；有数据就先备份再重建，并在界面上提示备份文件的位置。 | 单元：临时 SQLite 文件缺表或缺列时，会生成备份并重建。 |
| M-24 | P2 | `App.xaml.cs:108-137` | `OnExit` 是 `async void`：遇到第一个真正异步的 `await AppHost.StopAsync` 就返回，WPF 随即关闭调度器、退出进程。后面的 `AppHost.Dispose()`、`Log.CloseAndFlush()`、`base.OnExit` 很可能都不执行。出异常时只记一句固定文本，`ex` 本身没有记录。 | **待验证**（查看日志里有没有 `StopAsync` 之后的记录）。关闭主窗口时，后台服务来不及正常停止，Worker 看到的是连接被重置；退出阶段的日志可能丢失。 | 在 `MainWindow.Closing` 中处理：第一次关闭时取消关闭，异步完成 `StopAsync`（带超时）后再调用 `Shutdown()`。或者在 `OnExit` 中同步等待 `Task.Run(() => 关闭逻辑).Wait(超时)`，避免续体回到 UI 线程。异常要写进日志。 | 界面：关闭 Master 后 5 秒内进程退出，日志末尾有“退出完成”的记录。 |

### P3

| 编号 | 严重度 | 文件:行号 | 问题 | 触发场景 | 修复建议 | 验证 |
|---|---|---|---|---|---|---|
| M-25 | P3 | `Services/CrawlerCoordinatorService.cs:148-152`、`:252-256`、`:340-344`；`Services/NodeManagementService.cs:64-69`、`:111-120`、`:196-197` | **节点状态事件重复，来源分散**：<br>• 注册时 Online 事件发布 3 次，断流时 Offline 事件发布 2–3 次；<br>• 协调服务发布的事件里 `NodeInfo` 是空对象（机器名、IP 为空，资源为 0）；<br>• 心跳事件在 `SaveChangesAsync` 之前发布；<br>• 发布时持有全局的 `_writeLock`，订阅者在锁内同步执行。 | **T1**（重复事件）。故障恢复和健康检查的日志里机器名、IP 为空；节点监控的曲线上出现资源为 0 的点；以后订阅者变多，容易在锁内做重活。 | 节点状态事件只由 `NodeManagementService` 在库中状态真正变化后发布，并且在释放锁之后发布；协调服务不再发布这类事件；心跳事件改到保存之后发布。 | 单元：注册一次只收到一个 Online 事件。 |
| M-26 | P3 | `CrawlerCore/Infrastructure/DatabaseSettings.cs:8`；`CrawlerMaster.WPF/appsettings.json:20` | 代码读取 `Database:ConnectionString`，appsettings 里写的却是 `ConnectionStrings:ConnectionString`。 | **T1**。目前碰巧与默认值相同；改了 appsettings 里的连接字符串也不会生效。 | 把 appsettings 中的节名改为 `Database`，或者让代码同时读取 `ConnectionStrings`（二选一，并同步 `docs/project/deployment.md`）。 | 单元：配置绑定。 |
| M-27 | P3 | `CrawlerCore/Common/LogHelper.cs:15-34`；调用处 `Services/FailedTaskRecoveryService.cs:107`、`:117`、`:134`、`:146`、`:176`、`:187` | `LogHelper.Info/Warn/Debug/Error(message, [CallerMemberName] member)` 与 `Info(format, params object[])` 两个重载有歧义：传入**一个字符串参数**时，会绑定到 `member`。结果是 `{NodeId}` 占位符没有被替换，节点 ID 被当成了“方法名”。 | **代码确认**（C# 重载决议：`(string, string)` 优先于 `params object[]`）。日志显示为 `[it-node-a] 节点 {NodeId} 无运行中的任务`。 | 带占位符的日志改用 Serilog 的 `Log.Information(template, args)`，或者给 `LogHelper` 增加名字不同的 `InfoFormat` 等方法；上面列出的调用处一并修改。 | 单元：可选。 |
| M-28 | P3 | `CrawlerCore/Entities/NodeInfo.cs:75`、`CrawlerTask.cs:81`、`CrawlResult.cs:35`、`PageResult.cs:47`；`Services/CrawlerCoordinatorService.cs:400-401`、`:542`；`Services/RealtimeDataSyncService.cs:291`、`:330` | **日志噪音大**：<br>• 实体的构造函数里写日志，EF 每实例化一个实体就写一条（`NodeInfo`、`CrawlerTask` 是 Information 级）；<br>• 每次心跳、每条进度、每次推送都写一条 Information；<br>• 大量使用字符串插值，而不是消息模板。 | **代码确认**。每个节点每次心跳约产生 4 条 Information 日志，刷新一次任务列表，每个任务产生一条。日志按 10 MB × 7 个文件滚动保存，几个节点运行一天，较早的有用日志就被覆盖了。 | 删除实体构造函数中的日志；心跳、进度、推送的日志降为 Debug；改用消息模板。 | 无需测试。 |
| M-29 | P3 | `Services/SessionManager.cs:61`；`Services/CrawlerCoordinatorService.cs:186`；`Hosting/MasterHostingExtensions.cs:67` | 会话 ID 以 Information 级别写进日志（在 M-01 修好之前，会话 ID 是建流时唯一的凭据）；gRPC 开启了 `EnableDetailedErrors`，异常信息会返回给客户端。 | **代码确认**。能读到日志文件的人可以拿会话 ID 冒充节点建流；客户端能看到服务端的异常细节。 | 日志中不写会话 ID（或者只写前几位）；`EnableDetailedErrors` 只在 Development 环境开启。 | 无需测试。 |
| M-30 | P3 | `Services/GrpcHostedService.cs`（整个文件）；`Hosting/MasterHostingExtensions.cs:148`；`Services/CrawlerCoordinatorService.cs:35-84`、`:799-802`；`Services/FailedTaskRecoveryService.cs:27`、`:155-161`、`:296-299`；`Services/NodeHealthCheckService.cs:127-130`；`ViewModels/DataViewerViewModel.cs:401-404`；`Data/CrawlerDbContext.cs:15-21`；`CrawlerMaster.WPF.csproj:24-25`；`App.xaml.cs:53` | **死代码和无用依赖**：<br>• `GrpcHostedService` 没有注册，是基于 Grpc.Core `Server` 的旧实现；<br>• `CrawlerCoordinatorBase` 的别名注册；<br>• 协调服务的 7 个事件里，`StreamConnected`/`StreamDisconnected` 从不触发，其余的除测试外没有订阅者；<br>• `RemoveInactiveNodesAsync`、`GetAllOnlineNodeIds` 没有调用方；<br>• `_failedTaskQueue` 只进不出，会持续增长；<br>• 没有非托管资源的类写了终结器（协调服务的终结器还会访问托管对象）；<br>• `CrawlerDbContext` 注入了 `IConfiguration` 却没有使用；<br>• 引用了 `SignalR.Client`、`SignalR.StackExchangeRedis` 包，没有用到；<br>• `NodeDetailViewModel` 注册成了 Transient，但实际是直接 `new` 出来的；<br>• Master 的 appsettings 中有只对 Worker 有效的 `CrawlerSettings`。 | **代码确认**。增加阅读和维护成本；故障任务队列会随故障次数增长。 | 删除上述内容。`SendMessageToNodeAsync` 留给 T9 下发 TaskControl 使用，**不要删**。 | 编译加现有测试。 |
| M-31 | P3 | `Views/NodeMonitorView.xaml:118`、`:54`；`Views/NodeDetailWindow.xaml:66`；`ViewModels/MainWindowViewModel.cs:55-58` | 界面显示问题：<br>• 节点列表的“最后心跳”直接显示 UTC 时间（详情窗口里是转换过的）；<br>• “内存占用 > 90 标黄”拿的是 MB 数值和 90 比较，几乎总是黄色；<br>• `MasterStatus` 在后台线程上修改，并且任何节点事件（包括离线）都会把它刷新成“最后节点心跳”。 | **代码确认**。在 UTC+8 的机器上，心跳时间显示晚 8 小时；内存一栏常亮警告色。 | ① 加上 `UtcToLocalTimeConverter`；<br>② 内存改为按使用率比较（`NodeDisplayInfo` 增加 `MemoryUsagePercent`，阈值 90）；<br>③ 用 `Dispatcher.InvokeAsync` 修改 `MasterStatus`，只在心跳事件时更新。 | 界面。 |
| M-32 | P3 | `Services/RealtimeDataSyncService.cs:191-197`、`:228-234`、`:262-276`、`:303-317` | SignalR 推送的节流是“保留第一条”：节流期内后到的事件（包括 Offline 和任务终态）都被丢弃。客户端最后看到的不是最新状态，任务结束时的节流记录也可能清不掉。 | **代码确认**。仓库里没有 SignalR 客户端，目前不影响使用；以后接入 Web 端时，会看到节点一直在线、任务一直在运行。 | 改为“每个键保留最新状态、到期发送”（trailing 节流）；Offline 和任务终态不受节流限制。 | 单元。 |
| M-33 | P3 | `Hosting/MasterHostingExtensions.cs:101-109` | gRPC 服务和 SignalR Hub 映射到了两个端口上：gRPC 在 5003 端口（HTTP/1.1）上不可用，Hub 也暴露在了 5001 端口上。 | **代码确认**。连错端口时，得到的是难以理解的错误。 | 终结点加上 `.RequireHost($"*:{port}")`，分别限定端口。 | 集成（可选）。 |
| M-34 | P3 | `Services/CrawlerCoordinatorService.cs:540-601` | `TaskProgress.error_message` 被丢弃，既不写日志，也不落库。 | **代码确认**。任务失败（例如插件不存在、页面全部超时）时，界面和 Master 日志都看不到失败原因。 | 先记一条带原因的 Warn 日志；再考虑给 `CrawlerTask` 增加 `ErrorMessage` 列，并在任务列表中显示（属于实体结构变化，要配合 M-23）。 | 单元。 |
| M-35 | P3 | `ViewModels/CreateTaskViewModel.cs:64-66`、`:72-81` | 不检查 `ScheduleTaskAsync` 的返回值，返回 false 时也关闭对话框并记“任务创建成功”；出异常时只写日志，界面上没有提示；起始 URL 只校验了非空，不校验格式；插件名是自由文本。 | **代码确认**。数据库写入失败时，用户以为任务已经创建；输错 URL 或插件名，要等任务下发到 Worker 后才失败。 | 检查返回值，在对话框内显示错误；用 `Uri.TryCreate` 检查每个 URL，并要求是 http/https；插件名改为下拉选择（候选来自配置或已知插件列表）。 | 单元：ViewModel 的校验逻辑。 |
| M-36 | P3 | `ViewModels/DataViewerViewModel.cs:300-301`、`:353`、`:357` | 导出时的问题：<br>• JSON 序列化和写文件都在 UI 线程上执行；<br>• 默认文件名直接使用任务名，没有去掉非法字符；<br>• `exportTime` 是本地时间，`completedAt` 不带时区标记。 | **代码确认**。导出上千页的任务时界面卡住；任务名里含 `/`、`:` 时，保存对话框报错。DataAnalysis 不读取这两个时间字段（`data_processor.py` 只读 `pageResults`/`parsedData`），所以修改时间格式只需同步 `docs/analysis/README.md` 中的示例。 | 用 `Task.Run` 完成序列化，用 `JsonSerializer.SerializeAsync` 写入文件流；文件名中的 `Path.GetInvalidFileNameChars()` 替换为 `_`；时间统一输出带时区的 UTC。 | 界面。 |
| M-37 | P3 | `ViewModels/DataViewerViewModel.cs:110-120`、`:125`、`:136-139`、`:165` | 每到一份新结果（来自任何任务）都会重新加载列表：`Clear()` 后重新选中，同一个任务的单页列表也会重新加载，当前选中的单页被清空；自动刷新失败时弹出模态框。 | **代码确认**。查看某个任务的单页数据时，其他任务的结果一到，列表就闪一下，选中的单页丢失，单页导出被打断；数据库出错时，每份结果都弹一次框。 | 恢复选中项时，如果 Id 没有变，就不重新加载单页列表；自动刷新失败只写日志，不弹框。 | 界面。 |
| M-38 | P3 | `Services/TaskDataService.cs:47-52`；`Services/TaskSchedulerHostedService.cs:256-265`；`Hosting/MasterHostingExtensions.cs:215` | EF/SQLite 的用法问题：<br>• `GetAllTasksAsync` 带跟踪地加载整张表，任务列表刷新和重启恢复都用它（重启恢复还是在内存里过滤）；<br>• `VerifyTableExistsAsync` 用 `await using` 释放了 DbContext 自己持有的连接；<br>• 没有启用 WAL，界面上的长查询（例如导出）会阻塞写入。 | **代码确认**。任务数达到几千后，刷新变慢；导出大任务时，心跳和结果写库被阻塞。 | 只读查询加 `AsNoTracking()`，并在数据库里筛选；不要释放 `GetDbConnection()` 返回的连接；初始化时执行 `PRAGMA journal_mode=WAL`。 | 单元。 |
| M-39 | P3 | `Services/NodeHealthCheckService.cs:86-106` | 定时器回调是 `async void`，`CreateScope()` 写在 try 外面；`System.Timers.Timer` 的回调可能重叠执行。 | **代码确认**。退出时容器已经释放，如果这时回调刚开始执行，`ObjectDisposedException` 会变成未处理异常；检查耗时超过间隔时，多次检查并行执行。 | 改成 `BackgroundService` + `PeriodicTimer` 循环，并注册为托管服务，循环体整体 try/catch；`StartMasterBackgroundServicesAsync` 中相应的手动启动和停止一并删除。 | 单元。 |
| M-40 | P3 | `Services/ILoadBalancingScheduler.cs:8`；`Services/LoadBalancingScheduler.cs:38`；`Services/NodeManagementService.cs:44-51`；`Services/TaskSchedulerHostedService.cs:214-229`；`App.xaml.cs:35`、`:94` | 其他小问题：<br>• 接口返回值是不可空的 `NodeInfo`，实现返回 `NodeInfo?`；<br>• `TotalMemoryGb == 0` 的节点被直接过滤掉，`CalculateMemoryUsageRate` 中“按 0% 处理”的分支永远走不到；<br>• 已存在的节点重新注册时，不更新 `CpuCores`/`TotalMemoryGb`；<br>• 分配失败时 `Task.Delay(1000)` 会卡住整个队列，没有节点时每秒打一条 Warn；<br>• 生产环境没有开启 DI 的 `ValidateScopes`/`ValidateOnBuild`（只有集成测试开了，界面 ViewModel 的注册没有被校验到）；<br>• 非 UI 线程的未处理异常处理器里调用了同步的 `Dispatcher.Invoke`。 | **代码确认**。节点改了容器内存限制后重新注册，负载均衡仍按旧的总内存计算；没有节点时日志刷屏；以后有人给 ViewModel 注入 Scoped 服务，没有任何检查能发现。 | 逐项修改：<br>• 接口改为 `NodeInfo?`；<br>• 内存未知的节点按 0% 处理；<br>• 注册时更新硬件信息；<br>• 重新入队不阻塞，同一条警告做节流；<br>• `App.xaml.cs` 中加 `UseDefaultServiceProvider(o => { o.ValidateScopes = true; o.ValidateOnBuild = true; })`；<br>• 未处理异常处理器只写日志，或改用 `BeginInvoke`。 | 单元。 |

## 5. 与 T1 已发现缺陷的对照

`docs/project/overview.md` §4 第 8 条列出的缺陷中，属于 Master 的部分都已复核，并编入本报告；属于 Worker 的部分留给 T3。

| T1 记录 | 归属 | 本报告 | 复核结论 |
|---|---|---|---|
| D1 Master 重启后在线的 Worker 不会重新注册 | Worker | — | Master 侧的行为正确：会话无效时返回 `PermissionDenied`。修复在 T5；M-09 保证启用拦截器后，这个状态码不会被改写 |
| D2 Worker 正常停止时，正在执行的任务不会被重新调度 | 主要在 Worker | M-02、M-11 | Master 侧相关的有两点：本地排队的任务被丢弃（M-02，已验证）；结果丢失后任务永久“已完成、无数据”（M-11） |
| D3 同一 NodeId 崩溃重启后 30 分钟内无法注册 | Master | M-03 | 确认。修法见 M-03，不依赖 D1 |
| D4 Master 不校验节点令牌 | Master | M-01（+M-09、M-21） | 确认。按“安全漏洞”定为 P0 |
| Worker 停止时抛异常 | Worker | — | 留给 T3 |
| Windows 上 CPU 使用率始终采集失败 | Worker | 相关：M-07 | Master 侧：CPU 阈值在 Windows 上不起作用；内存阈值在 Windows 上反而过严 |
| 任务下发与状态写库存在竞态（推断“排队任务不会被恢复”） | Master | M-02、M-10 | 推断已由探针 2 证实：任务会永久丢失，因此升为 P0 |
| Worker 的环境变量不生效 | Worker | 相关：M-22 | Master 有类似问题：配置和数据库位置依赖工作目录 |
| “已完成但无数据”的短暂窗口 | Master/Worker | M-11 | 扩展为结果永久丢失的情况，定为 P2 |
| 数据库连接字符串的配置不生效 | Master/Core | M-26 | 确认 |
| 每个任务的第一条引擎进度不会上报 | Worker | — | 留给 T3 |
| 节点状态事件重复发布 | Master | M-25、M-16 ① | 确认。并发的双重清理是原因之一 |

## 6. 写得好、应保持的设计

T4 修改时，以下设计应当保留：

1. **写入经过 Channel，由单一写循环写流**：Master 的每条流用 `Channel<FromMaster>`（`SingleReader`）串行写出（`CrawlerCoordinatorService.StartWriteLoopAsync`），`RealtimeDataSyncService` 也用 Channel 缓冲推送。修改流的生命周期时，保留“所有写入都经过通道”这一点。
2. **同一节点的消息在读循环中按到达顺序处理**（`StartReadLoopAsync`，不再用 `Task.Run`）。M-10/M-11 的状态机依赖这个顺序，不要再引入并发处理。
3. **单例中的 Scoped 服务一律通过 `IServiceScopeFactory` 按次创建作用域**（协调服务、调度、故障恢复、健康检查、各 ViewModel），集成测试开启了 `ValidateScopes`/`ValidateOnBuild` 来守住这一点。建议在生产启动中也开启（M-40）。
4. **单次心跳落库失败不会中断流**（`HandleHeartbeatMessage` 中的 try/catch），各条消息的处理都有自己的异常隔离。
5. **服务注册与启动步骤抽成 `MasterHostingExtensions`**，WPF 与集成测试共用一套，避免测试与生产慢慢走样。
6. **`BatchResetFailedTasksAsync` 在事务内只重置仍为 Running 的任务**：本身是幂等的，M-02 扩大范围、M-18 去掉时间窗口，都建立在这一点上。
7. **优先级队列用 `(int.MaxValue - priority, 入队序号)` 作为优先级**：同一优先级内保持先进先出，简单且正确。
8. **UI 更新统一用 `Dispatcher.InvokeAsync` 投递**（节点监控、任务管理），先进先出且不阻塞 gRPC 线程；节点监控不再因抢锁超时而丢弃事件。
9. **数据查看页对过期的加载结果做了丢弃**（`_crawlResultsLoadVersion` 和 `ReferenceEquals(SelectedCrawlResult, crawlResult)`），快速切换时不会把两个任务的数据混在一起。
10. **分页投影查询**：`GetPagedCrawlResultProjectionsAsync` 用 Join/GroupJoin 在数据库端算出任务名和页数，配合 `AsNoTracking`，不会一次加载全部页面。
11. **事件订阅保存令牌，并在释放时退订**（`IEventAggregator` 的 Guid 订阅 ID），ViewModel 和服务都遵守这个约定（M-08 是例外：它在错误的时机释放了单例）。
12. **输入防御**：心跳数值做了 `Math.Max(0, …)`，proto 状态值用 `Enum.IsDefined` 校验，注册落库失败时回滚会话（`RegisterNode` 中的 `RemoveSession`）。
13. **时间统一用 UTC 存储，显示时用转换器转为本地时间**（M-31 中有一处遗漏）。
14. **健康检查参数可以配置**（`HealthCheckSettings`），T1 借此把离线判定缩短到 5 秒做测试。M-05、M-07 新增的参数建议沿用同样的方式。

## 7. T4 处理状态

> 2026-10-22，路线图 T4。提交：`989744a`（任务状态机与故障恢复）、`4c2f22b`（双向流、会话、调度与负载均衡、协议扩展）、`7ef58c2`（认证、主机配置与数据库初始化）、`80d67c7`（界面与事件聚合器）。
> 测试名中标“集成”的在 `CrawlerCore.Tests/Integration/`，其余为单元测试。

### 7.1 状态汇总

| 状态 | 编号 |
|---|---|
| 已修复 | P0：M-01、M-02；P1：M-03 – M-09（M-04 ③ 不采用，见下）；P2：M-10 – M-18、M-20 – M-24；P3（随上面的修改一并完成）：M-25、M-29 |
| 部分修复（P3，随上面的修改顺带完成一部分） | M-27、M-30、M-34、M-38、M-39、M-40 |
| 不修复，留给 T6 | M-19 |
| 不修复（P3，只记录） | M-26、M-28、M-31、M-32、M-33、M-35、M-36、M-37 |
| Worker 侧部分留给 T5 | M-02（排队时不报 Pending，W-22 ①）、M-04 ④（W-23）、M-05（Worker keepalive，W-12）、M-06（Worker 上报并发上限，W-22 ②）、M-11（W-27、D2）、M-21（Worker 配置与 compose 中的默认令牌，W-24 ④）、W-03 的发件箱与重发 |

### 7.2 逐条状态

| 编号 | 状态 | 做法 | 验证 |
|---|---|---|---|
| M-01 | 已修复（`7ef58c2`） | `AddGrpc` 中加入 `Interceptors.Add<GrpcAuthenticationInterceptor>()`，与 M-09 一起改；令牌改为定长比较 | 集成：`令牌错误的节点注册被拒绝`（D4，去掉 Skip）、`缺少令牌的StartStreaming被拒绝`；单元：`MasterHostingTests.Grpc_RegistersAuthenticationInterceptor`、`GrpcAuthenticationInterceptorTests` |
| M-02 | 已修复（`989744a`、`4c2f22b`）；Worker 侧留给 T5 | ① 故障恢复改为 `ResetUnfinishedTasksOfNodeAsync`：在一个事务里重置分配给该节点的 Pending 与 Running；② 重启恢复改为 `ResetTasksForStartupRecoveryAsync`：在库中筛选所有 Pending，以及执行节点不在线的 Running；③ 调度先写“Running + 节点”再下发，下发失败时撤销；④ Worker 上报的 Pending 不再覆盖 Running（M-10）。Worker 侧“排队时不报 Pending”（W-22 ①）留给 T5，Master 已不受它影响 | 集成：`Worker本地排队的任务在节点崩溃后由另一节点完成`（探针 2）、`Master重启后恢复已分配但未完成的任务_并跳过没有活跃流的旧在线节点`；单元：`TaskStateMachineTests`（重置、重启恢复、Pending 不降级）、`FailedTaskRecoveryServiceTests.节点离线事件_本地排队的Pending任务也被重置`、`TaskSchedulerHostedServiceTests`（先写库再下发、下发失败撤销） |
| M-03 | 已修复（`4c2f22b`、`7ef58c2`） | `RegisterNode`：节点没有活跃流就 `ReplaceSession`（旧会话随即失效），仍有活跃流才拒绝；流断开时不删会话，Worker 用旧会话重连仍可用。`SessionManager` 注册为托管服务，过期清理跳过有活跃流的会话 | 集成：`Worker崩溃后以相同NodeId重启能重新注册上线`（D3，去掉 Skip）、`短暂断流后Worker用旧会话重连_…`；单元：`GrpcMasterRegistrationTests`（无流时替换、有流时拒绝）、`SessionAndEventTests`（替换、过期清理） |
| M-04 | ①② 已修复（`989744a`、`4c2f22b`）；③ 不采用；④ 留给 T5（W-23） | ① 进度与结果先查任务：任务已分配给其他节点、任务不存在都丢弃并记 Warn。被重置为“Pending + 未分配”的任务，原节点重连后继续上报时由它**认领**为 Running，调度器出队时发现已不是 Pending，就不再重复下发；② `CrawlResults.TaskId` 加唯一索引，写入前也检查，重复结果丢弃。③ 宽限期要求 Worker 侧 W-03（发件箱与重发）同时完成，否则断线期间完成的任务会永久停在 Running；有了①②和“认领”，已能保证只有一份结果，所以 T4 不采用 | 集成：`短暂断流后Worker用旧会话重连_重复执行的任务只保留一份结果且完成后状态不回退`（探针 1）、`被判离线的节点恢复后_其进度和结果被丢弃_任务只在幸存节点上有一份结果`（双节点版）、`被改派后旧节点发来的进度和结果被丢弃_任务只保留新节点的结果`（假节点，结果确定）；单元：`TaskStateMachineTests`、`CoordinatorStreamTests.ResultFromNodeNotAssigned_IsDiscarded` |
| M-05 | 已修复（`4c2f22b`、`7ef58c2`）；Worker 侧 keepalive 留给 T5（W-12） | ① `ConfigureMasterHttp2KeepAlive`：`GrpcSettings:KeepAlivePingDelaySeconds`/`KeepAlivePingTimeoutSeconds`（默认 15/30），生产与集成测试共用；② 同一会话的新流替换旧流并关闭旧流，不再返回 `AlreadyExists`；③ 健康检查拿到判离线的节点 ID 后调用 `DisconnectNode` 断开其流 | 集成：`失联节点被判离线后Master断开其流_网络恢复后节点重新上线`；单元：`CoordinatorStreamTests`（新流替换旧流、`DisconnectNode`）、`SessionAndEventTests`（健康检查断流）、`MasterHostingTests`（keepalive）。人工复测：禁用网卡 2 分钟后再启用 |
| M-06 | 已修复（Master 侧，`4c2f22b`）；Worker 填写上限留给 T5（W-22 ②） | `NodeRegistration` 末尾追加 `max_concurrent_tasks = 6`（0 或缺省表示不限）。调度先按“已分配且未结束的任务数”从少到多选节点，再看资源得分；任务数已满的节点不再分配，任务留在 Master 的队列里 | 集成：`一次创建4个任务时两个节点各分到2个`、`任务数已达并发上限的节点不再分配任务`（假节点上报上限）；单元：`LoadBalancingSchedulerTests`、`ProtoContractTests.NodeRegistration_MaxConcurrentTasks_IsBackwardCompatibleExtension` |
| M-07 | 已修复（`4c2f22b`、`7ef58c2`） | 新增 `LoadBalancingSettings:MaxCpuPercent`/`MaxMemoryPercent`（默认 90）；没有节点满足阈值时，退而选择负载最低的在线节点并记 Warn；总内存未知按 0% | 单元：`LoadBalancingSchedulerTests`（默认阈值接受内存 70% 的节点、阈值可配置、都不满足阈值时的退路） |
| M-08 | 已修复（`80d67c7`） | 删除 `DataViewerView` 在 `Unloaded` 时释放单例 ViewModel 的代码 | 界面复测 |
| M-09 | 已修复（`7ef58c2`） | `RpcException` 原样抛出；`OperationCanceledException`/`IOException` 记 Information 后重新抛出；其余异常只返回固定文本 `Internal server error`，详情只写日志 | 单元：`GrpcAuthenticationInterceptorTests`；集成：`令牌正确但会话无效的StartStreaming返回PermissionDenied而不是Internal` |
| M-10 | 已修复（`989744a`、`4c2f22b`） | 状态写入集中到 `TaskDataService`，用带条件的 `ExecuteUpdate`（条件就是允许的来源状态）：Pending→Running→Completed/Failed，终态不回退；改回 Pending 只能走故障恢复和重启恢复；未知任务、未生效的进度不发布事件 | 单元：`TaskStateMachineTests`；集成：`创建任务后分配并爬取_…进度不回退` |
| M-11 | 已修复（`989744a`、`4c2f22b`）；Worker 侧（W-27、D2）留给 T5 | Completed 进度只更新页数，库中保持 Running；结果在同一事务中落库并改为 Completed。**顺带完成 W-03 的 Master 侧**：`FromMaster` 末尾追加 `ResultAck result_ack = 4`（字段 `task_id`、`accepted`），结果事务提交后发送；重复结果也回执 `accepted=true`，被丢弃的结果回执 `accepted=false`，落库失败不回执。现有 Worker 不认识这条消息，每次只记一条 Warn，T5 处理 | 单元：`TaskStateMachineTests.ApplyWorkerProgress_CompletedKeepsRunningUntilResultStored`、`CoordinatorStreamTests.CompletedProgress_WaitsForResult_ThenAcks`、`ProtoContractTests.FromMaster_ResultAck_IsBackwardCompatibleExtension`；集成：`Completed进度之后结果未到时任务保持Running_结果落库后才完成并回执` |
| M-12 | 已修复（`4c2f22b`） | 结果落库抛异常时把任务标为 Failed 并发布进度事件，不发布“收到结果”事件，也不回执。之后再收到该节点的结果（T5 实现重发后），落库成功即改为 Completed | 单元：`CoordinatorStreamTests.ResultPersistenceFailure_MarksTaskFailed`（删掉 PageResults 表让写入失败）、`TaskStateMachineTests.SaveCrawlResult_FailedTaskFromAssignedNode_IsCompleted` |
| M-13 | 已修复（`80d67c7`） | 订阅者异常改用 `LogHelper.Error` 记录，其他订阅者照常执行 | 单元：`SessionAndEventTests.Publish_SubscriberFailure_DoesNotAffectOthers` |
| M-14 | 已修复（`989744a`、`4c2f22b`） | 调度器下发与撤销、故障恢复重置、重启恢复都发布 `TaskProgressUpdatedEventArgs`；任务列表“刷新”时同时更新状态 | 单元：`FailedTaskRecoveryServiceTests`（重置后发布 Pending）、`TaskSchedulerHostedServiceTests`（下发前发布 Running）；界面复测 |
| M-15 | 已修复（`80d67c7`） | 选中节点的 setter 先保存旧值，对旧节点退订 | 界面复测 |
| M-16 | 已修复（`4c2f22b`） | ① 去掉取消回调中的第二次清理，清理用 `Interlocked` 标志保证只执行一次；② 用 `TryRemove(KeyValuePair)` 只删除自己，被替换的旧流不把节点标离线；③ 链接令牌加 `Task.WhenAny`，任一循环结束就关闭整条流并返回 | 单元：`CoordinatorStreamTests.ReadLoopFault_EndsCall_AndMarksOfflineOnce`、`NewStreamReplacesOld_OldCleanupDoesNotAffectNew`；集成：各断流重连用例 |
| M-17 | 已修复（`4c2f22b`） | ① 单个任务的分配包在 try/catch 中，出异常时重新入队；② 用 `TaskAssignmentOutcome` 区分已分配、无可用节点、下发失败、不可调度，只有无可用节点和下发失败才重新入队；③ 下发前确认仍为 Pending（`TryMarkTaskDispatchedAsync` 的条件）；④ 候选节点只取有活跃流的节点，下发失败的节点在本次尝试中排除，换下一个节点 | 单元：`TaskSchedulerHostedServiceTests`（异常后重新入队、不可调度、下发失败换节点、候选节点）；集成：`Master重启后…跳过没有活跃流的旧在线节点` |
| M-18 | 已修复（`989744a`） | 去掉 30 秒窗口和“正在处理就跳过”，改为每个节点一把 `SemaphoreSlim`，同一节点的离线事件依次处理；重置本身是幂等的 | 单元：`FailedTaskRecoveryServiceTests.节点30秒内第二次离线_新分配的任务仍会被重置并入队`、`节点离线事件_重复事件幂等_不重复入队`、`并发触发同一节点离线事件_仅处理一次` |
| M-19 | 不修复，留给 T6 | 分批回传要改协议，应与 T6 的“一次响应包含多篇论文”一起设计（W-28）。M-16 ③ 修复后，超限不会再让流挂起：流结束 → 节点离线 → 任务重置重做。但仍会无限重做，重试上限也放到 T6 一并设计 | — |
| M-20 | 已修复（`7ef58c2`） | SignalR 监听地址改为 `SignalRSettings:ListenAddress`（默认 `127.0.0.1`）；CORS 改为 `SignalRSettings:AllowedOrigins` 白名单（默认为空，不允许携带凭据）；gRPC 与 SignalR 的详细错误只在 Development 环境开启 | 单元：`MasterHostingTests`（监听地址解析、CORS、详细错误） |
| M-21 | 已修复（Master 侧，`7ef58c2`）；Worker 侧留给 T5（W-24 ④） | Master `appsettings.json` 中的令牌改为空；令牌为空或等于公开的 `test-token` 时，启动记 Error、界面弹窗提示，并拒绝所有节点。令牌通过环境变量 `GrpcSettings__NodeAuthToken` 提供，见 `docs/project/deployment.md` | 单元：`GrpcAuthenticationInterceptorTests.UnusableConfiguredToken_RejectsEveryone` |
| M-22 | 已修复（`7ef58c2`） | `UseContentRoot(AppContext.BaseDirectory)`；相对路径的 Data Source 解析到程序所在目录；`.gitignore` 忽略 `*.db`、`*.db-shm`、`*.db-wal`、`*.db-journal`、`*.db.bak-*`。从 VS 启动的用户，数据库位置不变 | 单元：`MasterHostingTests`（连接字符串解析、容器中 DbContext 的数据库路径）；界面复测 |
| M-23 | 已修复（`7ef58c2`） | 按 EF 模型逐表逐列检查（`PRAGMA table_info`），不检查索引。结构不符或无法读取时：库中有数据，先备份为 `crawler.db.bak-yyyyMMddTHHmmssZ`（UTC；能读取时用 SQLite 在线备份，读不了时直接复制文件）再重建；没有数据则直接重建；界面弹窗提示备份位置。不再释放 DbContext 自己持有的连接 | 单元：`MasterDatabaseInitializationTests`（7 种情况，只用临时文件） |
| M-24 | 已修复（`7ef58c2`） | 主窗口第一次关闭时取消关闭，异步停止主机、`DisposeAsync` 容器后再 `Shutdown()`；`OnExit` 改为同步方法，其他途径退出时在线程池上限时停止主机；异常写进日志 | 界面复测 |
| M-25 | 已修复（P3，随 M-16，`4c2f22b`） | 协调服务不再发布节点状态事件；`NodeManagementService` 只在库中状态真正变化时、保存之后、释放写锁之后发布；心跳事件改到保存之后 | 单元：`CoordinatorStreamTests`（离线事件只发布一次） |
| M-26 | 不修复（P3） | — | — |
| M-27 | 部分修复（P3，`989744a`） | 列出的调用处都在 `FailedTaskRecoveryService`，随 M-18 重写已改用插值字符串；`LogHelper` 的重载歧义本身没有改 | — |
| M-28 | 不修复（P3） | — | — |
| M-29 | 已修复（P3，随 M-03、M-20，`4c2f22b`、`7ef58c2`） | Master 日志中不再写会话 ID；`EnableDetailedErrors` 只在 Development 环境开启 | 单元：`MasterHostingTests.DetailedErrors_OnlyInDevelopment` |
| M-30 | 部分修复（P3，`989744a`） | `FailedTaskRecoveryService` 的终结器与过期清理定时器随 M-18 删除；其余死代码（`GrpcHostedService` 等）未删 | — |
| M-31 – M-33 | 不修复（P3） | — | — |
| M-34 | 部分修复（P3，`4c2f22b`） | Worker 上报 Failed 时把 `error_message` 写进 Warn 日志；没有加 `ErrorMessage` 列 | — |
| M-35 – M-37 | 不修复（P3） | — | — |
| M-38 | 部分修复（P3，`989744a`、`7ef58c2`） | `GetTaskByIdAsync`、`GetAllTasksAsync` 改为 `AsNoTracking`；重启恢复在库中筛选；建库校验不再释放 DbContext 的连接（随 M-23）；没有开启 WAL | — |
| M-39 | 部分修复（P3，`4c2f22b`） | 定时器回调中作用域的创建移到 try 里面，并防止两次检查重叠执行；没有改为 `BackgroundService` | — |
| M-40 | 部分修复（P3，`4c2f22b`） | 接口返回值改为 `NodeInfo?`；总内存未知按 0%；重新注册时更新 CPU 核数与总内存。重新入队不阻塞、生产环境开启 `ValidateScopes`、`Dispatcher.Invoke` 没有改 | 单元：`LoadBalancingSchedulerTests.SelectOptimalNode_UnknownTotalMemory_TreatedAsZeroPercent` |

### 7.3 数据库结构变化

- `CrawlResults.TaskId` 新增唯一索引。`EnsureCreated` 不会给已有的库补建索引，而 M-23 的结构检查只看表和列，所以**旧的 `crawler.db` 可以继续使用**：重复结果仍由写入前的检查拦截，只是少了数据库层的兜底。如果想要这个索引，先自行备份，再删除 `crawler.db` 让程序重建。
- 没有新增或删除列，旧库不会触发自动备份和重建。
