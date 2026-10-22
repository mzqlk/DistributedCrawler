# 分布式爬虫管理与数据分析工作站

一个基于 **.NET 10 + WPF** 构建的桌面端分布式爬虫管理系统，采用 Master-Worker 架构，支持多节点并行爬取、实时性能监控、图形化任务管理和插件化扩展；配套的 Python 数据分析模块（`DataAnalysis/`）对采集到的学术文献元数据做 LDA 主题建模与可视化。

本项目源自毕业设计《面向学术文献元数据采集与主题可视化分析系统研究与实现》，设计文档、论文与开发计划见 [`docs/`](docs/README.md)。

> ⚠️ **法律免责声明**：本项目仅用于学习和研究目的。使用者必须遵守《中华人民共和国网络安全法》《数据安全法》等相关法律法规，**禁止用于任何未经授权的数据采集活动**。请遵守目标网站的robots.txt协议，合理控制爬取频率，避免给目标网站造成不必要的负担。因使用本项目造成的任何法律责任，由使用者自行承担。

## ✨ 核心特性

- **分布式执行**：支持动态添加/移除Worker节点，无需重启Master，任务自动负载均衡分配
- **实时监控**：节点CPU/内存/磁盘使用率、资源趋势曲线、任务执行进度实时展示
- **图形化任务管理**：界面中创建任务（起始URL、插件、深度、页数上限、优先级），按优先级与节点负载自动分配
- **插件化架构**：基于接口的插件系统，快速扩展不同网站的爬取逻辑（内置 arXiv 论文插件）
- **高可用性**：节点故障自动检测、任务重新分配、Master 重启后恢复未调度任务
- **高性能通信**：基于gRPC双向流式通信，低延迟、高吞吐量
- **容器化部署**：Worker节点支持Docker一键部署，快速扩容集群
- **数据持久化**：SQLite本地存储，支持爬取结果分页查询和JSON导出
- **主题分析**：导出的 JSON 可直接交给 `DataAnalysis/`（Streamlit + LDA）做主题建模、趋势与词云分析，并导出 CSV

## 🛠️ 技术栈

| 组件类别 | 技术选型 | 说明 |
|----------|----------|------|
| 前端框架 | WPF + .NET 10 | 现代化桌面应用，遵循MVVM架构 |
| 通信协议 | gRPC + Protobuf | 高性能跨语言RPC通信 |
| 数据持久化 | SQLite + EF Core 10.0 | 轻量级本地数据库，ORM映射 |
| UI组件 | MaterialDesignInXaml | 符合Material Design规范的组件库 |
| 数据可视化 | OxyPlot.Wpf | 实时性能图表展示 |
| 爬虫引擎 | HtmlAgilityPack + AngleSharp | 强大的HTML/XML解析能力 |
| 日志系统 | Serilog | 结构化日志，支持多输出源 |
| 容器化 | Docker | 环境隔离，一键部署Worker节点 |
| 实时推送 | SignalR | 对外推送节点状态与任务进度（WPF 界面内部通过事件聚合器刷新） |
| 数据分析 | Python + Streamlit + NLTK + Gensim | 文本预处理、LDA 主题建模、交互式可视化（`DataAnalysis/`） |

## 🏗️ 系统架构

系统采用经典的Master-Worker分布式架构，分为控制端和工作节点两部分：

```
┌─────────────────────────────────────────────────────────────┐
│                      Master 控制端 (WPF)                      │
├─────────────────────────────────────────────────────────────┤
│  UI层(视图)  │  ViewModel层  │  业务逻辑层(Services)         │
│              │               │  - 节点管理/健康检查          │
│              │               │  - 任务调度/负载均衡          │
│              │               │  - 故障任务恢复              │
│              │               │  - 实时数据同步              │
└─────────────────────────────────────────────────────────────┘
                              │  gRPC 双向流
                              ▼
┌─────────────────────────────────────────────────────────────┐
│                    Worker 节点 (Console)                     │
├─────────────────────────────────────────────────────────────┤
│  gRPC 客户端  │  爬虫引擎    │  资源监控器                  │
│              │  - 插件加载   │  - CPU/内存/磁盘采集         │
│              │  - BFS深度爬取│  - 心跳上报                  │
└─────────────────────────────────────────────────────────────┘
```

