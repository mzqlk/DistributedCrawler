# 运行、部署与配置

> 来源：`部署命令.docx`、`项目文档.docx` 第 5 章、论文 6.1，已对照仓库中的 `appsettings.json`、`docker-compose.yml`、`CrawlerWorker/Dockerfile`。

## 本地运行

```bash
dotnet build                      # 解决方案 DistributedCrawler.slnx
cd CrawlerMaster.WPF && dotnet run   # 先启动 Master（首次运行在当前目录创建 crawler.db）
cd CrawlerWorker && dotnet run       # 再启动 Worker（另开终端）
```

插件：构建 `ThesisPlugin`/`MockPlugin` 后，Worker 构建会把 `*/bin/$(Configuration)/net10.0/*.dll` 复制到输出目录 `Plugins/`（见 `CrawlerWorker.csproj`）。

## Docker（仅 Worker）

```bash
docker-compose up -d --build           # 编译并运行（默认 3 副本）
docker-compose logs -f crawler-worker  # 查看日志
docker-compose down                    # 关闭容器
```

- Master 是 WPF，只在 Windows 宿主机运行；容器内 Worker 通过 `http://host.docker.internal:5001` 访问。
- Worker 镜像：多阶段构建（`sdk:10.0` 构建 → `aspnet:10.0` 运行），`EXPOSE 5002`。
- compose 把 `./CrawlerWorker/appsettings.json` 只读挂载到 `/app/appsettings.json`，`restart_policy: on-failure, max_attempts 3`，bridge 网络 `crawler-network`。
- 部署架构图：[`docs/thesis/figures/fig6-1.png`](../thesis/figures/fig6-1.png)。

## 环境变量（Worker，优先级高于 appsettings）

| 变量 | 映射 | 说明 |
|---|---|---|
| `MASTER_ENDPOINT` | `GrpcSettings:MasterEndpoint` | Master gRPC 地址 |
| `NODE_AUTH_TOKEN` | `GrpcSettings:NodeAuthToken` | 必须与 Master 一致 |
| `WORKER_ENDPOINT` | `GrpcSettings:WorkerEndpoint` | Worker 控制服务监听地址 |
| `MASTER_NODE_ID` | NodeId | 固定节点标识，防止容器重建后 NodeId 漂移 |
| `DOTNET_ENVIRONMENT` | — | `Production` |

另支持 .NET 标准的 `Section__Key` 双下划线环境变量。

## 端口

| 端口 | 用途 |
|---|---|
| 5001 | Master gRPC（HTTP/2 明文，Worker 侧开启 `Http2UnencryptedSupport`） |
| 5002 | Worker `WorkerControl` 服务端底座（预留） |
| 5003 | Master SignalR Hub `/crawlerHub` |

端口被占用时 Master 启动会报“端口被占用，请检查 gRPC(5001)/SignalR(5003)”。

## 主要配置项（论文表 6-1 + 实际文件）

| 配置项 | 默认值 | 说明 |
|---|---|---|
| `GrpcSettings:MasterEndpoint` | `http://0.0.0.0:5001`（Worker 端为 `http://host.docker.internal:5001`） | Master 监听/连接地址 |
| `GrpcSettings:NodeAuthToken` | `test-token` | 节点认证令牌，生产环境必须改为 32 位以上随机串 |
| `GrpcSettings:HeartbeatIntervalSeconds` | 5 | 心跳间隔 |
| `GrpcSettings:MaxRetryAttempts` / `RetryDelayMilliseconds` | 3 / 1000 | 重试与重连 |
| `GrpcSettings:MaxMessageSizeMb` | 64 | gRPC 收发上限 |
| `CrawlerSettings:MaxConcurrentRequests` | 10 | 单任务最大并发请求 |
| `CrawlerSettings:DelayBetweenRequestsMs` | 1000 | 请求间隔（插件 `CrawlDelay` 优先，arXiv 插件为 15000） |
| `CrawlerSettings:MaxConcurrentTasks` | Worker 配置 2（代码默认 5） | Worker 同时执行任务数 |
| `CrawlerSettings:EnableProgressSimulation` | Master true / Worker false | 进度模拟（仅 DEBUG 生效） |
| `ConnectionStrings:ConnectionString` | `Data Source=crawler.db` | Master SQLite |
| `SignalRSettings:Port` / `HubPath` | 5003 / `/crawlerHub` | 实时推送 |
| `SignalRSettings:NodeUpdateThrottleSeconds` / `TaskProgressThrottleSeconds` | 5 / 2 | 推送节流 |
| `Logging:LogLevel:Default` | Debug | 生产环境应调高；`EnableDetailedErrors` 生产应为 false |
