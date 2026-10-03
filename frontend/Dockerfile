# DSH 平台前端镜像（Vue 构建 + Nginx）。
# Teager_DevOps：项目类型 Vue，单镜像 docker run；服务目录 frontend，Dockerfile 为 Dockerfile，不要勾选「仓库根 context」。
# 容器监听 5680；/api 反代到 API_UPSTREAM（默认 http://api:5600，需与后端 compose 处于同一 Docker 网络 dsh），
# 已针对 SSE（关闭缓冲）与 WebSocket（终端）做了代理配置。
# 日志 / 缓存请挂到宿主机：/var/log/nginx、/var/cache/nginx。

FROM node:22-alpine AS build
WORKDIR /app
ARG NPM_REGISTRY=https://registry.npmmirror.com
COPY package*.json ./
RUN npm ci --no-audit --no-fund --registry "${NPM_REGISTRY}"
COPY . ./
RUN npm run build

FROM nginx:1.27-alpine
COPY nginx/default.conf.template /etc/nginx/templates/default.conf.template
COPY nginx/00-prepare-dirs.sh /docker-entrypoint.d/00-prepare-dirs.sh
RUN chmod +x /docker-entrypoint.d/00-prepare-dirs.sh
COPY --from=build /app/dist /usr/share/nginx/html
ENV API_UPSTREAM=http://api:5600 \
    MAX_BODY_SIZE=512m \
    NGINX_ENVSUBST_FILTER=API_UPSTREAM|MAX_BODY_SIZE
EXPOSE 5680
HEALTHCHECK --interval=30s --timeout=5s --retries=3 CMD wget -qO- http://127.0.0.1:5680/healthz || exit 1
