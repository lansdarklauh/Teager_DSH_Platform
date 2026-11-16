# 仅使用分支部署说明

代码分支：`仅使用`。`用户管理` 分支保留改造前基线。当前修改留在工作区供审阅，没有推送。此次验证没有重启现有 5600/3680 服务，也没有向现有业务数据库迁移。

## 运行前提

当前支持单组织、单 API 实例；数据库独占租约阻止第二实例接管同一数据库。后端使用 SQL Server，个人执行环境需要 Linux Docker 引擎、FUSE、iptables 和构建好的 `teager-dsh-sandbox:usage-only` 镜像。Windows 开发机可使用 Docker Desktop Linux 容器。

推荐本期以原生 .NET API 进程连接本机 Docker：API 必须能调用 Docker CLI、访问自己的持久化 DataDir，并访问 Docker 发布到宿主回环地址的预览入口。普通用户没有 Docker socket、宿主目录或后台凭据的访问权。

仓库原有 compose API 镜像没有自动补齐 Docker CLI/socket 和宿主回环预览路径。不能只把 Sandbox.Enabled 改为 true 就认定该 compose 部署具备执行能力；容器化 API 需要额外配置 Docker CLI、daemon、HostDataDir 路径映射，以及可访问宿主回环入口的网络。此期真实验收使用原生 API + Docker Desktop。

## 安装和配置

1. 在仓库根目录执行 `docker build -t teager-dsh-sandbox:usage-only deploy/sandbox`。
2. 构建后端和前端，备份业务数据库、数据目录、日志和加密主密钥。按正常发布流程应用 7 个新增 EF 迁移（含 `20261004112938_ProjectMessageReferences`），然后启动新版本。不要同时启动两个新版本实例。
3. 将 [配置示例](../deploy/appsettings.UsageOnly.example.json) 中的 Dsh 节合并到实际部署配置，或设置对应 `Dsh__...` 环境变量。该 example 文件不会自动加载。保留实际数据库连接和主密钥，示例没有包含任何凭据。
4. 设置 Sandbox.Enabled=true、Runtime.TerminalEnabled=true，平台和 Pool 策略允许终端。未满足条件时界面显示环境未就绪，普通用户不会回退到宿主 shell。
5. 管理员配置启用的模型、模型 API 地址和加密密钥。测试的 Mock 对话不需要商业密钥；真实 DSH 运行需要正确的上游配置。
6. 配置独立 HTTPS 预览域名，例如 `https://{execution}.preview.example.net/`，通配 DNS/TLS 将其指向 API；反向代理保留 Host，转发 HTTP 与 WebSocket。预览域名必须与平台页面来源不同。生产不允许普通 HTTP 预览；`.localhost` HTTP 仅供本机开发。
7. 配置 Platform.PublicBaseUrl、CookieSecure=true、明确的 TrustedProxies。仅把实际受信代理的 IP 放入列表。

新建普通账号自动获得独立个人 Workspace。既有普通用户由启动迁移受控补齐；管理员可以在用户页重新指派未归属的 Workspace，活动任务/应用期间不能切换。个人空间禁用 Pool 成员继承；归档空间不会由用户登录擅自替换为另一个空间。

## 用户使用边界

用户登录后进入 `/w/<自己的工作空间>`，初始工作台有项目、对话、文件面板；终端、命令、应用入口位于工作台。个人 `/workspace/files` 和 `/home/user` 持久保存，项目删除只移除项目目录索引，文件仍可在文件模块管理。安装依赖使用用户目录：例如 `python3 -m pip install --target /home/user/python-dependencies ...`，或 `npm install --prefix /home/user/node-dependencies ...`。配置好 PYTHONPATH/require 路径后，后续命令仍可使用。

默认外网名单为空。管理员可以给 NetworkAllowedHosts 配置必要的 HTTPS 包仓库域名，包含下载时实际使用的重定向域名。代理仅接受名单内公开 IPv4 的 443 端口，并要求 TLS SNI 与 CONNECT 域名一致；私网、宿主控制面和未列域名不可访问。关闭平台/Pool AllowNetwork 时不开放包代理。依赖离线安装不需要开放外网。

个人环境使用非 root 用户、只读系统目录、独立进程/网络空间、受限能力、CPU/内存/PID/时长/文件数配额；文件和 home 共用存储上限。共享空间按自身配额与硬上限取较小值，RO 授权使用实际只读挂载，USER/RW 必须由管理员明确授予。编辑者只能编辑共享文件或 Pool 名称/描述，不能管理资源。

平台文件操作暂停用户进程而保留可信 FUSE/代理，避免链接交换竞态及整容器暂停导致的文件系统卡住。共享授权、Token 和账号撤销会取消任务或停止容器。应用必须声明端口；每次预览生成不同来源的短期地址，转发时清除平台 Cookie/Authorization，停止应用后地址失效。

隔离 Agent 使用平台指定模型代理与托管 Skill。上游密钥位于 chroot 外，用户环境只有本次代理的局部凭据。当前隔离 Agent 不启动任意平台 MCP/custom plugin；这类插件需要另行提供受控代理/镜像集成，不能通过用户编辑托管配置获得平台密钥。用户自行运行的开发工具与 CLI 使用自己的环境和凭据，仍受同一隔离边界约束。

## 模型额度及运维

管理员在“平台配置 → 模型 → 新增/编辑”中设置单次任务预算（`params.taskBudget`）及上下文大小（`params.contextWindow`）。两项默认为 0。预算 0 不附加平台累计预算限制；上下文 0 不覆盖引擎容量，优先使用运行镜像中的模型目录。自定义/新发布模型没有目录元数据时采用 DSH 自身的默认容量，不能声称自动知道供应商真实上限，应由管理员填写供应商公布的上下文大小；上游接口始终执行自身的真实上限。模型原生容量限制单次请求，不定义整个 Agent 任务的累计消耗。

