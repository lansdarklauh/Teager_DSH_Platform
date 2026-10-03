# DSH 平台后端镜像（ASP.NET Core 10 + Node.js + @deepseek-ai/dsh）
# Teager_DevOps：项目类型 DotNet，服务目录 backend，Dockerfile 为 Dockerfile，不要勾选「仓库根 context」。
# 容器监听 5600。数据 / 日志 / 缓存分别固定在 /app/data、/app/logs、/app/cache，请在 compose / 部署表单里挂载到宿主机目录。
#
# 国内构建机常无法直连 registry.npmjs.org，默认使用 npmmirror；可用 build-arg 覆盖：
#   --build-arg NPM_REGISTRY=https://registry.npmjs.org
#   --build-arg DSH_VERSION=0.1.7-rc.2      # 固定 dsh 版本（默认 latest）

ARG NPM_REGISTRY=https://registry.npmmirror.com
ARG DSH_VERSION=latest

# ── 1) 构建 API ──
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# 先拷贝项目文件，利用 Docker 层缓存加速 restore
COPY Directory.Build.props ./
COPY src/Teager.Dsh.Domain/Teager.Dsh.Domain.csproj src/Teager.Dsh.Domain/
COPY src/Teager.Dsh.Infrastructure/Teager.Dsh.Infrastructure.csproj src/Teager.Dsh.Infrastructure/
COPY src/Teager.Dsh.Api/Teager.Dsh.Api.csproj src/Teager.Dsh.Api/
RUN dotnet restore src/Teager.Dsh.Api/Teager.Dsh.Api.csproj
COPY src/ src/
RUN dotnet publish src/Teager.Dsh.Api/Teager.Dsh.Api.csproj -c Release -o /app/publish --no-restore

# ── 2) Node.js 运行时来源（用于承载 dsh CLI） ──
FROM node:22-bookworm-slim AS node

# ── 3) 运行镜像 ──
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ARG NPM_REGISTRY
ARG DSH_VERSION
# 从 node 镜像拷贝 node 与 npm，再全局安装 dsh（Agent Runtime）与 pnpm（dsh 插件管理依赖）
COPY --from=node /usr/local/bin/node /usr/local/bin/node
COPY --from=node /usr/local/lib/node_modules /usr/local/lib/node_modules
RUN set -eux; \
    ln -sf ../lib/node_modules/npm/bin/npm-cli.js /usr/local/bin/npm; \
    ln -sf ../lib/node_modules/npm/bin/npx-cli.js /usr/local/bin/npx; \
    npm config set update-notifier false; \
    npm install -g --registry "${NPM_REGISTRY}" --no-fund --no-audit "@deepseek-ai/dsh@${DSH_VERSION}" pnpm@11; \
    dsh --version; \
    apt-get update; apt-get install -y --no-install-recommends curl ca-certificates git tzdata; rm -rf /var/lib/apt/lists/*; \
    npm cache clean --force

WORKDIR /app
COPY --from=build /app/publish ./
COPY docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh
RUN chmod +x /usr/local/bin/docker-entrypoint.sh && mkdir -p /app/data /app/logs /app/cache

ENV TZ=Asia/Shanghai \
    ASPNETCORE_URLS=http://+:5600 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_gcServer=0 \
    NODE_NO_WARNINGS=1 \
    Dsh__Storage__DataDir=/app/data \
    Dsh__Storage__LogDir=/app/logs \
    Dsh__Storage__CacheDir=/app/cache \
    Dsh__Runtime__DshExecutable=dsh

EXPOSE 5600
HEALTHCHECK --interval=30s --timeout=5s --start-period=40s --retries=3 CMD curl -fsS http://127.0.0.1:5600/health/live || exit 1
ENTRYPOINT ["/usr/local/bin/docker-entrypoint.sh"]
CMD ["dotnet", "Teager.Dsh.Api.dll"]
