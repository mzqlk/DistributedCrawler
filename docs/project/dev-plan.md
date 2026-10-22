# 16 周开发计划（精简版 + 完成状态）

> 来源：`D:\毕设相关\开发计划（16周）.docx`（80 个工作日，5 个阶段）。原文按工作日给出了很细的步骤，这里只保留每个子阶段的目标、关键设计决策和验收标准；已完成部分的细节以代码和 git 历史为准。状态核对日期 2026-10-22。

## 里程碑

| 里程碑 | 计划时间 | 验收标准 | 状态 |
|---|---|---|---|
| M1 基础组件就绪 | 第 2 周末（工作日 10） | 骨架可编译，日志/数据库正常 | ✅ |
| M2 分布式核心就绪 | 第 6 周末（工作日 30） | 1 Master + 3 Worker 通信正常，任务可重分配 | ✅ |
| M3 UI 与爬虫闭环 | 第 10 周末（工作日 50） | 创建任务→执行→查看→导出无 bug | ✅（有偶发绑定问题，见 overview §4） |
| M4 扩展功能 | 第 13 周末（工作日 65） | 流程设计器可编译流程，插件可加载执行 | ⚠️ 插件可加载执行；设计器未做 |
| M5 项目交付 | 第 16 周末（工作日 80） | 部署文档完整，稳定运行 ≥24h | ⚠️ 部分 |

## 阶段一：项目初始化与基础组件（第 1–2 周，工作日 1–10）✅

- 4 个项目（Master/WPF、Worker/Console、Core、Plugins），只允许 Core 被引用，无循环依赖。
- Core：实体、核心接口、Common 工具类（URL/日期/字符串/集合）+ 单测。
- Master 集成 EF Core + SQLite；用 `EnsureCreated()` 替代迁移（规避 SQLite 兼容性）。
- Serilog（控制台 + 文件按天分割），`LogHelper` 静态封装。

## 阶段二：分布式通信与核心服务（第 3–6 周，工作日 11–30）✅

### 2.1 gRPC 协议与通信基础（工作日 11–15）
硬约束：
1. proto 定稿后不改字段；流程固定为 注册 → 建流。
2. 本子阶段只做通信层，不写健康检查/调度等业务。
3. 配置全部来自 appsettings.json + 环境变量；时间戳统一 UTC。
4. Master 是 WPF，只发布 Windows exe，不容器化；Worker 支持 Docker。

要点：WPF 用通用主机托管 gRPC（避免 STA 死锁）；`GrpcAuthenticationInterceptor` 校验 Token + 结构化日志；会话用 `ConcurrentDictionary`；Worker 封装 `GrpcClientManager`（通道复用、懒加载）；重试 3 次、间隔 1s；`Channel<T>` 保证流写入串行；NodeId 默认机器名，容器可用 `MASTER_NODE_ID` 固定。
验收：10 Worker 并发注册 ≤100ms（局域网），1M+3W 连续 30 分钟心跳零丢失，Master CPU ≤20%、Worker ≤5%，单测覆盖率 ≥80%。

### 2.2 节点管理与资源监控（工作日 16–20）
LastHeartbeat 更新、定时健康检查、`NodeStatusChangedEvent`、资源采集、10 个 Docker Worker 并发注册测试。

### 2.3 任务调度与故障重分配（工作日 21–25）
- `CrawlerTask.Priority`（1–5，越大越优先）+ `PriorityQueue<CrawlerTask,int>`。
- `LoadBalancingScheduler`：过滤 Online / CPU<50% / 内存<60%，评分 CPU×0.4 + 内存×0.3 + 网络 20(预留) + 契合度 10(预留)。
- `SendTaskToNodeAsync` 经会话中的流写 `FromMaster.NewTask`；成功后任务置 Running、写 AssignedNode。
- `FailedTaskRecoveryService`：Offline 事件 → 事务重置 Running→Pending → 重新入队；30s 内同节点重复事件跳过。
- Master 启动时恢复 Pending 且未分配的任务（按创建时间）。
- 验收：节点离线到任务重新执行 ≤30s；Master 重启不丢任务。

