# 完全对齐原项目官方Worker Dockerfile规范，.NET 10兼容
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
WORKDIR /app
EXPOSE 5002

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# 还原依赖
COPY ["CrawlerWorker/CrawlerWorker.csproj", "CrawlerWorker/"]
COPY ["CrawlerCore/CrawlerCore.csproj", "CrawlerCore/"]
RUN dotnet restore "CrawlerWorker/CrawlerWorker.csproj"

# 构建发布
COPY . .
WORKDIR "/src/CrawlerWorker"
RUN dotnet build "CrawlerWorker.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "CrawlerWorker.csproj" -c $BUILD_CONFIGURATION -o /app/publish

# 最终运行镜像
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# 100%对齐原项目官方环境变量规范，全配置项支持
ENV MASTER_ENDPOINT="http://master:5001"
ENV MASTER_NODE_ID=""
ENV NODE_AUTH_TOKEN="your-node-auth-token-here"
ENV WORKER_ENDPOINT="http://0.0.0.0:5002"
ENV DOTNET_ENVIRONMENT=Production

# 启动入口
ENTRYPOINT ["dotnet", "CrawlerWorker.dll"]