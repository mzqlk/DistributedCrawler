# CLAUDE.md

分布式爬虫管理与数据分析工作站：.NET 10 WPF Master + Console Worker（gRPC 双向流）+ 插件，外加 Python 数据分析模块。源自一篇已答辩的毕业设计，现阶段目标是把项目做完整：补齐原计划未实现的功能、审阅并修复代码。

**开始任何工作前先读 [`docs/README.md`](docs/README.md)**，其中：
- `docs/project/overview.md`：完成度、已知问题、文档与代码的差异。
- `docs/project/design.md`：设计约定。
- `docs/thesis/proposal.md`：原计划中尚未实现的功能清单。
- `docs/roadmap.md`：当前任务的顺序、状态、执行规则和决策记录。

文档与代码冲突时以代码为准。

## 结构

| 目录 | 说明 |
|---|---|
| `CrawlerCore/` | 实体、接口、事件聚合器、配置类、`Protos/crawler.proto`（Master/Worker 共用） |
| `CrawlerMaster.WPF/` | WPF + 通用主机，gRPC :5001，SignalR :5003，SQLite `crawler.db` |
| `CrawlerWorker/` | Worker（BackgroundService），插件从输出目录 `Plugins/*.dll` 反射加载 |
| `ThesisPlugin/`、`MockPlugin/` | 插件；构建后由 `CrawlerWorker.csproj` 自动复制到 `Plugins/` |
| `CrawlerCore.Tests/` | xUnit + Moq，覆盖 Core、Master、Worker（目标框架 `net10.0-windows`） |
| `DataAnalysis/` | Python（Streamlit + NLTK + Gensim LDA），读取 Master 导出的 JSON |

## 构建与测试

先运行 `dotnet --list-sdks`：本机曾经没有 .NET 10 SDK。如果缺少 10.x，可以请用户安装，或者用 `dotnet-install.ps1 -Channel 10.0 -InstallDir <临时目录> -NoPath` 装到临时目录，再把 `DOTNET_ROOT` 指向该目录后使用。

```bash
dotnet build DistributedCrawler.slnx
dotnet test CrawlerCore.Tests/CrawlerCore.Tests.csproj    # 全部：303 个单元测试 + 24 个集成测试（没有 Skip），约 50 秒
dotnet test CrawlerCore.Tests/CrawlerCore.Tests.csproj --filter "Category=Integration"    # 只跑集成测试，约 30 秒
dotnet test CrawlerCore.Tests/CrawlerCore.Tests.csproj --filter "Category!=Integration"   # 排除集成测试，约 5 秒
dotnet run --project CrawlerMaster.WPF                     # 仅 Windows
dotnet run --project CrawlerWorker                         # 默认连接 http://localhost:5001；Master 在别处时设置 MASTER_ENDPOINT
```

本机运行 Master 与 Worker 前，要给两边设置同一个节点令牌（两边的 appsettings 中令牌都为空；Master 拒绝空令牌和公开的默认值 `test-token`，Worker 令牌为空时直接以退出码 1 退出），例如在 PowerShell 中：
`$env:GrpcSettings__NodeAuthToken = "<随机字符串>"`，再在同一个终端里 `dotnet run`（Worker 也可以用 `NODE_AUTH_TOKEN`）。Docker 用仓库根目录的 `.env`（已被忽略）提供 `NODE_AUTH_TOKEN`。详见 `docs/project/deployment.md`。

运行测试时给命令设置超时（例如 Bash 工具的 timeout），集成测试出问题时可能挂起；排查时先用 `--filter "FullyQualifiedName~类名"` 只跑相关的几个。

端到端集成测试在 `CrawlerCore.Tests/Integration/`，用 `[Trait("Category", "Integration")]` 标记：
- 进程内启动 Master 的全部后台服务（不开 WPF 界面）和 1–2 个真实 Worker，Worker 用 MockTestPlugin 爬取本地 Kestrel 静态网站（`Integration/TestSite/`），每个测试一个临时 SQLite 文件。只监听 127.0.0.1 的随机端口，不需要先关掉本机正在运行的 Master，也不会触发防火墙弹窗。
- Master、Worker 与生产代码共用服务注册（`CrawlerMaster.WPF/Hosting/MasterHostingExtensions.cs`、`CrawlerWorker/Hosting/WorkerServiceCollectionExtensions.cs`）。新增或修改后台服务注册时改这两个文件，不要直接写进 `App.xaml.cs` 或 `Program.cs`（界面相关的 ViewModel、窗口仍在 `App.xaml.cs`），否则集成测试测不到。
- Worker 经 `TcpFaultProxy` 连接 Master，用来模拟崩溃（连接被重置）和失联（连接不断但收不到数据）。健康检查超时在测试里缩短为 5 秒（`HealthCheckSettings`），Worker 的 keepalive 为 2 秒/2 秒、重连退避上限 1 秒。
- 等待一律用 `Poll`（轮询加超时），不要写固定 sleep。集成测试集合禁止并行，在其他测试之后单独运行。
- 失败时，测试输出里有 Master 和各 Worker 的完整日志，`[Component]` 标出来源。
- `MasterWorkerKnownDefectTests` 里是集成测试发现的生产缺陷（D1–D4，见 `docs/project/overview.md`），T4、T5 已全部修复并去掉 Skip，现在是回归测试。新发现的缺陷照此先写成 Skip 的测试。
- `MasterReliabilityTests` 是 T4 的回归测试。只与 Master 有关的行为用 `RawNodeClient`（直接调用 gRPC 接口的假节点）验证，可以精确控制注册参数、进度和结果的发送顺序。`WorkerReliabilityTests` 是 T5 的回归测试（robots、失联期间完成的结果、Master 不可达时启动、令牌错误退出）。
- `TestWorker.StopAsync` 不吞异常：Worker 停止或释放时抛异常会让测试失败。
- 测试网站的 `/robots.txt` 默认 404（Worker 视为没有限制），`HoldRequests` 也会扣住 robots.txt 请求。