### 2.4 SignalR 实时同步（工作日 26–30）
`SignalRSettings`（端口 5003、`/crawlerHub`、节点 5s / 进度 2s 节流）；WPF 端 `HubConnection.WithAutomaticReconnect` + Dispatcher 更新；`RealtimeDataSyncService` 用 Channel 缓冲 + 节流；Worker 端 `TaskProgressSimulator`（`#if DEBUG` 且 `EnableProgressSimulation=true` 才生效）。
计划的集成测试报告路径 `docs/phase2_integration_test_report.md`（未产出）。

## 阶段三：爬虫引擎与可视化 UI（第 7–10 周，工作日 31–50）✅

### 3.1 Worker 爬虫引擎（工作日 31–35）
引擎协议无关、插件驱动；`PluginExecutionContext` 带任务配置、深度、父 URL；链接提取是引擎内置能力（不依赖插件）；BFS 队列 + HashSet 去重（10 万上限告警，后续可换布隆过滤器/数据库去重）；`SemaphoreSlim` 控制并发；进度节流上报。
风险对策：反爬 → 随机 UA（配置读取）+ 可配置延迟。

### 3.2 WPF 主框架与节点监控（工作日 36–40）
MaterialDesign + OxyPlot；纯 MVVM；DataGrid 虚拟化；状态配色与超限高亮；节点详情窗口；相对时间“xx 秒前”；Dispose 中注销事件订阅。

### 3.3 任务管理 UI（工作日 41–45）
任务列表 + 进度、创建任务弹窗（任务名、StartUrls、MaxDepth、PluginName，URL 校验）、按状态筛选。

### 3.4 数据查看与导出（工作日 46–50）
分页（每页 20）+ 关键词搜索、虚拟滚动、详情弹窗、导出（计划写 JSON/CSV，实际实现 JSON）。

## 阶段四：流程设计器与插件系统（第 11–13 周，工作日 51–65）⚠️

| 子阶段 | 内容 | 状态 |
|---|---|---|
| 4.1 设计器基础 | `WorkflowDesigner : Canvas`、20px 灰色虚线网格、`DesignElement`/`StartUrlElement`、拖拽（用 `GetPosition(this)` 防坐标偏移）、贝塞尔连线、删除/复制 | ❌ |
| 4.2 编译与执行 | `ParseListElement`(CssSelector)、ExtractData/Export 元素、`WorkflowCompiler`（必须有起始节点、无环、拓扑排序）、生成任务、XML 保存/加载、属性面板 | ❌ |
| 4.3 插件系统 | `ICrawlerPlugin.ValidateConfig`、插件加载（✅ Worker 端已有）、插件管理 UI、NewsPlugin 示例、隔离加载（计划写 AppDomain，.NET Core 下应改用 `AssemblyLoadContext`） | ⚠️ |

## 阶段五：测试、优化与部署（第 14–16 周，工作日 66–80）⚠️

| 子阶段 | 内容 | 状态 |
|---|---|---|
| 5.1 系统测试 | 单测 ≥50 个且覆盖率 ≥80%；≥8 个集成场景；10 并发任务压测 30 分钟（CPU ≤80%、内存 ≤1GB）；1000 条数据导出完整性 | ⚠️ 单测 104 个通过；压测报告无 |
| 5.2 性能优化 | `CrawlResult.Url`/`CrawlerTask.TaskId` 索引、`AsNoTracking`、`MemoryManager`（1GB 阈值）、`MaxConcurrentRequests=10`、图表降采样（1000→100 点） | ⚠️ 已有 AssignedNode/Status 索引与 AsNoTracking；其余未做 |
| 5.3 部署与文档 | Master/Worker Dockerfile、compose（1M+3W）、用户指南、开发指南（插件开发步骤）、API 文档（gRPC/SignalR）、项目总结 | ⚠️ 仅 Worker Dockerfile + compose + README |

## 最终交付物清单（计划）
源码 4 模块；Master.exe / Worker.exe；Dockerfile.master、Dockerfile.worker、docker-compose.yml；crawler.db（含测试数据）；系统测试报告、用户指南、开发指南（含插件示例）、API 文档、项目总结报告。
