# CLAUDE.md

分布式爬虫管理与数据分析工作站：.NET 10 WPF Master + Console Worker（gRPC 双向流）+ 插件，外加 Python 数据分析模块。源自一篇已答辩的毕业设计，现阶段目标是把项目做完整：补齐原计划未实现的功能、审阅并修复代码。

**开始任何工作前先读 [`docs/README.md`](docs/README.md)**，其中：
- `docs/project/overview.md`：完成度、已知问题、文档与代码的差异。
- `docs/project/design.md`：设计约定。
- `docs/thesis/proposal.md`：原计划中尚未实现的功能清单。

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
dotnet test CrawlerCore.Tests/CrawlerCore.Tests.csproj    # 目前 104 个测试，约 25 秒
dotnet run --project CrawlerMaster.WPF                     # 仅 Windows
dotnet run --project CrawlerWorker                         # 本机运行需把 appsettings 里的 MasterEndpoint 改为 http://localhost:5001
```

```bash
cd DataAnalysis && python -m venv .venv && .venv\Scripts\activate && pip install -r requirements.txt
streamlit run app.py
```

- 有代理时，NLTK 首次下载会被拒绝，需要设置 `NLTK_ALLOW_PROXIED_URLOPEN=1`。
- 真实测试数据在本机 `D:\毕设相关\数据分析\元数据\*.json`（22 个导出文件，约 1050 篇论文）。

**WPF 界面无法自动操作**。改动要通过单元测试或集成测试验证；只能靠人工点界面确认的，在交付时明确列出复测步骤。

## 代码约定

- **注释与日志**：XML 注释、日志、提交信息都用中文。日志统一用 `CrawlerCore.Infrastructure.LogHelper`（Master/Worker 中也有 `ILogger<T>`，与所在文件保持一致）。
- **时间与配置**：时间一律用 UTC，界面显示时再转换为本地时间。配置走 `appsettings.json` + 环境变量（`MASTER_ENDPOINT`、`NODE_AUTH_TOKEN`、`WORKER_ENDPOINT`、`MASTER_NODE_ID`），不要硬编码。
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
  - `ThesisPlugin` 输出的 `parsedData` 键（`ArXivId`、`Title`、`Authors`、`AuthorsList`、`Abstract`、`Subjects`、`SubjectsList`、`PublicationDate`、`DOI`、`PdfUrl`、`SourceUrl` 等）是 `DataAnalysis/src/data_processor.py` 的输入契约，任何一边改了，另一边都要同步改。
- **数据库**：用 `EnsureCreated`，不用迁移；改了实体结构，旧的 `crawler.db` 需要删除重建。

## 文档与提交

- 完成一项工作后，更新 `docs/project/overview.md` 中的完成度和已知问题。设计有变化时同步更新 `docs/project/design.md`。
- **只在用户要求时才提交和推送**。用户习惯直接提交到 master、提交信息用中文。并行会话在 git worktree 的独立分支上工作，完成后先 rebase 到 `origin/master`，再执行 `git push origin HEAD:master`。
- 远程仓库 `github.com/mzqlk/DistributedCrawler` 是**公开的**。不得提交个人信息（姓名、学号、老师姓名、评语、答辩材料）和来源不明的第三方代码。从 `D:\毕设相关` 引入任何内容之前都要检查。