```bash
cd DataAnalysis && python -m venv .venv && .venv\Scripts\activate && pip install -r requirements.txt
streamlit run app.py
```

- 有代理时，NLTK 首次下载会被拒绝，需要设置 `NLTK_ALLOW_PROXIED_URLOPEN=1`。
- 真实测试数据在本机 `D:\毕设相关\数据分析\元数据\*.json`（22 个导出文件，约 1050 篇论文）。

**WPF 界面无法自动操作**。改动要通过单元测试或集成测试验证；只能靠人工点界面确认的，在交付时明确列出复测步骤。

## 代码约定

- **注释与日志**：XML 注释、日志、提交信息都用中文。日志统一用 `CrawlerCore.Infrastructure.LogHelper`（Master/Worker 中也有 `ILogger<T>`，与所在文件保持一致）。
- **时间与配置**：时间一律用 UTC，界面显示时再转换为本地时间。配置走 `appsettings.json` + 环境变量（`MASTER_ENDPOINT`、`NODE_AUTH_TOKEN`、`WORKER_ENDPOINT`、`MASTER_NODE_ID`，以及 `Section__Key` 形式），不要硬编码。Worker 的主机构建在 `CrawlerWorker/Hosting/WorkerConfiguration.cs`，不要在 `Program.cs` 里另建配置。
- **采集合规**：Worker 的每个请求都要经过 robots.txt 检查（`IRobotsPolicy`）和按主机限速（`IHostRateLimiter`），新增请求路径（例如 T6 的 API 插件）不能绕过它们。真实网站只做少量验证请求（间隔不少于 15 秒）。
- **依赖注入生命周期**：`DbContext`、`ITaskDataService`、`INodeManagementService` 是 Scoped。单例服务和 ViewModel 都是单例，**不得直接注入 Scoped 服务**，要通过 `IServiceScopeFactory` 每次创建作用域。否则会多线程共用同一个 DbContext，曾导致界面数据偶发不同步。
- **gRPC 流**：
  - 写入一律经过 `Channel<T>`，由单一写循环串行写出。
  - 同一节点的消息在读循环里按到达顺序处理，不要用 `Task.Run` 并发处理（会导致状态乱序）。
- **WPF/MVVM**：
  - ViewModel 只通过 `IEventAggregator` 接收后台事件，不直接调用 gRPC/SignalR。
  - 更新 UI 用 `Dispatcher.InvokeAsync`（先进先出，保持顺序），不要用阻塞的 `Invoke`，也不要因为抢锁超时就丢弃事件。
  - 带 `CanExecute` 的 CommunityToolkit 命令，必须在相关属性上加 `[NotifyCanExecuteChangedFor]`。
  - 避免新增 `async void`（事件处理器除外，且必须在内部捕获异常）。
- **协议与数据契约**：
  - 修改 `crawler.proto` 会同时影响 Master 和 Worker，必须同步更新 `CrawlerCore.Tests/Grpc/ProtoContractTests.cs`。
  - `ThesisPlugin` 输出的 `parsedData` 键（`ArXivId`、`Title`、`Authors`、`AuthorsList`、`Abstract`、`Subjects`、`SubjectsList`、`PublicationDate`、`DOI`、`PdfUrl`、`SourceUrl` 等）是 `DataAnalysis/src/data_processor.py` 的输入契约，任何一边改了，另一边都要同步改。值的含义：`AuthorsList`/`SubjectsList` 是 JSON 字符串数组（“名 姓”格式的全部作者），`Authors`/`Subjects` 是用 `, ` / `; ` 拼接的字符串，`PublicationDate` 是首次提交日期（如 `12 Jun 2017`），`ArXivId` 不带版本号，`DOI` 是页面上第一个 doi.org 链接（通常是 arXiv 分配的 `10.48550/arXiv.<编号>`）。上报时值的转换规则在 `CrawlerCore/Common/ParsedDataConverter.cs`（字符串原样，其他标量按不变区域性、时间用 “O” 格式，列表与复杂类型为 JSON）。新的论文类插件要通过 `CrawlerCore.Tests/Plugins/ParsedDataContract.cs` 的契约断言。
  - 插件测试只用手写的最小 HTML 夹具，不要把真实页面或 `D:\毕设相关` 下的数据复制进仓库。
- **数据库**：用 `EnsureCreated`，不用迁移；改了实体结构，旧的 `crawler.db` 需要删除重建。

## 文档与提交

- 完成一项工作后，更新 `docs/project/overview.md` 中的完成度和已知问题。设计有变化时同步更新 `docs/project/design.md`。
- **后续工作按 [`docs/roadmap.md`](docs/roadmap.md) 串行推进**：由管理会话一次派一个子代理，验收通过后合并到 master 并推送（roadmap 已给出授权）。普通决定直接采用推荐做法，并写进 roadmap 的“决策记录”；只有 roadmap“决策规则”里列出的情况才停下来问用户。
- 不在 roadmap 流程内的工作，**只在用户要求时才提交和推送**。用户习惯直接提交到 master，提交信息用中文。
- 远程仓库 `github.com/mzqlk/DistributedCrawler` 是**公开的**。不得提交个人信息（姓名、学号、老师姓名、评语、答辩材料）和来源不明的第三方代码。从 `D:\毕设相关` 引入任何内容之前都要检查。
