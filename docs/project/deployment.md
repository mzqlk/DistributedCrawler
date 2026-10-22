# 运行、部署与配置

> 来源：`部署命令.docx`、`项目文档.docx` 第 5 章、论文 6.1，已对照仓库中的 `appsettings.json`、`docker-compose.yml`、`CrawlerWorker/Dockerfile`（2026-10-22 T5 后）。

## 本地运行

```powershell
dotnet build                                   # 解决方案 DistributedCrawler.slnx
$env:GrpcSettings__NodeAuthToken = "<随机字符串>"  # Master 与 Worker 的终端都要设置，值相同（见下文“节点令牌”）
dotnet run --project CrawlerMaster.WPF          # 先启动 Master（首次运行在程序输出目录创建 crawler.db）
dotnet run --project CrawlerWorker              # 再启动 Worker（另开终端，同样先设置令牌）
```

- Master 与 Worker 的 `appsettings.json`、Master 的数据库都按程序所在目录（输出目录）查找，与在哪个目录执行 `dotnet run` 无关（Master 自 T4、Worker 自 T5）。
- Worker 的 `GrpcSettings:MasterEndpoint` 默认是 `http://localhost:5001`，在本机运行不用改配置；Master 在其他机器上时设置 `MASTER_ENDPOINT`。
- Worker 先于 Master 启动、或 Master 重启时，Worker 会按退避一直重试并自动重新注册，不需要重启 Worker（T5）。
- 启动日志会列出加载到的插件，例如“插件加载成功: ArXivThesisPlugin v1.4.0”。

## 节点令牌

Worker 注册和建流时带 `Authorization: Bearer <令牌>`，Master 用 `GrpcSettings:NodeAuthToken` 校验（T4 起真正启用）。

- Master 与 Worker 的 `appsettings.json` 中令牌都为空，仓库里不保存任何令牌。
- 生成一个随机令牌，例如 PowerShell 7：`[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))`（Windows PowerShell 5.1 可以用 `(New-Guid).Guid`）。
- Master：设置环境变量 `GrpcSettings__NodeAuthToken`。令牌为空，或等于曾经提交在公开仓库里的默认值 `test-token` 时，Master 启动时记 Error 日志并弹窗提示，**拒绝所有 Worker**。
- Worker：设置 `NODE_AUTH_TOKEN` 或 `GrpcSettings__NodeAuthToken`（两种写法都生效，T5 起）。令牌为空或为 `test-token` 时，Worker 启动时记 Fatal 日志并以**退出码 1** 退出；令牌与 Master 不一致时，Worker 收到 `Unauthenticated` 后同样以退出码 1 退出，不会反复重试。
- Docker：在仓库根目录的 `.env` 文件（已被 `.gitignore` 忽略）中写 `NODE_AUTH_TOKEN=<与 Master 相同的令牌>`，或在执行 compose 的终端设置同名环境变量；都没有时 `docker compose` 直接报错。
- 启动日志只记录令牌的长度，不输出令牌内容。
- gRPC 是明文 HTTP/2，令牌可被同一网络中的人截获；需要更高安全性时应在 Kestrel 上配置 TLS（目前未实现）。

## 插件

构建或发布 Worker 时，`CrawlerWorker.csproj` 把 `<CrawlerPlugin>` 列出的插件项目（ThesisPlugin、MockPlugin）复制到输出目录的 `Plugins/<插件名>/`，每个目录里是插件程序集、pdb、deps.json 和插件自己的依赖（例如 HtmlAgilityPack），不含 CrawlerCore（由 Worker 提供）。新增插件时在 `CrawlerWorker.csproj` 的 `<CrawlerPlugin>` 中加一项，插件项目按 ThesisPlugin.csproj 的写法开启 `EnableDynamicLoading`。

插件目录可以用 `CrawlerSettings:PluginDirectory` 修改；直接放在插件目录下的单个 DLL 也会加载，与子目录中的插件重名时以子目录为准。

## 采集 arXiv 的注意事项

- Worker 遵守目标网站的 robots.txt：起始 URL、子链接和重定向目标在请求前都会检查，被禁止的地址不请求（arXiv 禁止 `/search`、`/api` 等，允许 `/abs`、`/list`）。Master 新建任务的默认起始 URL 是 `https://arxiv.org/list/cs.LG/recent`。
- 同一 Worker 内所有任务对同一主机的请求间隔取设置、插件 `CrawlDelay`（ArXivThesisPlugin 为 15 秒）和 robots.txt `Crawl-delay`（arXiv 为 15 秒）中的最大值。
- **采集 arXiv 时只运行 1 个 Worker**（compose 默认 1 个副本）：跨节点的总速率目前没有控制，多个 Worker 共用出口 IP 时合计频率会超过 `Crawl-delay`（路线图 T6 解决）。

## Docker（仅 Worker）

```bash
# 1. 在仓库根目录创建 .env（不要提交），写入与 Master 相同的令牌
echo "NODE_AUTH_TOKEN=<与 Master 相同的令牌>" > .env
# 2. 构建并启动（默认 1 个副本）
docker compose up -d --build
docker compose logs -f crawler-worker   # 查看日志
docker compose down                     # 关闭容器
```