- **Master控制端**：负责任务管理、节点调度、状态监控和用户交互
- **Worker节点**：作为gRPC客户端主动连接Master，执行具体的网页爬取和解析任务
- **通信层**：基于gRPC双向流式通信，实现命令下发和结果上报的全双工传输

## 🚀 快速开始

### 环境要求

- .NET 10 SDK 或更高版本
- Docker（可选，用于容器化部署Worker节点）
- Windows 10 1809+ / Windows 11（WPF桌面端仅支持Windows）

### 本地开发运行

1. **克隆仓库**
   ```bash
   git clone https://github.com/your-username/distributed-crawler.git
   cd distributed-crawler
   ```

2. **修改配置**
   - 编辑 `CrawlerMaster.WPF/appsettings.json` 和 `CrawlerWorker/appsettings.json`
   - ⚠️ **重要安全提示**：生产环境必须修改默认的 `NodeAuthToken` 值（建议使用32位以上随机字符串），确保Master和Worker配置一致
   - ⚠️ 生产环境请将 `EnableDetailedErrors` 设置为 `false`

3. **编译解决方案**
   ```bash
   dotnet build
   ```

4. **启动Master控制端**
   ```bash
   cd CrawlerMaster.WPF
   dotnet run
   ```
   > 💡 首次运行会自动在当前目录创建`crawler.db`数据库文件

5. **启动Worker节点（新开终端）**
   ```bash
   cd CrawlerWorker
   dotnet run
   ```
   > 💡 `CrawlerWorker/appsettings.json` 中的 `MasterEndpoint` 默认是给 Docker 用的 `http://host.docker.internal:5001`；在宿主机直接运行 Worker 时请改为 `http://localhost:5001`。解决方案内的插件项目（如 `ThesisPlugin`）构建后会自动复制到 Worker 输出目录的 `Plugins/`。

6. **数据分析（可选）**：见 [`DataAnalysis/`](DataAnalysis/README.md)

### Docker部署Worker节点

> 💡 Worker作为客户端主动连接Master，**无需暴露任何端口**

1. **构建Worker镜像**
   ```bash
   docker build -f CrawlerWorker/Dockerfile -t crawler-worker:latest .
   ```

2. **使用Docker Compose启动多副本**
   ```bash
   # Windows/macOS
   docker-compose up -d --scale crawler-worker=3

   # Linux（需替换为宿主机实际IP地址）
   MASTER_ENDPOINT=http://192.168.1.100:5001 docker-compose up -d --scale crawler-worker=3
   ```

   默认启动3个Worker节点，可根据需要调整副本数量。

## ⚙️ 核心配置说明

### Master配置 (CrawlerMaster.WPF/appsettings.json)

```json
{
  "GrpcSettings": {
    "MasterEndpoint": "http://0.0.0.0:5001",    // gRPC服务监听地址
    "HeartbeatIntervalSeconds": 5,              // 心跳上报间隔（秒）
    "MaxMessageSizeMb": 64,                     // 最大消息大小（MB）
    "NodeAuthToken": "your-secure-token-here"   // 节点认证令牌（必须修改）
  },
  "CrawlerSettings": {
    "RequestTimeoutSeconds": 30,                // HTTP请求超时时间（秒）
    "DelayBetweenRequestsMs": 1000              // 请求间隔（毫秒，建议不小于500）
  },
  "SignalRSettings": {
    "Port": 5003,                               // SignalR实时推送端口
    "HubPath": "/crawlerHub"                    // SignalR Hub路径
  }
}
```

### Worker配置 (CrawlerWorker/appsettings.json)

```json
{
  "GrpcSettings": {
    "MasterEndpoint": "http://host.docker.internal:5001",  // Master控制端地址（本机直接运行改为 http://localhost:5001）
    "NodeAuthToken": "your-secure-token-here"   // 与Master保持一致
  },
  "CrawlerSettings": {
    "MaxConcurrentTasks": 2                     // 单节点最大并发任务数
  }
}
```

