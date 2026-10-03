# DSH 平台部署说明（Teager_DevOps / Docker）

平台由 **前端（Nginx 静态站 + `/api` 反代）** 与 **后端（ASP.NET Core 10 + Node.js + `@deepseek-ai/dsh`）** 两个镜像组成，
数据库使用 **SQL Server**。两者都写好了 Dockerfile，并提供 docker compose 方案：

| 文件 | 用途 |
|---|---|
| `backend/Dockerfile` · `backend/docker-entrypoint.sh` | 后端镜像（API 监听 **5600**，非 root 运行） |
| `frontend/Dockerfile` · `frontend/nginx/*` | 前端镜像（Nginx 监听 **5680**，已支持 SSE 流式与 WebSocket 终端） |
| `backend/docker-compose.yml` | **Teager_DevOps** 部署后端时使用的 Compose（镜像变量 `TEAGER_IMAGE_api`） |
| `deploy/docker-compose.yml` + `deploy/.env.example` | 一体化部署（API + Web，可选自带 SQL Server），适合自建 / 联调 |

```
浏览器 / SDK ──► Nginx(5680) ──/api──► API(5600) ──► SQL Server（元数据 / 权限 / 会话索引 / 审计）
                                          │
                                          ├──► /app/data   Workspace 文件、共享空间、各 Pool 独立的 DSH_HOME、快照   ← 数据卷（必须持久化并备份）
                                          ├──► /app/logs   应用日志（按天滚动）+ Agent 运行事件日志（JSONL，不入库）  ← 日志卷
                                          └──► /app/cache  上传 / 打包临时文件、dsh 的 HOME                       ← 缓存卷（可清空）
```

> 设计原则：**数据库只存元数据**（用户、Pool、权限、会话索引、审计摘要）。Workspace 文件、共享空间、Agent 的思考 / 工具调用等详细事件、
> 应用日志全部放在宿主机挂载目录里，不会撑大数据库，也不会写进容器可写层。

---

## 1. 上线前准备

1. **SQL Server 2017+** 已建空库（默认库名 `DshPlatform`），账号具备建表权限。程序启动时自动执行 EF Core 迁移建表（`DSH_AUTO_MIGRATE=true`）。
   ```sql
   CREATE DATABASE DshPlatform;
   ```
2. **宿主机目录**（建议先建好并确认不在 Docker 数据盘 `/var/lib/docker` 上；容器入口会把属主调整为运行用户）：
   ```bash
   sudo mkdir -p /var/lib/dsh/data /var/log/dsh /var/cache/dsh /var/log/dsh/nginx /var/cache/dsh/nginx
   ```
3. **HTTPS 反代**：生产环境 Cookie 默认按请求协议决定是否带 `Secure`。请在 Nginx 前面加 HTTPS（Caddy / Nginx / 网关），并保证透传 `X-Forwarded-Proto`。
   纯 HTTP 内网访问时可设置 `DSH_COOKIE_SECURE=false`。
4. **模型凭据**：真实 DSH Runtime 需要模型 API Key（如 `DEEPSEEK_API_KEY`）。三种方式（优先级从低到高）：
   - 平台级默认：环境变量 `DEEPSEEK_API_KEY`（只注入 Runtime，不对前端暴露）；
   - 组织密钥：Web →“平台配置 → 组织密钥”；
   - Pool / Workspace 密钥：对应页面的“密钥”标签（优先级 Workspace > Pool > 组织 > 平台默认）。

## 2. Teager_DevOps 中创建两个项目

两个项目指向**同一 Git 仓库**，构建上下文都是子目录，**不要勾选「仓库根 context」**。

### 2.1 后端项目

| 表单项 | 填写 |
|---|---|
| 名称 | DSH 平台后端 |
| 项目类型 | DotNet（.NET 版本 10.0） |
| 服务目录 / Dockerfile | `backend` / `Dockerfile` |
| 部署策略 | 多服务 Compose（也可只有一个服务） |
| Compose 文件白名单 | `backend/docker-compose.yml`（改完需重新构建） |
| 服务名 | `api`（与 compose 里的 `${TEAGER_IMAGE_api}` 对应） |
| 镜像路径 | 例如 `dsh/api` |