- Master 是 WPF，只在 Windows 宿主机运行；容器内 Worker 默认通过 `http://host.docker.internal:5001` 访问（compose 已加 `extra_hosts: host.docker.internal:host-gateway`，Linux 主机也能解析）。Master 在其他机器上时设置 `MASTER_ENDPOINT`，例如 `MASTER_ENDPOINT=http://192.168.1.100:5001 docker compose up -d`。
- Worker 镜像：多阶段构建（`sdk:10.0` 还原并发布 → `aspnet:10.0` 运行），以非 root 用户（`$APP_UID`）运行，不暴露端口（Worker 只作为客户端连接 Master）。构建上下文由 `.dockerignore` 排除本机的 `bin/`、`obj/`、`.git/`、数据库等。
- compose：`restart: unless-stopped`；日志 `json-file`，单个文件 10 MB、保留 3 个；配置通过环境变量提供，不再挂载本机的 `appsettings.json`（需要改其他配置时用 `Section__Key` 形式的环境变量，例如 `CrawlerSettings__MaxConcurrentTasks=1`）。
- **NodeId**：不设置 `MASTER_NODE_ID` 时取容器主机名，每次重建容器都会变，Master 中会留下离线的旧节点。需要固定 NodeId 时在 `.env` 中设置 `MASTER_NODE_ID`；**多副本时不能设成同一个值**（同一 NodeId 有活跃连接时，另一个会被拒绝注册并一直重试）。
- 部署架构图：[`docs/thesis/figures/fig6-1.png`](../thesis/figures/fig6-1.png)。

## 环境变量（Master）

Master 使用 .NET 标准的 `Section__Key` 双下划线环境变量，优先级高于 `appsettings.json`，例如 `GrpcSettings__NodeAuthToken`、`LoadBalancingSettings__MaxMemoryPercent`。

## 环境变量（Worker）

支持 .NET 标准的 `Section__Key` 双下划线环境变量（例如 `GrpcSettings__MasterEndpoint`、`CrawlerSettings__MaxConcurrentTasks`），另有以下简写（T5 起真正生效，优先级最高）：

| 变量 | 映射 | 说明 |
|---|---|---|
| `MASTER_ENDPOINT` | `GrpcSettings:MasterEndpoint` | Master gRPC 地址 |
| `NODE_AUTH_TOKEN` | `GrpcSettings:NodeAuthToken` | 必须与 Master 一致；为空时 Worker 拒绝启动 |
| `WORKER_ENDPOINT` | `GrpcSettings:WorkerEndpoint` | `WorkerControl` 服务端底座的地址（预留，目前没有监听） |
| `MASTER_NODE_ID` | NodeId | 本 Worker 的节点标识（名字沿用历史），防止容器重建后 NodeId 漂移；同一台机器运行多个 Worker 时必须各不相同 |
| `DOTNET_ENVIRONMENT` | — | `Production` |

配置优先级（低 → 高）：`appsettings.json` → `appsettings.{环境}.json` → `Section__Key` 环境变量 → 命令行 → 上表的简写变量。

## 端口

| 端口 | 用途 |
|---|---|
| 5001 | Master gRPC（HTTP/2 明文，Worker 侧开启 `Http2UnencryptedSupport`） |
| 5002 | Worker `WorkerControl` 服务端底座（预留，没有监听） |
| 5003 | Master SignalR Hub `/crawlerHub`（默认只监听 127.0.0.1，见 `SignalRSettings:ListenAddress`） |

端口被占用时 Master 启动会报“端口被占用，请检查 gRPC(5001)/SignalR(5003)”。

## 主要配置项（论文表 6-1 + 实际文件）

