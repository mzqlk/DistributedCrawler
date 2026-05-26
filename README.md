# 分布式爬虫管理与数据分析工作站

一个基于 **.NET 10 + WPF** 构建的桌面端分布式爬虫管理系统，采用 Master-Worker 架构，支持多节点并行爬取、实时性能监控、可视化任务调度和插件化扩展，为大规模数据采集提供高效、稳定、易用的解决方案。

> ⚠️ **法律免责声明**：本项目仅用于学习和研究目的。使用者必须遵守《中华人民共和国网络安全法》《数据安全法》等相关法律法规，**禁止用于任何未经授权的数据采集活动**。请遵守目标网站的robots.txt协议，合理控制爬取频率，避免给目标网站造成不必要的负担。因使用本项目造成的任何法律责任，由使用者自行承担。

## ✨ 核心特性

- **分布式执行**：支持动态添加/移除Worker节点，无需重启Master，任务自动负载均衡分配
- **实时监控**：节点CPU/内存/磁盘使用率、任务执行进度、日志全链路实时展示
- **可视化调度**：拖拽式流程设计器，无需编码即可构建复杂爬虫流程
- **插件化架构**：基于接口的插件系统，快速扩展不同网站的爬取逻辑
- **高可用性**：节点故障自动检测、任务重新分配、异常恢复机制
- **高性能通信**：基于gRPC双向流式通信，低延迟、高吞吐量
- **容器化部署**：Worker节点支持Docker一键部署，快速扩容集群
- **数据持久化**：SQLite本地存储，支持爬取结果分页查询和CSV/JSON导出

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
| 实时推送 | SignalR | Master与UI层实时数据同步 |

## 🏗️ 系统架构

系统采用经典的Master-Worker分布式架构，分为控制端和工作节点两部分：

```
┌─────────────────────────────────────────────────────────────┐
│                      Master 控制端 (WPF)                      │
├─────────────────────────────────────────────────────────────┤
│  UI层(视图)  │  ViewModel层  │  业务逻辑层(Services)         │
│              │               │  - 节点管理                  │
│              │               │  - 任务调度                  │
│              │               │  - 插件管理                  │
│              │               │  - 实时数据同步              │
└─────────────────────────────────────────────────────────────┘
                              │  gRPC 双向流
                              ▼
┌─────────────────────────────────────────────────────────────┐
│                    Worker 节点 (Console)                     │
├─────────────────────────────────────────────────────────────┤
│  gRPC 客户端  │  爬虫引擎    │  资源监控器                  │
│              │               │  - CPU/内存/磁盘采集         │
│              │               │  - 心跳上报                  │
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
    "MasterEndpoint": "http://localhost:5001",  // Master控制端地址
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
- 在「节点监控」页面查看所有在线节点的资源使用情况
- 支持手动标记离线节点和查看节点详细信息
- 超时2分钟无心跳的节点会被自动标记为离线，其未完成的任务会被重新分配

### 2. 创建爬虫任务
- **快速创建**：指定起始URL、插件、爬取深度和最大页数
- **流程设计**：使用可视化流程设计器，拖拽节点构建复杂爬取流程
- 支持任务优先级设置（1-10，数值越大优先级越高）
- 支持任务暂停、继续、停止和取消操作

### 3. 任务监控
- 实时查看任务执行进度、已爬取页面数和运行状态
- 查看任务运行日志，快速定位问题
- 任务失败后支持自动重试和手动重新执行

### 4. 数据查看与导出
- 分页查看所有爬取结果
- 支持按URL、标题等关键词搜索
- 导出爬取数据为CSV或JSON格式

## 🔌 扩展开发

### 开发自定义爬虫插件

1. 新建.NET 10类库项目，引用`CrawlerCore`项目
2. 实现`ICrawlerPlugin`接口：

```csharp
using CrawlerCore.Entities;
using CrawlerCore.Interfaces;

namespace MyCustomPlugin
{
    public class MyPlugin : ICrawlerPlugin
    {
        public string Name => "MyCustomPlugin"; // 全局唯一标识
        public string Version => "1.0.0";

        public async Task<PluginResult> ExecuteAsync(PluginContext context)
        {
            // 解析HTML内容
            var doc = context.HtmlDocument;
            
            // 自定义解析逻辑
            var result = new PluginResult();
            result.Data["title"] = doc.QuerySelector("title")?.TextContent?.Trim();
            
            return await Task.FromResult(result);
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
│   ├── Services/               # 核心业务服务
│   └── Engine/                 # 爬虫引擎和插件管理
├── CrawlerCore/                # 共享核心库（所有项目引用）
│   ├── Entities/               # 数据实体定义
│   ├── Interfaces/             # 公共接口定义
│   ├── Protos/                 # gRPC协议文件
│   └── Common/                 # 通用工具类和扩展方法
├── CrawlerPlugins/             # 官方插件库
├── CrawlerCore.Tests/          # 单元测试项目
├── docs/                       # 文档和截图
├── docker-compose.yml          # Docker Compose部署配置
└── README.md                   # 项目说明文档
```

## 🧪 测试与维护

- **运行单元测试**：
  ```bash
  dotnet test
  ```

- **日志查看**：
  - 日志默认输出到控制台和文件
  - 文件日志位于`Logs`目录，按日期滚动，默认保留30天
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