**部署**：新建部署 → 选后端项目与构建版本 → Compose 文件 `backend/docker-compose.yml`

| 目标字段 | 建议 | 说明 |
|---|---|---|
| 栈标识 | `dsh-backend` | 平台记账用 |
| 部署路径 | `/opt/apps/dsh-backend` | compose 与 `.env` 落盘目录 |
| 健康检查 | `http://127.0.0.1:5600/health/ready` | `live` 只看进程，`ready` 会检查数据库连通 |
| 环境变量 | 见第 3 节 | 写入目标机 `.env`，覆盖 compose 默认值 |

### 2.2 前端项目（单镜像）

| 表单项 | 填写 |
|---|---|
| 项目类型 | Vue |
| 子项目路径 / Dockerfile | `frontend` / `Dockerfile` |
| 部署策略 | 单镜像 docker run |
| 镜像路径 | 例如 `dsh/web` |

**部署目标**（建议先部署后端栈，再部署前端）：

| 目标字段 | 建议 | 说明 |
|---|---|---|
| 容器名 | `dsh-web` | |
| 宿主端口 → 容器端口 | `5680` → `5680` | |
| Docker 网络 | `dsh` | 必须与后端 `DSH_NETWORK` 一致，才能通过服务名 `api` 访问 |
| 健康检查 | `http://127.0.0.1:5680/healthz` | |

环境变量：

| 变量 | 默认值 | 说明 |
|---|---|---|
| `API_UPSTREAM` | `http://api:5600` | Nginx 把 `/api/` 反代到此地址；若前端不在后端网络内，改成 `http://<主机IP>:5600` |
| `MAX_BODY_SIZE` | `512m` | 上传体积上限（需 ≥ 后端 `DSH_MAX_UPLOAD_MB`） |

卷挂载（不要只读）：

| 宿主机路径 | 容器路径 | 说明 |
|---|---|---|
| `/var/log/dsh/nginx` | `/var/log/nginx` | access / error 日志 |
| `/var/cache/dsh/nginx` | `/var/cache/nginx` | 临时文件 / 缓存 |

## 3. 环境变量（后端）

完整示例见 `deploy/.env.example`。同一组变量也可直接写在 Teager_DevOps 的部署环境变量里。

| 变量 | 默认值 | 必填 | 说明 |
|---|---|---|---|
| `DSH_SQL` | `Server=host.docker.internal,1433;Database=DshPlatform;User ID=sa;Password=ChangeMe_123;TrustServerCertificate=True;MultipleActiveResultSets=true` | ✅ | SQL Server 连接串；库在宿主机用 `host.docker.internal`，在别的机器则写其地址 |
| `DSH_MASTER_KEY` | — | ✅ | 加密存储 Secret 的主密钥（长随机串）。**丢失后已存密钥无法解密**，请备份 |
| `DSH_ADMIN_USERNAME` / `DSH_ADMIN_PASSWORD` | `admin` / 空 | 首次 | 数据库中没有管理员时创建首个管理员；密码 ≥ 8 位且含字母与数字。建好后可清空 |
| `DSH_AUTO_MIGRATE` | `true` | | 启动时自动迁移建表 |
| `DSH_DATA_DIR` / `DSH_LOG_DIR` / `DSH_CACHE_DIR` | `/var/lib/dsh/data` / `/var/log/dsh` / `/var/cache/dsh` | | **宿主机**路径（compose 绑定挂载），容器内固定为 `/app/data` `/app/logs` `/app/cache` |
| `DSH_PUBLIC_URL` | 空 | | 对外访问基址（仅用于 SDK 示例展示） |
| `DSH_COOKIE_SECURE` | `auto` | | `auto` / `true` / `false` |
| `DSH_DEFAULT_PROVIDER` | `DeepSeekHarness` | | 新建 Pool 默认 Provider；`Mock` 为模拟运行时（仅演示 / 联调） |
| `DEEPSEEK_API_KEY` | 空 | | 平台级默认模型密钥（优先级最低） |
| `DSH_TERMINAL_ENABLED` | `false` | | 是否启用 Workspace 终端（见第 7 节安全提示） |
| `DSH_RUN_TIMEOUT_MINUTES` | `30` | | 单次 Agent 运行最长分钟数 |
| `DSH_MAX_CONCURRENT_RUNS` | `4` | | 每个 Pool 并发运行数（Pool 策略可再收紧） |
| `DSH_RUNTIME_IDLE_MINUTES` / `DSH_RUNTIME_AUTOSTOP_MINUTES` | `5` / `60` | | Runtime 空闲降级 / 自动停止时间 |
| `DSH_SANDBOX_MODE` | `workspace-write` | | 平台默认沙箱：`read-only` / `workspace-write` / `danger-full-access` |
| `DSH_MAX_WORKSPACE_MB` | `5120` | | 单 Workspace 私有文件默认配额 |
| `DSH_MAX_UPLOAD_MB` | `200` | | 单文件上传上限 |
| `DSH_RATE_LIMIT_PER_MINUTE` / `DSH_LOGIN_PER_MINUTE` | `1200` / `10` | | 每主体每分钟请求上限 / 每 IP 每分钟登录尝试上限 |
| `DSH_LOG_LEVEL` | `Information` | | 应用日志级别 |
| `DSH_DEMO_DATA` | `false` | | 写入演示数据（集团/编辑部/市场部，用户 a~e，密码 `Demo@1234`）。**生产保持 false** |
| `DSH_API_PORT` / `DSH_NETWORK` | `5600` / `dsh` | | 宿主映射端口 / 固定 Docker 网络名 |
| `NPM_REGISTRY` / `DSH_VERSION` | `https://registry.npmmirror.com` / `latest` | | **构建参数**：安装 dsh 的 npm 源与版本 |

