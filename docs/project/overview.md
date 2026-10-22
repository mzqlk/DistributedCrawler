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
| `CrawlerCore.Tests` | xUnit | 论文中报告 104 个单测全部通过（见 `docs/thesis/figures/fig6-2.png`） |

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
| 阶段五：测试/优化/部署 | ⚠️ 部分 | Worker Dockerfile + compose 已有；Master Dockerfile、MemoryManager、压测报告、用户/开发指南未做 |
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

## 5. 文档与代码的差异（以代码为准）

| 项 | 文档说法 | 代码实际 |
|---|---|---|
| 健康检查 | 开发计划：每 10s 检查、30s 超时；论文：每 30s 检查、2 分钟超时 | `NodeHealthCheckService`：每 **15s** 检查，**1 分钟**无心跳判离线 |
| 负载均衡 | 论文式 5-1：CPU×0.4 + 内存×0.3 + 网络×0.2 + 专业化×0.1 | `LoadBalancingScheduler`：先过滤 Online 且 CPU<50%、内存使用率<60%；得分 = (100−CPU)×0.4 + (100−内存%)×0.3 + **20 + 10（常量）** |
| 重连 | 论文 4.1.3.2 写“指数退避” | `WorkerHostedService.HandleStreamReconnectAsync`：**固定间隔** `RetryDelayMilliseconds`，超过 `MaxRetryAttempts` 次后全量重新注册（论文 5.1.2 的描述是对的） |
| 任务队列 | 论文写 `ConcurrentPriorityQueue` | 开发计划/代码：`PriorityQueue<CrawlerTask,int>`（数值越大优先级越高，1–5） |
| Worker 并发任务数 | `CrawlerSettings.MaxConcurrentTasks` 默认 5 | Worker `appsettings.json` 配置为 **2** |
| 项目文档 | “拖拽式流程设计器”“CSV/JSON 导出” | 设计器未实现；数据查看页导出 JSON（CSV 在分析模块里导出）。README 已于 2026-10-22 改为与代码一致 |
| 项目文档 3.2 | Worker 有 gRPC 服务端接收 Master 指令 | 实际 Master→Worker 指令走双向流；`WorkerControlService`(5002) 仅是预留底座 |
| 项目文档 4.x | `WorkflowDesigner`、`MemoryManager`、`OptimizedDataService` 代码 | 均为设计稿，仓库中不存在 |
