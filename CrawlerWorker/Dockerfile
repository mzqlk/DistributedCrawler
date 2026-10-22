FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# 还原依赖：Worker 以及构建时引用的项目（插件项目只保证构建顺序，发布时复制到 Plugins/<插件名>/）
COPY ["CrawlerWorker/CrawlerWorker.csproj", "CrawlerWorker/"]
COPY ["CrawlerCore/CrawlerCore.csproj", "CrawlerCore/"]
COPY ["ThesisPlugin/ThesisPlugin.csproj", "ThesisPlugin/"]
COPY ["MockPlugin/MockPlugin.csproj", "MockPlugin/"]
RUN dotnet restore "CrawlerWorker/CrawlerWorker.csproj"

# 发布（构建上下文中的 bin/obj 等本机文件由 .dockerignore 排除）
COPY . .
RUN dotnet publish "CrawlerWorker/CrawlerWorker.csproj" -c $BUILD_CONFIGURATION -o /app/publish --no-restore

# 最终运行镜像：以基础镜像提供的非 root 用户运行
FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
USER $APP_UID

# Worker 只作为客户端连接 Master，不监听端口。
# 节点令牌没有默认值：运行时必须通过 NODE_AUTH_TOKEN 提供（与 Master 一致），缺少时 Worker 报错并以退出码 1 退出
ENV MASTER_ENDPOINT="http://host.docker.internal:5001"
ENV DOTNET_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "CrawlerWorker.dll"]