> 配置项也可以用 ASP.NET 的原生写法覆盖（`Dsh__Runtime__IdleMinutes` 之类，节结构见 `backend/src/Teager.Dsh.Api/appsettings.json`）。

## 4. 卷挂载（后端）

默认使用**宿主机绑定挂载**而不是 Docker named volume（named volume 仍在 `/var/lib/docker`）：

| 用途 | 宿主机路径（变量） | 容器路径 | 内容 | 备份 |
|---|---|---|---|---|
| 数据 | `DSH_DATA_DIR` | `/app/data` | `workspaces/`、`shared/`、`pools/<id>/dsh_home`、`snapshots/`、`versions/` | **必须** |
| 日志 | `DSH_LOG_DIR` | `/app/logs` | `app/dsh-YYYYMMDD.log`（保留 14 天）、`runs/YYYYMM/<sessionId>.jsonl`（Agent 事件详情） | 建议 |
| 缓存 | `DSH_CACHE_DIR` | `/app/cache` | `tmp/`、`home/`（dsh / npm 的 HOME） | 否 |

同时 compose 为容器 stdout 设置了 Docker json-file 限额（`max-size=20m`、`max-file=3`），避免 `docker logs` 撑满磁盘。

### 换地方安置

Docker 数据盘空间紧张时，**不要改容器内路径**，只改宿主机一侧（在部署环境变量里覆盖）：

```text
DSH_DATA_DIR=/data/dsh/data
DSH_LOG_DIR=/data/dsh/logs
DSH_CACHE_DIR=/data/dsh/cache
```

迁移数据时先停容器，再把旧目录整体移动（`data` 保持完整目录树，包括每个 Pool 的 `dsh_home`）。

## 5. 一体化部署（deploy/docker-compose.yml）

```bash
cd deploy
cp .env.example .env        # 编辑：DSH_MASTER_KEY、DSH_ADMIN_PASSWORD、MSSQL_SA_PASSWORD
docker compose --profile with-db up -d --build     # 使用自带 SQL Server
# 或使用外部 SQL Server：在 .env 配置 DSH_SQL，然后
docker compose up -d --build
```

访问 `http://<主机>:5680`，用 `DSH_ADMIN_USERNAME / DSH_ADMIN_PASSWORD` 登录。自带 SQL Server 的数据目录 `DSH_MSSQL_DIR` 需要对 uid `10001` 可写。

## 6. 访问与核验

