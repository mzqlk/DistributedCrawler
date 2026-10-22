# 项目概览与现状

> 整理自 `D:\毕设相关` 下的项目文档、开发计划、毕业论文、任务书/开题报告，并对照仓库代码（截至 2026-10-22，`c431d8d`）核对过。文档与代码冲突时**以代码为准**，差异列在文末。

## 1. 项目定位

本仓库是毕业设计《面向学术文献元数据采集与主题可视化分析系统研究与实现》（2026 届本科毕业设计，已于 2026-05 答辩）中的**分布式爬虫管理模块**，项目内部名称为“分布式爬虫管理与数据分析工作站”。

整个系统由两个低耦合模块组成，通过 **JSON 文件**对接：

| 模块 | 技术栈 | 代码位置 | 说明 |
|---|---|---|---|
| 分布式爬虫管理模块 | .NET 10 / C#、WPF+MVVM、gRPC 双向流、SignalR、SQLite+EF Core、Serilog、Docker | **本仓库** | Master(WPF) + Worker(Console) + 插件 |
| 数据分析模块 | Python、Streamlit、NLTK、Gensim(LDA)、pyLDAvis、Plotly、WordCloud | [`DataAnalysis/`](../../DataAnalysis/)（2026-10 从 `代码.docx` 还原入库，独立于 .NET 解决方案） | 读取 Master 导出的 JSON，做预处理 → LDA → 可视化 → CSV 导出 |