正数任务预算仍按请求 UTF-8 字节数加预留输出额度累计，是保守的预算单位，不等同于供应商账单 Token。历史 `Sandbox.ModelBudgetCredits` 兼容保留，但已不参与任务预算；请将所需限额移到模型配置中。模型配置保存后对下一次任务生效，无需重新启动 API，运行中的任务保持已经预留的预算。默认模型、会话所选模型和本次显式选择的模型均使用各自的有效配置。

代理不再固定将输出截到 4096：可识别的模型使用目录输出容量，其他模型沿用 DSH/上游设置。显式上下文大小传入 DSH 模型配置，用于引擎上下文管理；它不是发送给 OpenAI 兼容接口的非标准参数。默认模式仍保留每次任务 100 次模型调用、16 MiB 单请求传输上限、并发、超时、文件及网络隔离保护。调用次数和预算不足返回不同的 JSON 错误，预算不足会显示中文处理说明。

配置 MaxTokensPerDay 后，任务先原子预留额度，已完成任务的预留量保守计入当日准入；预算 0 时最多预留当天剩余额度，因此不会绕过每日限制。用量页仍显示报告的实际 Token，可能早于实际 Token 显示值触及准入上限。没有供应商计量对账时不能把该额度当作财务计费。

FileVersionsKeep 控制单文件保留数量，历史版本与快照分别受容量上限约束；事件日志与数据目录仍需部署磁盘监控和备份。慢 SSE 客户端会断开并按 seq 重连；历史回放目前仍扫描日志文件，超长日志可进一步做分段索引。日常维护公平轮询空间，清理过期资源、事件缓存、历史版本和已结束容器。

验收脚本及命令见 [验证记录](usage-only-verification.md)。测试脚本自动删除自己的临时数据库和容器；带 --keep-ui 时创建 `.cache/usage-validation/stop-ui` 文件即可结束临时 UI/API 服务。

本地页面验收可执行 `python -X utf8 backend/tests/e2e/usage_only.py --sandbox --keep-ui`。脚本输出本轮随机前端/API 地址，前端使用 Vite 代理，需正常登录：临时普通账号 `alice / Validation_User123`，管理员 `admin / Validation_Admin123`。这些账号仅存在于随机测试库。不要使用现有业务账号或将该入口发布公网；页面验收结束后用 stop-ui 关闭本轮服务。前端入口的 HTTP/SSE/WebSocket 检查可在服务存活时执行 `python -X utf8 backend/tests/e2e/frontend_proxy_checks.py`，它不代替真实浏览器验收。

## DNS 环境检查

模型代理支持管理员明确指定的 VPN fake-IP 域名列表 `Dsh:Sandbox:ModelFakeIpHosts`。默认列表为空；指定域名时只额外允许 198.18.0.0/15 内的 VPN 映射，仍校验 HTTPS/TLS 域名，且只供可信模型代理访问。该设置不开放用户进程或包代理的网络，也不允许 localhost、RFC1918、链路本地地址。2026-10-10 用户已授权用 MiniMax 的该映射做模型验收，开发配置仅列入 `api.minimaxi.com`；生产示例仍为空列表。无此需求的部署应使用正常公网 DNS。

## 统一启用个人环境（2026-10-10）

主配置默认启用 Sandbox 和 Terminal，开发配置也默认使用 DeepSeekHarness。隔离容器在用户实际连接终端、启动命令或发送任务时创建；账号开通后不需要管理员逐个启用，也不会为所有空闲账号提前启动容器。

启动时 `Runtime.SynchronizePersonalPoolProvider=true` 将系统生成的个人 Pool 同步为 DefaultProvider；清除旧引擎的会话标识，保留消息、个人文件、模型和密钥配置。管理员手动创建的 Pool 保持其所选引擎。若需要维护 Mock 测试部署，可以将 DefaultProvider 设为 Mock 或关闭同步。

模型的 `apiKeyEnv` 必须填写同层级密钥名称，例如 MINIMAX_API_KEY。启动自动将旧配置里误填在该字段的 `sk-...` 密钥转入同层级 AES-GCM 加密存储，保留模型配置和密钥值；接口拒绝新提交的错误格式。迁移时须保持原 MasterKey，避免已有密文无法解密。

这些后端选项在服务启动时加载。已运行的旧 API 不会因编辑配置自动切换引擎；按原启动方式停止、重新编译并启动 API 后生效。不要启动第二个实例连接同一数据库；启动租约会拒绝这种情况。

包代理和模型代理均要求上游域名解析为公网地址。当前开发机检测到 PyPI/npm 域名返回 198.18.x.x，因此在线下载被拒绝；显式给独立容器设置 1.1.1.1 也未改变结果。部署应在受信 DNS/VPN 管理配置中排除仓库域名的 fake-IP 处理并重新验收，不要把保留地址加入允许名单或关闭 SSRF 检查。运行 `python -X utf8 backend/tests/e2e/network_probe.py` 可验证实际 pip/npm 下载及网络隔离。

若要区分镜像下载功能和当前默认 DNS 故障，可执行 `python -X utf8 backend/tests/e2e/network_probe.py --public-dns-snapshot`。该参数只为本次临时探针容器使用经 HTTPS 查询并验证的公网 IPv4，不修改部署配置；正向下载和负向隔离已在该模式通过。正式部署仍应修复默认可信 DNS 并通过不带参数的检查，不应把测试时获取的 CDN 地址永久写入生产配置。