## 📖 使用指南

### 1. 节点管理
- 启动Worker节点后会自动注册到Master
- 在「节点监控」页面查看所有在线节点的资源使用情况，选中节点查看 CPU/内存趋势，双击查看节点详情
- Master 每 15 秒检查一次，超过 1 分钟无心跳的节点会被自动标记为离线，其运行中的任务会被重置并重新分配

### 2. 创建爬虫任务
- 在「任务管理」页面指定任务名、起始URL、插件、爬取深度、最大页数
- 支持任务优先级设置（1-5，数值越大优先级越高）
- 任务按优先级入队，由负载均衡器分配给 CPU/内存占用最低的在线节点

### 3. 任务监控
- 实时查看任务执行进度、已爬取页面数、运行状态和执行节点

### 4. 数据查看与导出
- 分页查看所有爬取结果，按任务ID/任务名称搜索
- 双击单页结果查看完整解析数据
- 导出整个任务或单页结果为 JSON（可直接作为 `DataAnalysis/` 的输入）

## 🔌 扩展开发

### 开发自定义爬虫插件

1. 新建.NET 10类库项目，引用`CrawlerCore`项目
2. 实现`ICrawlerPlugin`接口：

```csharp
using CrawlerCore.Entities;
using CrawlerCore.Interfaces;
using HtmlAgilityPack;

namespace MyCustomPlugin
{
    public class MyPlugin : ICrawlerPlugin
    {
        public string Name => "MyCustomPlugin"; // 全局唯一标识，创建任务时填写的插件名
        public string Version => "1.0.0";

        public Task<PluginResult> ExecuteAsync(PluginContext context)
        {
            // context.Html 为页面原始HTML，context.Url 为页面地址
            var doc = new HtmlDocument();
            doc.LoadHtml(context.Html);

            // 自定义解析逻辑
            var result = new PluginResult { Success = true };
            result.Data["title"] = doc.DocumentNode.SelectSingleNode("//title")?.InnerText.Trim() ?? string.Empty;

            return Task.FromResult(result);
        }

        public Dictionary<string, string> GetDefaultConfiguration()
        {
            return new Dictionary<string, string>
            {
                { "CrawlDelay", "1000" }
            };
        }

        // 接口默认实现，可根据需要重写
        public bool ShouldFollowLink(string url, int currentDepth)
        {
            // 只爬取当前域名下的链接
            return url.StartsWith("https://example.com", StringComparison.OrdinalIgnoreCase);
        }
    }
}
```

3. 编译插件，将生成的DLL文件复制到Worker节点的`Plugins`目录
4. 重启Worker节点，插件会自动加载

> 💡 插件版本必须与CrawlerCore版本保持一致，否则可能出现兼容性问题

## 📁 项目结构

```
DistributedCrawler/
├── CrawlerMaster.WPF/          # Master控制端（WPF桌面应用）
│   ├── Views/                  # UI视图（XAML）
│   ├── ViewModels/             # MVVM视图模型
│   ├── Services/               # 业务服务实现
│   └── Data/                   # 数据库上下文和实体
├── CrawlerWorker/              # Worker节点（控制台应用）
│   ├── Communication/          # gRPC通信相关
│   └── Services/               # 核心业务服务
│       └── Engine/             # 爬虫引擎和插件管理
├── CrawlerCore/                # 共享核心库（所有项目引用）
│   ├── Entities/               # 数据实体定义
│   ├── Interfaces/             # 公共接口定义
│   ├── Protos/                 # gRPC协议文件
│   └── Common/                 # 通用工具类和扩展方法
├── ThesisPlugin/               # arXiv 论文爬虫插件
├── MockPlugin/                 # 测试用插件
├── CrawlerCore.Tests/          # 单元测试 + Master–Worker 端到端集成测试（Integration/）
├── DataAnalysis/               # Python 数据分析模块（Streamlit + LDA）
├── docs/                       # 设计文档、论文与开发计划
├── docker-compose.yml          # Docker Compose部署配置
└── README.md                   # 项目说明文档
```