| 配置项 | 默认值 | 说明 |
|---|---|---|
| `GrpcSettings:MasterEndpoint` | `http://0.0.0.0:5001`（Master 监听）/ `http://localhost:5001`（Worker） | Master 监听/连接地址；Docker 镜像中 `MASTER_ENDPOINT` 默认为 `http://host.docker.internal:5001` |
| `GrpcSettings:NodeAuthToken` | 空 | 节点认证令牌，必须通过环境变量设置随机串，见“节点令牌” |
| `GrpcSettings:KeepAlivePingDelaySeconds` / `KeepAlivePingTimeoutSeconds` | 15 / 30 | HTTP/2 keepalive：连接上这么久没有收到任何帧就发 PING，超时未回应即关闭连接，用于发现半开连接。Master（Kestrel）与 Worker（gRPC 客户端，T5）都使用 |
| `GrpcSettings:HeartbeatIntervalSeconds` | 5 | 心跳间隔 |
| `GrpcSettings:MaxRetryAttempts` / `RetryDelayMilliseconds` / `MaxRetryDelaySeconds` | 3 / 1000 / 30 | Worker 重连：从 `RetryDelayMilliseconds` 开始指数退避（加 ±20% 抖动），上限 `MaxRetryDelaySeconds`；连续失败超过 `MaxRetryAttempts` 次后重新注册 |
| `GrpcSettings:MasterResponseTimeoutSeconds` | 10 | Worker 注册、建流时等待 Master 响应的超时 |
| `GrpcSettings:ResultAckTimeoutSeconds` | 60 | Worker 发出结果后这么久没有收到回执就重发（之后翻倍，上限 10 分钟） |
| `GrpcSettings:MaxMessageSizeMb` | 64 | gRPC 收发上限 |
| `CrawlerSettings:DefaultUserAgent` | `DistributedCrawler/1.0 (+https://github.com/mzqlk/DistributedCrawler)` | 爬虫请求的 User-Agent；robots.txt 按其中的产品名匹配规则组 |
| `CrawlerSettings:RequestTimeoutSeconds` | 30 | 单个请求（含读取正文）的超时 |
| `CrawlerSettings:DelayBetweenRequestsMs` | 1000 | 同一主机相邻请求的最小间隔；实际取它、插件 `CrawlDelay`（arXiv 插件 15000）与 robots.txt `Crawl-delay` 的最大值，对 Worker 内所有任务共同生效 |
| `CrawlerSettings:MaxRequestRetries` | 2 | 超时、429、5xx、网络错误的重试次数；429/503 的 `Retry-After` 推迟该主机的所有请求（上限 5 分钟） |
| `CrawlerSettings:MaxResponseBytes` | 5242880（5 MB） | 单个响应正文的上限，超过的页面不解析 |
| `CrawlerSettings:SameHostOnly` | true | 只跟随与起始 URL 同主机的链接 |
| `CrawlerSettings:RobotsCacheHours` | 24 | robots.txt 的缓存时间 |
| `CrawlerSettings:PluginTimeoutSeconds` | 30 | 插件解析单个页面的超时 |
| `CrawlerSettings:PluginDirectory` | `Plugins` | 插件目录，相对路径相对于程序所在目录 |
| `CrawlerSettings:IgnoreCertificateErrors` | false | 是否忽略被爬网站的证书错误；打开时启动日志记警告 |
| `CrawlerSettings:MaxConcurrentTasks` | Worker 配置 2（代码默认 5） | Worker 同时执行的任务数，注册时上报给 Master，Master 不会分配超过这个数的任务 |
| `CrawlerSettings:MaxConcurrentRequests` | 10 | 未使用 |
| `CrawlerSettings:EnableProgressSimulation` | Master true / Worker false | 进度模拟（仅 DEBUG 生效；Worker 侧的模拟器已在 T5 删除） |
| `Database:ConnectionString` | `Data Source=crawler.db` | Master SQLite。相对路径相对于程序所在目录（T4 起；之前相对于工作目录）。注意：`appsettings.json` 里写的是 `ConnectionStrings:ConnectionString`，代码不读这一项（M-26，未修） |
| `SignalRSettings:Port` / `HubPath` | 5003 / `/crawlerHub` | 实时推送 |
| `SignalRSettings:ListenAddress` | `127.0.0.1` | SignalR 监听地址；需要其他机器访问时改为网卡地址或 `0.0.0.0` |
| `SignalRSettings:AllowedOrigins` | `[]` | 允许跨域访问 Hub 的来源白名单，默认不允许任何跨域来源 |
| `SignalRSettings:NodeUpdateThrottleSeconds` / `TaskProgressThrottleSeconds` | 5 / 2 | 推送节流；Worker 端也读 `TaskProgressThrottleSeconds`，作为运行中进度的上报间隔（每个任务的第一条与最终状态不节流） |
| `HealthCheckSettings:CheckIntervalSeconds` / `NodeTimeoutSeconds` | 15 / 60 | Master 健康检查间隔，以及多久没有心跳判为离线（判离线后 Master 主动断开该节点的流） |
| `LoadBalancingSettings:MaxCpuPercent` / `MaxMemoryPercent` | 90 / 90 | 资源使用率低于阈值的节点优先分配；没有节点满足时退而选择负载最低的在线节点 |
| `Logging:LogLevel:Default`、`Serilog:MinimumLevel` | Master Debug / Worker Information | Worker 自 T5 起默认 Information（Microsoft、Grpc 为 Warning）。gRPC 与 SignalR 的 `EnableDetailedErrors` 只在 `Development` 环境开启（T4 起） |

## 数据库（Master）

- 位置：默认 `<Master 输出目录>/crawler.db`（例如 `CrawlerMaster.WPF/bin/Debug/net10.0-windows/crawler.db`）。`.gitignore` 已忽略 `*.db` 及其 WAL/SHM、备份文件，不会被提交。
- 启动时按实体模型检查每张表与每一列（不检查索引）。结构一致就直接使用；结构不符或文件无法读取时，库中有数据会先备份为 `crawler.db.bak-yyyyMMddTHHmmssZ`（UTC 时间），再删除重建，并弹窗提示备份位置；库中没有数据则直接重建。
- T4 给 `CrawlResults.TaskId` 加了唯一索引。旧库没有这个索引也能继续使用（写入前的检查同样会丢弃重复结果）；想要索引，先自行备份，再删除 `crawler.db` 让程序重建。T5 没有改动数据库结构。
