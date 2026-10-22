# 运行、部署与配置

> 来源：`部署命令.docx`、`项目文档.docx` 第 5 章、论文 6.1，已对照仓库中的 `appsettings.json`、`docker-compose.yml`、`CrawlerWorker/Dockerfile`。

## 本地运行

```powershell
dotnet build                                   # 解决方案 DistributedCrawler.slnx
$env:GrpcSettings__NodeAuthToken = "<随机字符串>"  # Master 与 Worker 的终端都要设置，值相同（见下文“节点令牌”）
dotnet run --project CrawlerMaster.WPF          # 先启动 Master（首次运行在程序输出目录创建 crawler.db）
dotnet run --project CrawlerWorker              # 再启动 Worker（另开终端，同样先设置令牌）
```

Master 的 `appsettings.json` 和数据库都按程序所在目录（输出目录）查找，与在哪个目录执行 `dotnet run` 无关（2026-10-22 T4 起）。

## 节点令牌

Worker 注册和建流时带 `Authorization: Bearer <令牌>`，Master 用 `GrpcSettings:NodeAuthToken` 校验（2026-10-22 T4 起真正启用）。

- Master 的 `appsettings.json` 中令牌为空。令牌为空，或等于曾经提交在公开仓库里的默认值 `test-token` 时，Master 启动时记 Error 日志并弹窗提示，**拒绝所有 Worker**。
- 生成一个随机令牌，例如 PowerShell 7：`[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))`（Windows PowerShell 5.1 可以用 `(New-Guid).Guid`）。
- Master：设置环境变量 `GrpcSettings__NodeAuthToken`（在启动 Master 的终端里，或在系统环境变量中设置）。
- Worker：同样设置 `GrpcSettings__NodeAuthToken`。`NODE_AUTH_TOKEN` 目前会被 Worker 的 `appsettings.json`（仍为 `test-token`）覆盖而不生效（W-13，T5 修复）。
- Docker：在 T5 修改 `docker-compose.yml` 之前，需要在 `environment` 中加一行 `GrpcSettings__NodeAuthToken=<与 Master 相同的令牌>`，否则容器里的 Worker 会因 `test-token` 被拒绝而退出。
- gRPC 是明文 HTTP/2，令牌可被同一网络中的人截获；需要更高安全性时应在 Kestrel 上配置 TLS（目前未实现）。

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

## 环境变量（Master）

Master 使用 .NET 标准的 `Section__Key` 双下划线环境变量，优先级高于 `appsettings.json`，例如 `GrpcSettings__NodeAuthToken`、`LoadBalancingSettings__MaxMemoryPercent`。

## 环境变量（Worker，优先级高于 appsettings）

| 变量 | 映射 | 说明 |
|---|---|---|
| `MASTER_ENDPOINT` | `GrpcSettings:MasterEndpoint` | Master gRPC 地址 |
| `NODE_AUTH_TOKEN` | `GrpcSettings:NodeAuthToken` | 必须与 Master 一致 |
| `WORKER_ENDPOINT` | `GrpcSettings:WorkerEndpoint` | Worker 控制服务监听地址 |
| `MASTER_NODE_ID` | NodeId | 固定节点标识，防止容器重建后 NodeId 漂移 |
| `DOTNET_ENVIRONMENT` | — | `Production` |

另支持 .NET 标准的 `Section__Key` 双下划线环境变量。