## 🧪 测试与维护

- **运行测试**：
  ```bash
  dotnet test                                                        # 全部测试
  dotnet test CrawlerCore.Tests --filter "Category=Integration"      # 只跑端到端集成测试
  dotnet test CrawlerCore.Tests --filter "Category!=Integration"     # 只跑单元测试
  ```
  集成测试在进程内启动 Master 后台服务和真实 Worker，爬取本地测试网站，不需要打开界面，详见 `CLAUDE.md`。

- **日志查看**：
  - 日志默认输出到控制台和文件
  - 文件日志位于程序目录下的`logs`目录，按日期滚动，默认保留7天
  - 可通过`appsettings.json`配置日志级别和保留策略

- **数据库维护**：
  - 数据库文件为`crawler.db`，位于Master运行目录
  - 支持使用SQLite工具直接查看和修改数据
  - ⚠️ 建议定期备份数据库文件，防止数据丢失

## ❓ 常见问题

1. **Worker节点无法连接到Master**
   - 检查Master和Worker的`NodeAuthToken`是否完全一致
   - 确认Master的5001端口已开放且未被占用
   - Docker部署时，Windows/macOS使用`host.docker.internal`，Linux使用宿主机实际IP
   - 检查防火墙是否阻止了端口访问

2. **插件加载失败**
   - 确认插件DLL已复制到Worker的`Plugins`目录
   - 检查插件是否正确实现了`ICrawlerPlugin`接口
   - 确认插件目标框架为.NET 10，且与CrawlerCore版本一致
   - 查看Worker日志获取详细错误信息

3. **端口被占用**
   - 修改`appsettings.json`中的gRPC(5001)和SignalR(5003)端口
   - 排查端口占用：
     - Windows：`netstat -ano | findstr :5001`
     - Linux：`lsof -i :5001`

4. **Windows Defender误报病毒**
   - 这是爬虫程序的常见问题，可将程序目录添加到Windows Defender的信任列表
   - 建议从官方仓库下载源码自行编译，避免使用来历不明的二进制文件

5. **如何提高爬取速度**
   - 增加Worker节点数量
   - 适当调大`MaxConcurrentTasks`参数（建议不超过CPU核心数）
   - 减小`DelayBetweenRequestsMs`参数（注意反爬风险）

## 🗺️ 开发路线图

### v1.1（计划中）
- [ ] 可视化流程设计器（拖拽构建爬取流程）
- [ ] 任务暂停/继续/取消（协议中已定义 `TaskControl`，界面与 Worker 端未实现）
- [ ] 支持MySQL/PostgreSQL数据库
- [ ] 集成Redis作为分布式缓存
- [ ] 添加代理池支持
- [ ] User-Agent自动轮换

### v1.2（计划中）
- [ ] 支持断点续爬
- [ ] 开发Web版管理界面
- [ ] 插件市场功能
- [ ] 更多数据导出格式（Excel、XML）

### v2.0（远期规划）
- [ ] 支持JavaScript渲染（Playwright集成）
- [ ] 分布式任务队列
- [ ] 智能反爬策略
- [ ] 多租户支持

## 🤝 贡献

欢迎提交Issue和Pull Request来帮助改进项目！

### 贡献指南

1. **提交Issue**
   - 报告Bug请使用Bug模板，提供详细的复现步骤和环境信息
   - 提出新功能请使用Feature模板，说明功能用途和实现思路

2. **提交PR**
   - Fork本仓库并创建特性分支：`git checkout -b feature/amazing-feature`
   - 确保代码符合[C#官方编码规范](https://learn.microsoft.com/zh-cn/dotnet/csharp/fundamentals/coding-style/coding-conventions)
   - 新增功能请添加对应的单元测试
   - 提交信息遵循[Conventional Commits](https://www.conventionalcommits.org/)规范
   - PR标题清晰描述变更内容

3. **代码规范**
   - 使用4个空格缩进
   - 公共方法和类必须添加XML注释
   - 避免使用魔法数字，使用常量代替
   - 异常处理要明确，不要捕获所有异常