| 入口 | 默认地址 | 用途 |
|---|---|---|
| Web | `http://<主机>:5680` | 浏览器（统一入口） |
| API | `http://<主机>:5600/api/v1/...` | SDK / API（Bearer Token） |
| 健康 | `http://<主机>:5600/health/live`、`/health/ready` | 存活 / 就绪（含数据库） |

登录后建议按顺序初始化：**用户管理**（创建用户 / 服务账号）→ **Pool 管理**（建树、加成员）→ **平台配置**（模型 / Skill / MCP / 密钥）→ **共享空间**（建空间、授权）→ **Workspace**。

## 7. Runtime（DSH）与安全说明

- **每个 Pool 一个独立 `DSH_HOME`**（`/app/data/pools/<poolId>/dsh_home`）：会话、凭据、插件互不混用。Workspace 根目录同时是 Agent 的当前目录：
  `files/`（私有文件）、`shared/<挂载名>/`（共享空间链接）、`.dsh/skills/`（平台生成的 Skill）、`AGENTS.md`（平台生成的目录 / 权限说明）。
- **Runtime 进程**：每次消息启动一个 `dsh headless --json` 子进程并通过 `--session-id` 续接；子进程环境变量经**白名单清洗**，不会继承 API 的数据库连接串与主密钥。
- **共享空间 RO 的强制方式**：API 文件读写严格按权限拦截；Agent 直接在文件系统里的改动无法做文件级只读，平台会在每次运行前后对比挂载目录清单，
  修改了 RO 挂载即写入 `sharedspace.ro_violation` 审计并在工作台告警。需要更强隔离时，请把沙箱模式设为 `read-only`，或在宿主机层面使用只读 bind mount（后续多节点版本）。
- **终端**等同于容器内 shell，默认关闭（`DSH_TERMINAL_ENABLED=false`）。开启前请知悉：终端进程与 API 同容器同用户；平台已清洗其环境变量，
  但仍建议仅对受信任的 Pool 通过策略（`allowTerminal`）放开，并保留审计。
- **审批（Approvals）**：DSH headless 模式没有交互式审批通道，工具调用由沙箱策略（`DSH_PERMISSION_MODE`）自动放行 / 限制；工作台“告警”页展示安全类告警。
- 容器以非 root 用户 `app` 运行（入口脚本先修正挂载目录属主再降权）。

## 8. 备份、升级与排障

- **备份**：数据库（常规 SQL Server 备份）+ `DSH_DATA_DIR`（整个目录）+ `DSH_MASTER_KEY`（安全保管）。日志与缓存目录不需要备份。
- **升级**：在 Teager_DevOps 重新构建并部署新版本即可；启动时自动迁移数据库；数据目录与配置不受镜像更换影响（**Runtime 与数据分离**）。
- **升级 dsh**：重新构建镜像时指定 `--build-arg DSH_VERSION=<版本>`；各 Pool 的 `dsh_home` 保留，会话可继续。
- **常见问题**
  - 登录后立刻掉线：HTTPS 反代下未透传 `X-Forwarded-Proto` 导致 Cookie 属性不匹配，或纯 HTTP 下未设置 `DSH_COOKIE_SECURE=false`。
  - 流式输出卡顿 / 终端连不上：外层反代需关闭对 `/api/` 的缓冲并允许 WebSocket Upgrade（本仓库的 Nginx 配置已处理，如再叠加网关请同样配置，且保留 `Host` 头含端口）。
  - Runtime 启动失败：在“运行环境 / Pool → Runtime”查看错误；确认镜像内 `dsh --version` 可执行、模型密钥已配置。
  - 启动报缺少管理员：设置 `DSH_ADMIN_PASSWORD` 后重启。
  - Windows 本机开发运行真实 DSH：把 `Dsh__Runtime__DshExecutable` 指向 `npm-global\dsh.cmd`，并用 `Dsh__Runtime__SeedHome` 指向已有 DSH_HOME 以复制凭据；沙箱在 Windows 上可能需要 `danger-full-access`。

## 9. 本机构建验证（可选）

```powershell
docker build -t dsh-api:local ./backend
docker build -t dsh-web:local ./frontend
cd deploy; docker compose --profile with-db up -d --build
# 验证完成后务必关闭本机验证容器：
docker compose --profile with-db down
```