> 注意（2026-10-22 实测）：上表中 `MASTER_ENDPOINT`、`NODE_AUTH_TOKEN`、`WORKER_ENDPOINT` 的映射目前会被 `appsettings.json` 覆盖，并不生效（见 [`overview.md` §4 第 8 条](overview.md#4-已知问题与待办)）。修复前请改用 `GrpcSettings__MasterEndpoint`、`GrpcSettings__NodeAuthToken` 这类双下划线写法，或直接修改 `appsettings.json`。

## 端口

| 端口 | 用途 |
|---|---|
| 5001 | Master gRPC（HTTP/2 明文，Worker 侧开启 `Http2UnencryptedSupport`） |
| 5002 | Worker `WorkerControl` 服务端底座（预留） |
| 5003 | Master SignalR Hub `/crawlerHub`（默认只监听 127.0.0.1，见 `SignalRSettings:ListenAddress`） |

端口被占用时 Master 启动会报“端口被占用，请检查 gRPC(5001)/SignalR(5003)”。

## 主要配置项（论文表 6-1 + 实际文件）

| 配置项 | 默认值 | 说明 |
|---|---|---|
| `GrpcSettings:MasterEndpoint` | `http://0.0.0.0:5001`（Worker 端为 `http://host.docker.internal:5001`） | Master 监听/连接地址 |
| `GrpcSettings:NodeAuthToken` | 空（Master）/ `test-token`（Worker，T5 修改） | 节点认证令牌。Master 拒绝空令牌和 `test-token`，必须通过环境变量设置随机串，见“节点令牌” |
| `GrpcSettings:KeepAlivePingDelaySeconds` / `KeepAlivePingTimeoutSeconds` | 15 / 30 | Master 的 HTTP/2 keepalive：连接上这么久没有收到任何帧就发 PING，超时未回应即关闭连接，用于发现半开连接 |
| `GrpcSettings:HeartbeatIntervalSeconds` | 5 | 心跳间隔 |
| `GrpcSettings:MaxRetryAttempts` / `RetryDelayMilliseconds` | 3 / 1000 | 重试与重连 |
| `GrpcSettings:MaxMessageSizeMb` | 64 | gRPC 收发上限 |
| `CrawlerSettings:MaxConcurrentRequests` | 10 | 单任务最大并发请求 |
| `CrawlerSettings:DelayBetweenRequestsMs` | 1000 | 请求间隔（插件 `CrawlDelay` 优先，arXiv 插件为 15000） |
| `CrawlerSettings:MaxConcurrentTasks` | Worker 配置 2（代码默认 5） | Worker 同时执行任务数 |
| `CrawlerSettings:EnableProgressSimulation` | Master true / Worker false | 进度模拟（仅 DEBUG 生效） |
| `Database:ConnectionString` | `Data Source=crawler.db` | Master SQLite。相对路径相对于程序所在目录（T4 起；之前相对于工作目录）。注意：`appsettings.json` 里写的是 `ConnectionStrings:ConnectionString`，代码不读这一项（M-26，未修） |
| `SignalRSettings:Port` / `HubPath` | 5003 / `/crawlerHub` | 实时推送 |
| `SignalRSettings:ListenAddress` | `127.0.0.1` | SignalR 监听地址；需要其他机器访问时改为网卡地址或 `0.0.0.0` |
| `SignalRSettings:AllowedOrigins` | `[]` | 允许跨域访问 Hub 的来源白名单，默认不允许任何跨域来源 |
| `SignalRSettings:NodeUpdateThrottleSeconds` / `TaskProgressThrottleSeconds` | 5 / 2 | 推送节流；Worker 端也读 `TaskProgressThrottleSeconds`，作为进度上报的节流间隔（终态不节流） |
| `HealthCheckSettings:CheckIntervalSeconds` / `NodeTimeoutSeconds` | 15 / 60 | Master 健康检查间隔，以及多久没有心跳判为离线（判离线后 Master 主动断开该节点的流） |
| `LoadBalancingSettings:MaxCpuPercent` / `MaxMemoryPercent` | 90 / 90 | 资源使用率低于阈值的节点优先分配；没有节点满足时退而选择负载最低的在线节点 |
| `Logging:LogLevel:Default` | Debug | 生产环境应调高。gRPC 与 SignalR 的 `EnableDetailedErrors` 只在 `Development` 环境开启（T4 起） |

## 数据库（Master）

- 位置：默认 `<Master 输出目录>/crawler.db`（例如 `CrawlerMaster.WPF/bin/Debug/net10.0-windows/crawler.db`）。`.gitignore` 已忽略 `*.db` 及其 WAL/SHM、备份文件，不会被提交。
- 启动时按实体模型检查每张表与每一列（不检查索引）。结构一致就直接使用；结构不符或文件无法读取时，库中有数据会先备份为 `crawler.db.bak-yyyyMMddTHHmmssZ`（UTC 时间），再删除重建，并弹窗提示备份位置；库中没有数据则直接重建。
- T4 给 `CrawlResults.TaskId` 加了唯一索引。旧库没有这个索引也能继续使用（写入前的检查同样会丢弃重复结果）；想要索引，先自行备份，再删除 `crawler.db` 让程序重建。