数据流：`Worker(ArXivThesisPlugin) 爬 arXiv /abs/ 页 → Master SQLite → 数据查看页“导出 JSON” → 任务数据_*.json → Streamlit 上传 → LDA 主题建模 → CSV`。
JSON 格式见 [`docs/analysis/README.md`](../analysis/README.md#输入数据契约crawler-导出-json)。

## 2. 解决方案结构（实际仓库）

| 项目 | 类型 | 职责 |
|---|---|---|
| `CrawlerCore` | 类库 | 实体（`CrawlerTask`/`CrawlResult`/`PageResult`/`NodeInfo`…）、接口（`ICrawlerPlugin`、`ITaskDataService`…）、事件聚合器、配置类、`Protos/crawler.proto`、通用工具 |
| `CrawlerMaster.WPF` | WPF 桌面应用 | 通用主机同时托管 gRPC(5001) 与 SignalR(5003)；节点管理、健康检查、优先级调度、负载均衡、故障任务恢复、实时推送、三大视图（节点监控/任务管理/数据查看） |
| `CrawlerWorker` | Console + BackgroundService | 注册 → 双向流 → 心跳；`CrawlerEngine`（BFS 深度爬取）；`PluginManager`（反射加载 `Plugins/*.dll`）；`NodeInfoCollector`（cgroup v2 / Windows API 资源采集）；Dockerfile |
| `ThesisPlugin` | 插件 | `ArXivThesisPlugin` v1.3.0，解析 arXiv `/abs/` 页面，`CrawlDelay=15000ms` |
| `MockPlugin` | 插件 | 测试用插件 |
| `CrawlerCore.Tests` | xUnit | 104 个单元测试（论文中报告全部通过，见 `docs/thesis/figures/fig6-2.png`）；`Integration/` 下是 Master–Worker 端到端集成测试（2026-10-22 新增，运行方式见 CLAUDE.md） |

插件编译后由 `CrawlerWorker.csproj` 的构建目标自动复制到 Worker 输出目录 `Plugins/`。

## 3. 完成度（对照 16 周开发计划与任务书）

| 范围 | 状态 | 依据 |
|---|---|---|
| 阶段一：骨架、实体、EF Core、Serilog | ✅ 完成 | commit `dbb44d3` |
| 阶段二：gRPC 注册/双向流/心跳、节点管理、优先级调度、负载均衡、故障重分配、SignalR | ✅ 完成 | `163a3ec` … `4384115` |
| 阶段三：爬虫引擎、WPF 主框架、节点监控、任务管理、数据查看与导出 | ✅ 完成 | `cbf6f46` … `cd16e89` |
| 论文爬虫插件（arXiv） | ✅ 完成（MVP） | `a9f9da5` |
| 阶段四.1-4.2：**可视化流程设计器**（拖拽/连线/编译/拓扑排序） | ❌ 未实现 | 仓库无 Workflow/Designer 代码 |
| 阶段四.3：插件系统 | ⚠️ 部分 | Worker 端加载已实现；Master 端插件管理 UI、`ValidateConfig`、NewsPlugin、AppDomain 隔离未做 |
| 阶段五：测试/优化/部署 | ⚠️ 部分 | Worker Dockerfile + compose、端到端集成测试已有；Master Dockerfile、MemoryManager、压测报告、用户/开发指南未做 |
| 端到端集成测试（路线图 T1） | ✅ 2026-10-22 | `CrawlerCore.Tests/Integration/`：进程内 Master 后台服务 + 真实 Worker + Kestrel 静态站点 + 临时 SQLite。覆盖注册与心跳、任务分配→爬取→落库且进度不回退、Worker 崩溃与失联后的故障转移、Master 重启后恢复 Pending 任务；另有 4 个用 Skip 标记的已知缺陷测试（见 §4 第 8 条） |
| Master 代码审阅（路线图 T2） | ✅ 2026-10-22（只出报告） | [`docs/review/master.md`](../review/master.md)：40 个问题，其中 P0 2 个、P1 7 个、P2 15 个、P3 16 个，编号 M-01 – M-40，由 T4 修复；见 §4 第 9 条 |
| 任务书：CNKI 等多源插件 | ❌ 未实现 | 只有 arXiv |
| 任务书：作者合作网络、主题层次树、文档降维散点图 | ❌ 未实现 | 分析模块只做了 LDA；合作网络可参考 `docs/analysis/reference-flask-app/` |
| 采集与分析端到端自动联动 | ❌ 文件级对接 | 论文第 7 章列为局限 |
| 数据分析模块入库 | ✅ 2026-10-22 | 从 `代码.docx` 还原到 `DataAnalysis/`，已用真实导出数据跑通 |

## 4. 已知问题与待办

来自提交信息、论文“结束语”、评审意见：

1. **论文 PDF 导出**未实现（`a9f9da5` 提出，或许应放在数据分析模块）。
2. ~~**视图数据偶发不同步、数据绑定失败**（`a9f9da5`）~~ 2026-10-22 已按代码分析修复，待在界面上复测确认。找到的原因：
   - Master 对每条任务进度消息 `Task.Run` 并发处理，较早的 Running 可能晚于 Completed 落库/推送 → 任务状态回退。改为在读循环中按序处理。
   - 单例服务/ViewModel（`CrawlerCoordinatorService`、`NodeMonitorViewModel`、`DataViewerViewModel`、`TaskSchedulerHostedService`）直接注入 Scoped 服务，共用根作用域的同一个 `DbContext` 并被多线程并发访问 → 偶发 “A second operation was started on this context” 异常；心跳路径抛出时还会中断双向流。改为每次访问创建作用域。
   - 节点监控 500ms 抢不到锁就丢弃事件，“节点离线”可能丢失；`InitializeAsync` 从未被调用。
   - CommunityToolkit 命令的 `CanExecute` 从不重新评估（刷新、上一页/下一页按钮可能一直禁用，取决于加载与绑定的时序）。
   - 数据查看：快速切换任务时两个任务的单页结果会混在一起；自动刷新会清空当前选中；分页在总数整除页大小时多出空页。
   - 任务管理：刷新列表时用 `MaxPages` 覆盖进度分母，进度跳回 0%；从数据库加载的已完成任务显示 0%。
3. 主题建模仅 LDA，细粒度语义能力有限 → 后续可引入 BERTopic / 预训练模型（评审意见、答辩 PPT 展望）。
4. 无反爬机制（代理池、验证码、随机 UA 池），数据源覆盖有限（评审意见）。
5. 两模块仅文件级对接，未形成实时数据链路。
6. 展望方向（答辩 PPT）：更多数据源插件（IEEE、Springer）、作者合作网络与机构影响力分析、热点趋势预测与个性化推荐、性能优化。
7. 负载均衡中的“网络得分/专业化得分”目前是常量（20/10），论文描述的“按网络延迟/同类任务经验计算”未实现。
8. **端到端集成测试发现的缺陷**（2026-10-22，T1）。D1–D4 已用 `CrawlerCore.Tests/Integration/MasterWorkerKnownDefectTests.cs` 中的测试复现，测试暂时 Skip，修复后去掉 Skip 即为回归测试；括号内是建议的严重度，以 T2/T3 审阅报告为准。Master 部分已经由 T2 复核并编号，对照见 [`docs/review/master.md` §5](../review/master.md#5-与-t1-已发现缺陷的对照)：D3 → M-03，D4 → M-01（定为 P0），写库竞态 → M-02（证实会丢任务，定为 P0），重复事件 → M-25，连接字符串 → M-26，“已完成但无数据” → M-11。
   - **D1 Master 重启后，在线的 Worker 不会重新注册**（P1）。`WorkerHostedService.StartStreamingAsync` 在创建流调用后（服务端尚未响应）就把 `_reconnectAttempts` 清零，计数永远到不了 `MaxRetryAttempts`，Worker 一直用失效会话重连：测试中 20 秒内被拒约 100 次，始终不会重新注册。
   - **D2 Worker 正常停止（Ctrl+C、`docker stop`）时，正在执行的任务不会被重新调度**（P1）。停止时任务被取消，但引擎把被取消的请求记成 499 页面后正常结束，于是上报 Completed（取消发生在其他位置时上报 Failed）；故障恢复只处理 Running 任务。另外，`CleanupStreamResourcesAsync` 先取消流再等发送队列清空，结果消息会丢：测试中 Master 显示“已完成”，但没有任何数据。
   - **D3 同一 NodeId 的 Worker 崩溃后重启，30 分钟内无法注册**（P1）。流断开时 Master 不移除 `SessionManager` 里的会话，`RegisterNode` 返回 “Node already has an active session”，Worker 随即退出。此外 `SessionManager` 是 `BackgroundService`，却只按单例注册，过期清理循环从不运行。手动重开 Worker，或容器崩溃后被 `restart_policy` 拉起（NodeId 不变）时都会遇到。
   - **D4 Master 不校验节点令牌**（P1，安全）。`GrpcAuthenticationInterceptor` 只注册到了容器，没有加入 `AddGrpc` 的拦截器列表（只有未使用的 `GrpcHostedService` 用到它），令牌错误也能注册、建流。
   - **Worker 停止时抛异常**（P2）。`CleanupStreamResourcesAsync` 用 `WaitAsync(TimeSpan)` 等待发送队列清空，超时抛出的是 `TimeoutException`，代码只捕获了 `OperationCanceledException`。连接已断开时，`StopAsync` 报错，`Dispose` 里的 `.Wait()` 抛出 `AggregateException`。
   - **Windows 上 CPU 使用率始终采集失败**（P2）。`NodeInfoCollector` 中 `SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION` 声明了 `Pack = 4`，结构体只有 44 字节（应为 48），缓冲区偏小，`NtQuerySystemInformation` 返回 `STATUS_INFO_LENGTH_MISMATCH`（-1073741820，日志中每次心跳都有警告），CPU 使用率恒为 0，负载均衡的 CPU 阈值在 Windows 上不起作用。集成测试用固定负载值绕开了这一点。
   - **任务下发与状态写库存在竞态**（P2）。`AssignTaskToOptimalNodeAsync` 先下发任务、再写 AssignedNode 和 Running；Worker 收到任务放入本地队列时会上报 Pending，日志可见它与调度器的写库交错，库中状态可能出现 Running → Pending → Running。由此推断（待验证）：在 Worker 本地排队（`MaxConcurrentTasks` 已满）的任务，库中是“Pending 且已分配节点”，节点离线时故障恢复（只查 Running）和 Master 重启恢复（只查未分配）都不会处理；极端情况下调度器写入的 Running 晚于 Completed 落库，状态回退。
   - **Worker 的 `MASTER_ENDPOINT`、`NODE_AUTH_TOKEN`、`WORKER_ENDPOINT` 环境变量不生效**（P2）。`Program.cs` 把它们映射到主机配置，但 `Host.CreateDefaultBuilder` 随后加载的 `appsettings.json` 优先级更高，会覆盖它们（2026-10-22 本机实测：设置了 `MASTER_ENDPOINT`，Worker 仍去连 `host.docker.internal`）。docker-compose 里的值碰巧与 `appsettings.json` 相同，所以没有暴露。标准写法 `GrpcSettings__MasterEndpoint` 可以生效。
   - **“已完成但无数据”的短暂窗口**（P3）。Worker 先发 Completed 进度、后发 `CrawlResult`，两条消息之间界面会看到任务已完成但没有数据。
   - **数据库连接字符串的配置不生效**（P3）。代码读 `Database:ConnectionString`（`DatabaseSettings.SectionName = "Database"`），`appsettings.json` 写的是 `ConnectionStrings:ConnectionString`；目前碰巧与默认值相同。
   - **每个任务的第一条引擎进度不会上报**（P3）。进度节流的 `AddOrUpdate` 走新增分支时没有设置 `shouldReport`。
   - **节点状态事件重复发布**（P3）。注册时 Online 事件发布 3 次，断流时 Offline 事件发布 2–3 次（`RegisterNode`、`RegisterNodeAsync`、`StartStreaming`，以及两次 `CleanupNodeStreamAsync` 调用）。
9. **Master 代码审阅发现的问题**（2026-10-22，T2）。完整清单、触发场景和修复建议见 [`docs/review/master.md`](../review/master.md)，由 T4 修复。P0/P1 如下：
   - **M-01 节点令牌完全不校验**（P0，即 D4），启用拦截器前要先完成 M-09（拦截器把所有异常都转成 Internal）。
   - **M-02 在 Worker 本地排队的任务，节点离线后永久丢失**（P0，临时探针已复现）。库中这类任务是“Pending + 已分配节点”，故障恢复和重启恢复都不会处理。
   - **M-03 同一 NodeId 崩溃重启后无法注册**（P1，即 D3）。
   - **M-04 节点被判离线后，旧节点不会被隔离**（P1，已复现）。短暂断流后，同一任务执行两次，库中有两份结果。
   - **M-05 Master 发现不了半开连接**（P1，待验证）。失联节点恢复后，可能永久无法重新上线。
   - **M-06 一次创建多个任务时，全部分给同一节点**（P1，已复现）。负载均衡不看节点上已分配的任务数。
   - **M-07 内存阈值写死为 60%**（P1）。Windows 上 Worker 上报的是整机内存，常见开发机上节点永远不合格，任务永远不下发。
   - **M-08 切换过页面后，数据查看页不再自动刷新**（P1）。视图卸载时把单例 ViewModel Dispose 掉了。

## 5. 文档与代码的差异（以代码为准）

| 项 | 文档说法 | 代码实际 |
|---|---|---|
| 健康检查 | 开发计划：每 10s 检查、30s 超时；论文：每 30s 检查、2 分钟超时 | `NodeHealthCheckService`：每 **15s** 检查，**1 分钟**无心跳判离线（2026-10-22 起可经 `HealthCheckSettings` 配置，默认值不变） |
| 负载均衡 | 论文式 5-1：CPU×0.4 + 内存×0.3 + 网络×0.2 + 专业化×0.1 | `LoadBalancingScheduler`：先过滤 Online 且 CPU<50%、内存使用率<60%；得分 = (100−CPU)×0.4 + (100−内存%)×0.3 + **20 + 10（常量）** |
| 重连 | 论文 4.1.3.2 写“指数退避” | `WorkerHostedService.HandleStreamReconnectAsync`：**固定间隔** `RetryDelayMilliseconds`，超过 `MaxRetryAttempts` 次后全量重新注册（论文 5.1.2 的描述是对的） |
| 任务队列 | 论文写 `ConcurrentPriorityQueue` | 开发计划/代码：`PriorityQueue<CrawlerTask,int>`（数值越大优先级越高，1–5） |
| Worker 并发任务数 | `CrawlerSettings.MaxConcurrentTasks` 默认 5 | Worker `appsettings.json` 配置为 **2** |
| 项目文档 | “拖拽式流程设计器”“CSV/JSON 导出” | 设计器未实现；数据查看页导出 JSON（CSV 在分析模块里导出）。README 已于 2026-10-22 改为与代码一致 |
| 项目文档 3.2 | Worker 有 gRPC 服务端接收 Master 指令 | 实际 Master→Worker 指令走双向流；`WorkerControlService`(5002) 仅是预留底座 |
| 项目文档 4.x | `WorkflowDesigner`、`MemoryManager`、`OptimizedDataService` 代码 | 均为设计稿，仓库中不存在 |
