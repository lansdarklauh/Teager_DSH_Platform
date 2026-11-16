# 仅使用分支交付与验证记录

最新验证日期：2026-10-08。基线 `1757135`，当前分支 `仅使用`，另有基线分支 `用户管理`。修改没有提交或推送。以下补充记录保留排查历史；当前浏览器状态以文末“真实 Edge 页面验收”记录为准。

## 已交付功能

| 用户需求 | 最终行为 | 验证依据 |
| --- | --- | --- |
| 普通用户初始工作台 | 登录返回个人 defaultWorkspaceId/landingPath；首页、管理路由、他人工作台都转入自己的 `/w/<id>`；工作台包含项目、对话、默认打开的文件面板 | 真实 Edge 登录、直接导航和页面截图；真实 API、路由测试、类型检查和构建 |
| 个人开发环境 | 项目索引、UTF-8 文件管理、会话、文件快照、命令、真实交互 PTY、Python/npm 用户依赖、应用及进程管理 | SQL 回归、真实 API、生产沙箱镜像与 HTTP/WebSocket 验收 |
| 对话引用项目 | 输入 `@` 搜索当前工作空间项目或点击“引用到对话”；标签可移除；Agent 获得目录/描述上下文，历史、复制和导出保留引用；每条最多 8 个，拒绝跨空间引用 | 真实 Edge 中文搜索、键盘选择、发送、流式回复和刷新历史；API/SDK、SQL 回归、Vue 组件测试 |
| 管理员管理资源 | 普通用户不能创建/克隆空间或 Pool，不能管理授权、成员、配置、运行环境或生命周期；旧 Owner/Admin 角色不能扩权 | 权限回归及真实 403/404 API 响应 |
| 共享使用与编辑 | 有访问权默认 RO，无授权不可见；管理员显式 USER/RW 可编辑文件；实际 RO 挂载拒绝命令写入；授权撤销会停机 | API 与实际容器文件写入、撤权验收 |
| Pool 指派编辑 | 管理员可授予名称/描述编辑权；不能因此管理成员、策略、Runtime 或下级资源 | 管理员授权及普通用户正/负向 API |
| API Token 和开发说明 | 个人 Token 创建、元信息查看、吊销；明文只在创建时显示；个人空间和共享白名单约束；Python/curl、文件、命令、应用 API 说明 | Token 回归、受限管理员 Token 测试、Python SDK 真实调用及断线契约测试 |
| 越界与资源限制 | 非 root、只读系统、个人目录、受限网络/端口、平台凭据代理、CPU/内存/PID/时长/文件数/存储上限 | 实际容器、Linux FUSE 文件系统、DSH CLI、TLS ClientHello 验证 |

## 业务问题修复对应表

编号沿用 [原分析方案](usage-only-permission-plan.md)；方案中的基线问题描述不代表这些问题仍未修复。

| 编号 | 本次实现 |
| --- | --- |
| F01–F03 | 保护区域根目录；统一共享 area 的 Scope；普通/服务/管理员 Token 的资源和能力白名单覆盖读取、挂载、运行、统计和管理 |
| F04–F05 | 执行前刷新账号/Token/空间权限；运行期间检查撤权；使用容器隔离、真实只读共享和凭据代理，不依赖事后检测阻止写入 |
| F06 | 不支持/无效的 Authorization 不回退 Cookie；CSRF 豁免依据实际 Token 认证；限流按已验证用户或 IP |
| F07 | 数据库准入锁、会话运行唯一索引、原子并发/每日预算预留、requestId 幂等与不同内容冲突 |
| F08 | 每次运行独立 patch/控制目录；普通 DSH 使用独立容器、不可写托管配置及独立模型代理 |
| F09 | 资源生命周期与准入互斥；活动任务/终端/应用阻止删除、恢复、转移或重新指派；转移清理旧挂载和运行关联 |
| F10 | 删除前同卷暂存与日志恢复；数据库提交后清理，失败保留清理日志；会话和运行上下文也采用暂存；快照先完整解压检查再替换并支持失败回滚 |
| F11 | 管理员可以查看、恢复归档共享空间；Workspace 恢复验证目标 Pool 状态和数量配额 |
| F12 | API 覆盖写按增量计费；文件/home 共用硬配额；共享取自身/硬上限最小值；快照、版本、项目文件数配额；Linux 已打开但删除的文件仍占用额度 |
| F13 | keep=1 版本先读取再创建撤销版本；Linux 路径区分大小写；工作区根和链接检查 |
| F14 | 401 清理身份并回到登录页；刷新元数据或网络错误保留已有身份；登录后使用个人工作台落点 |
| F15 | 前端及 SDK 消息按游标读取完整历史；前端 SSE 按 seq 续接；SDK 续接、去重、过滤目标 runId |
| F16 | 统计/资源详情按可访问 Workspace 和 Token 白名单裁剪；无权共享解释和资源列表返回 404/不可见 |
| F17 | 全流程运行异常收尾、落库重试与周期孤儿记录恢复；Runtime 首次记录/启动/维护锁和状态检查 |
| F18 | 执行校验保存/请求的模型，失效明确拒绝；上级强制禁用不因关闭继承而恢复 |
| F19 | 维护公平游标；有界 SSE 订阅和慢客户端断开；空订阅/已删会话缓存回收；配置 SQL 只读取有效链；删除清理版本；忽略运行日志/数据 |

R01–R06 的处理：显式可信代理、实际隔离环境、数据库单实例租约、拒绝数字枚举、启动拒绝多组织、用户进程暂停与路径检查。维护时只暂停用户进程，可信 FUSE 和代理继续运行；修复了测试中发现的整容器暂停导致依赖安装卡住的问题。

## 验证命令和结果

所有测试服务使用独立随机端口，SQL 测试库使用随机名称，不迁移或清空现有业务数据库；不使用 Playwright。

```text
dotnet build backend/src/Teager.Dsh.Api/Teager.Dsh.Api.csproj --artifacts-path .cache/usage-validation/build --no-restore
dotnet test backend/tests/Teager.Dsh.Tests/Teager.Dsh.Tests.csproj --artifacts-path .cache/usage-validation/test-build --no-restore
npm run build -- --outDir ../.cache/usage-validation/frontend   # 在 frontend 目录
node frontend/tests/usage-routes.mjs
node frontend/tests/project-mentions.mjs
node frontend/tests/project-composer.mjs
python -X utf8 backend/tests/e2e/usage_only.py --sandbox
python -X utf8 backend/tests/e2e/filesystem_probe.py
python -X utf8 backend/tests/e2e/agent_contract.py
python -X utf8 backend/tests/e2e/sdk_contract.py
git diff --check
```

- API 构建：成功，0 错误、0 警告。
- .NET 回归：39/39 通过，包括并发 Workspace 配额、并发个人空间补齐、受限管理员 Token、服务账户显式权限、损坏快照保护、keep=1 版本恢复、活动应用生命周期保护、删除回滚和上下文清理保留个人文件，以及项目引用上下文、权限、历史和幂等检查。
- 前端类型检查/生产构建：通过；合并第三方依赖 chunk 消除循环依赖警告，保留约 1.7 MB vendor 包大小提示，可后续优化按需加载。
- 实际路由守卫离线检查：通过，覆盖普通首页/登录/管理员页/他人工作台落点，以及个人工作台、API 页、过期登录。
- API/容器最终集成结果：28/28 通过，脚本退出码 0；`.cache/usage-validation/api-checks.json` 已记录全部检查及 testProcessesClosed=true。包括 Python SDK 真实流式会话创建/读取/删除、项目引用 send/stream、1000 字中文描述持久化、复制/导出、非法引用、重试冲突和前端登录代理检查。
- 项目引用组件离线测试：执行实际 Vue 组件 setup，覆盖键盘选择与发送、Shift+Enter、中文输入法、Esc、空结果、重复/数量上限/禁用、切换空间的迟到响应、加载失败重试。此测试没有启动浏览器，不代替真实页面交互验收。
- Linux 配额探针：通过；先 unlink 再向已打开文件写 3 MB，2 MB 配额下实际返回 ENOSPC。
- 真实 DSH CLI 契约：通过；使用生产 supervisor、模型代理和实际 DSH 二进制，完成 bash 工具调用、uid=1000、个人文件写入、上游密钥不可读。该测试仅模拟上游 HTTPS 传输，不调用付费商业模型；没有把它当作真实供应商可用性验证。
- SDK 断线契约：通过；接续携带最近 seq，不重复输出、不混入旧运行，遇到目标 run_end 结束。
- TLS 域名检查：使用 Python ssl 产生真实 ClientHello；正确 SNI 通过，其他域名、截断和非 TLS 数据拒绝。
- UTF-8 与代码规模：变更文本全部可按 UTF-8 解码，没有替换字符；变更代码文件均未超过 2000 行。

## 仍需部署环境完成的验证

真实 Edge 页面已完成 28 项验收，包括登录后自己的工作台、项目引用、个人文件管理、命令/终端、应用预览、Token 和共享授权。文件上传的系统选择器因 computer-use 无法可靠确定 Windows 浏览器 URL 而中止；下载按钮及 Bob 实际登录页面尚未验收，不能声称所有按钮均已通过。跨用户隔离已有真实 API、路由和容器验证。

生产部署尚未执行。原 5600 API（PID 29244）和 3680 前端（PID 38472）保持运行；代码修改需要按 [部署说明](usage-only-deployment.md) 正常发布、应用迁移和启用隔离环境后才会出现在原服务。该过程不包含在测试进程替换中。

单组织/单实例是本期明确支持边界。隔离 Agent 的任意 MCP/custom plugin 接入、供应商计费对账、事件日志分段索引和全站按需依赖加载属于后续扩展；没有将这些未完成扩展标为已验证功能。生产 HTTPS 通配 DNS/证书/代理、默认包仓库 DNS 与商业上游服务应在实际部署环境做连通验收；可信公网 DNS 的临时探针已实际下载和导入 pip/npm 包，默认 DNS 仍返回非公网地址。

测试结束后临时 API/UI 服务、测试容器和随机数据库由脚本 finally 清理；保留 `.cache/usage-validation` 的日志与检查结果方便追溯。

最终清理核对：没有本次 dsh_usage_* 数据库、dsh-usage 测试容器、配额探针 volume 或 usage-validation 测试进程残留；本轮 .NET 用例新建的数据库已清理。SQL 中原先较早建立的 dsh_test_* 库未做额外删除。原 5600/3680 监听 PID 保持不变。

## 继续验证补充记录

本次重新启动随机端口/随机数据库的临时 API/UI，14 项核心 API/SDK 验收再次通过，结果保存至 `.cache/usage-validation/ui-api-checks.json`。原 23 项完整容器验收报告仍保存在 `api-checks.json`，不会由不带 --sandbox 的页面检查覆盖。

再次打开真实工作台仍被同一浏览器策略检查拒绝，未完成浏览器视觉及交互验收；没有尝试其他浏览器或绕过策略。

新增 `backend/tests/e2e/network_probe.py`：

- 真实生产沙箱中，未允许域名、回环 HTTPS、云元数据地址、用户绕过代理直接访问公网/元数据地址均拒绝，检查通过。
- 用户进程 `/proc/self/status` 的 CapEff 为 0；可信 supervisor 的能力没有保留在用户进程中，检查通过。
- 在线 Python/npm 下载正向验收没有通过。实际 Docker DNS 将 `pypi.org`、`files.pythonhosted.org`、`registry.npmjs.org` 分别解析为 `198.18.0.15`、`198.18.0.16`、`198.18.0.39`，均非公网地址；指定测试容器 DNS 为 1.1.1.1 后仍得到相同结果。生产代理按设计拒绝这类地址。错误日志保存至 `.cache/usage-validation/network-probe.log`。
- 已增加非公网 DNS 的明确 403 提示，不放宽私网/保留地址限制。部署需要让这些域名通过受信 DNS 返回可路由的公网 IPv4，或在现有 DNS/VPN 管理配置中排除其 fake-IP 处理。没有改动主机 DNS/VPN 或其他服务。
- 当前环境使用 `python -X utf8 backend/tests/e2e/network_probe.py --deny-only --expect-dns-denial` 验证拒绝和错误原因。DNS 环境恢复后需重新执行不带参数的脚本，只有 pip/npm 实际下载、导入和负向网络检查全部通过，才能把在线下载标为已验收。

临时 UI/API、随机数据库、网络探针容器和数据卷均已结束并清理。仍待外部环境恢复的事项明确为：浏览器策略检查，以及在线依赖下载的 DNS/网络条件。

## 项目引用补充验证

新增 `@项目` 与项目卡片“引用到对话”、消息引用标签、Agent 项目上下文、API `projectIds`、Python SDK `project_ids`、历史/复制/导出保留元数据。新建 `ProjectMessageReferences` 迁移使用 `nvarchar(max)` 存储引用，1000 字中文描述在真实 SQL/API 中往返一致。

完整 API/容器验收由 23 项增加为 27 项，全部通过。最后一次权限补强后又完成 18/18 核心 API/SDK 检查和 39/39 SQL 回归：旧 Token 缺少 `files:read` 时引用项目返回 403；跨用户、已移除、目录缺失、重复和超量引用不创建消息或任务。

实际组件逻辑离线测试、项目检索辅助函数测试、路由测试、SDK 断线测试、前后端构建均通过；EF 模型检查无未生成的变更。真实浏览器限制仍存在，没有把组件离线测试标为浏览器交互验收。

本轮随机 SQL 数据库和临时 API/UI/容器已清理，原 5600/3680 进程 PID 保持 29244/38472。变更文本共 120 个文件可用 UTF-8 解码，没有替换字符，变更代码文件未超过 2000 行；`git diff --check` 通过。

## 2026-10-08 本地服务复验

重新构建前后端并启动独立 API、前端和随机 SQL 测试库，不替换原 5600/3680 服务。完整 API/容器验收 28/28 通过，包含新增的实际前端代理登录与 Cookie 往返检查。

此前临时页面服务器仅支持 GET 且为浏览器注入已登录 Cookie，无法用于真实登录和按钮操作验收。本次改为 Vite 的生产构建预览代理，支持真实登录、HTTP 写请求、SSE 与 WebSocket。页面需自行登录，没有注入身份。

新增 `python -X utf8 backend/tests/e2e/frontend_proxy_checks.py`（在 `usage_only.py --sandbox --keep-ui` 存活时运行）：2/2 通过。通过前端地址真实创建会话、发送带项目引用的消息、读取 SSE 直到 Completed/run_end、确认历史元数据；通过前端 WebSocket 代理连接真实非 root PTY，验证输入/resize 和 Cookie/Origin。结果保存在 `.cache/usage-validation/frontend-proxy-checks.json`。该检查验证传输与鉴权，不属于浏览器页面视觉/点击验收。

组件、检索、路由离线检查和 `git diff --check` 再次通过。在线包下载正向探针仍失败，实际错误为 `403 Repository resolves to a non-public address; check trusted DNS and fake-IP settings`；拒绝探针与用户进程零能力检查通过，未放宽网络边界。

这次 computer-use 初始化、轻量重试及内核重新初始化后均报 `Computer Use native pipe is unavailable: failed to connect native pipe: 系统找不到指定的文件。 (os error 2)`。尚未到网站策略检查，不能把它归因于项目访问权限。插件状态查询也因工具后端连接失败未能取得结果；没有更改插件、系统或网站权限。真实浏览器验收仍待客户端自动化连接恢复。

临时入口使用 `http://127.0.0.1:63039`，API 为 `http://127.0.0.1:62147`，SQL 库为 `dsh_usage_704150d4c5b3`。本轮结束通过 stop-ui 通知脚本 finally 清理，入口随测试服务关闭。官方浏览器调用和权限入口见 [Browser](https://learn.chatgpt.com/docs/browser) 与 [Computer Use](https://learn.chatgpt.com/docs/computer-use)。

## 2026-10-08 Edge 连接与剩余测试复验

Edge 扩展已安装并启用，原生通信配置和程序文件存在；打开对应用户配置后，浏览器运行时实际发现 Edge。随后 session 命名和标签页列表操作均报 `failed to start codex app-server: 系统找不到指定的路径。 (os error 3)`。因此扩展连接已恢复，但浏览器控制后端尚未正常启动；没有打开平台页面，也没有把浏览器验收标为通过。具体缺失路径尚未确定，未修改浏览器插件、本机通信配置或客户端安装。

本轮重新启动独立随机服务/数据库，39/39 SQL 回归、28/28 API/容器验收、2/2 前端 HTTP/SSE/WebSocket 入口检查、Linux 打开后删除文件的硬配额探针及真实 DSH CLI 隔离契约均通过。CLI 契约仍模拟上游 HTTPS 响应，不属于商业模型供应商验收。

默认 DNS 的在线依赖下载再次失败，保留明确的非公网地址 403。新增 `python -X utf8 backend/tests/e2e/network_probe.py --public-dns-snapshot`：通过 [Google Public DNS 的 HTTPS JSON 接口](https://developers.google.com/speed/public-dns/docs/doh/json) 取得并验证公网 IPv4，仅为临时探针容器添加仓库 hosts 映射，未改变主机或平台 DNS。该模式中真实下载并导入 `six==1.17.0` 和 `is-number@7.0.0` 成功，非名单、私网、元数据、直接外连拒绝及用户进程零能力检查同时通过。

这证明生产沙箱镜像在可信公网解析条件下支持真实 pip/npm 在线安装；当前默认 DNS 环境仍未修复，不能将默认部署环境的在线下载标为通过。解析记录与日志分别保存在 `.cache/usage-validation/network-public-dns.json` 和 `network-public-dns-probe.log`，默认 DNS 错误继续保存在 `network-probe.log`。

剩余阻挡为真实浏览器控制后端启动错误及默认部署 DNS 条件。统一检查状态保存在 `.cache/usage-validation/latest-verification.json`。临时 API/UI、随机数据库及探针容器/volume 均按 finally 清理；原 5600/3680 服务保持运行。

## 浏览器启动路径根因与配置修复

客户端重开后仍复现错误。检查 Windows 客户端日志 `C:/Users/Traveldaily/AppData/Local/Codex/Logs/2026/10/08/codex-desktop-18a2a29a-af2e-4f8b-8289-c7f0641f0f24-51432-t0-i1-023506-0.log` 第 9 行，确认 `consumer=browser-use` 使用 `source=override`，可执行程序为 `C:/Nodejs/node_modules/@openai/codex/node_modules/@openai/codex-win32-x64/vendor/x86_64-pc-windows-msvc/bin/codex.exe`；该路径不存在。主客户端使用另一份实际存在的运行时程序，因此此前仅验证主程序 `--version` 不能证明浏览器启动路径有效。

失效覆盖存在于用户级环境变量 `CODEX_CLI_PATH` 和 `~/.codex/config.toml` 的 `mcp_servers.node_repl.env.CODEX_CLI_PATH`。两处覆盖已移除，保留其它 TOML 配置，并恢复客户端自动选择自身运行时。备份位于用户私有目录 `C:/Users/Traveldaily/.codex/backups/browser-cli-path-20261008-979070f6`，未放入项目或推送。修复后验证用户环境变量不存在、TOML 可解析且 UTF-8 完整；实际客户端程序的 `--version` 和 `app-server --help` 均成功。

当前客户端仍持有启动时读取的旧路径，需要再次完全退出并重新打开后验证浏览器控制调用。配置修复已完成，但尚未把真实页面验收标为通过。未修补原生通信组件，未改变浏览器或平台安全策略，也未终止现有客户端或平台服务。

## 2026-10-08 真实 Edge 页面验收

客户端重开后，浏览器控制后端启动恢复，使用客户端实际存在的运行时程序。通过 Edge 扩展及 Browser AX 操作真实页面，不使用 Playwright，不注入登录 Cookie。独立前端 51425、API 51404 和随机数据库 `dsh_usage_d3593ce077b8` 承载本次验收。

真实页面 28 项检查通过，逐项记录见 [浏览器检查清单](usage-only-browser-checks.json)。已验证：

- 错误密码拒绝；普通用户登录直接进入自己的工作台，显示自己的项目、对话与默认文件面板；管理路由及他人的工作台地址转入自己的空间。
- 中文 `@项目` 搜索、Enter 选择、Shift+Enter 换行、引用标签移除、项目卡片引用；发送后流式 Mock 回复包含项目目录，刷新后历史引用仍存在。
- 新建中文项目；项目文件入口；中文文件编辑保存并重新打开；目录创建、重命名和删除确认。
- 页面执行命令以 uid=1000 运行并写入个人文件；真实 PTY 输入命令并读取同一文件，断开后终端停止。
- 启动个人 HTTP 应用，在独立预览域名打开目录及个人文件，关闭预览并点击停止后状态成为 Stopped。
- API 开发说明包含当前个人空间和项目引用；个人 Token 创建、一次性明文展示、关闭和吊销。
- 普通用户共享列表没有创建入口，RO 文件页面没有写入或管理入口；管理员实际指派 USER/RW 后，普通用户可以保存共享文件。管理员登录显示资源与用户管理入口。
- 管理员工作空间指派时，后端拒绝选择其他用户已有空间；修复后前端候选项也排除该空间。显式 Pool 详情编辑用户可以保存描述，页面不提供成员、策略或 Runtime 管理操作。

本轮实际页面发现并修复了管理员指派候选项包含已被占用空间的问题；后端原先已拒绝该操作。还澄清了工作台引导和终端提示的个人文件/项目目录，避免用户按错误的 files/shared 相对路径开始任务。修复后的前端已完成类型检查和生产构建，并在真实页面复验。

截图保存在 `.cache/usage-validation/ui-proof/`：`initial-workbench.png`、`shared-readonly.png`、`admin-home.png`、`assignment-filter.png` 和 `app-preview.png`。截图不包含 Token 明文；GUI 对话使用 Mock，不代表商业模型调用验证。

上传按钮已点击，但 Windows 文件选择器验收被 computer-use 自动安全检查中止：`could not determine the current browser URL on Windows with enough confidence to enforce policy`。按工具要求停止本轮 UI 操作，没有绕过该检查。上传/下载按钮与 Bob 登录页面仍需补测；这不等于上传功能已判定失败，也不把 API/容器检查代替按钮验收。

完整容器套件首次遇到一次配额命令 60 秒超时，记录保存在 `.cache/usage-validation/quota-first-failure.json`；同项隔离复验约 12.8 秒通过，再执行完整套件 28/28 通过。未放宽超时或配额以让测试通过；该偶发慢启动需作为稳定性观察项保留。

本轮清理确认：停止并移除该随机库内的 4 个命令/终端/应用测试容器，通过 stop-ui 通知脚本 finally 退出，临时 API/UI 和数据库均已关闭；没有 dsh-usage 容器残留。原 5600/3680 服务 PID 仍为 29244/38472。系统文件选择器因工具中止未继续操作，用户的 Edge 和 Codex 进程保持运行。

## 2026-10-10 局域网 HTTP 消息发送修复

用户通过 `http://192.168.31.25:3680` 打开自己的工作台，发送“你好”后显示“发送失败”，会话仍为 0 条。提供的 `GET /api/v1/sessions/<id>/messages?after=0&limit=500` 是读取历史消息的接口，不是发送接口。

根因为前端 `sessionApi.send` 无条件调用 `crypto.randomUUID()` 创建幂等标识。在局域网 HTTP 的非安全上下文中，该方法不可用，前端在调用 POST 之前抛出 TypeError；错误处理又读取历史，因此可见历史 GET 和通用“发送失败”。之前 localhost 浏览器验收没有覆盖这一差异。规范说明参见 [MDN Crypto.randomUUID](https://developer.mozilla.org/en-US/docs/Web/API/Crypto/randomUUID)。

修复保留原生 randomUUID 路径，并在该方法不可用时通过 `crypto.getRandomValues` 生成符合 UUID v4 格式的标识；保留消息、模型、项目引用及幂等字段，不修改权限或隔离设置。

新增 `node frontend/tests/session-send.mjs`，执行实际 API 模块而非复制实现：修复前 HTTPS/localhost 场景通过，模拟局域网 HTTP 的场景报 `TypeError: crypto.randomUUID is not a function`；修复后两种场景均通过，每种 200 次请求均到达 POST，标识无重复、符合格式且内容/模型/项目引用保留。前端类型检查、生产构建及 `git diff --check` 通过；没有使用 Playwright。

使用 Edge AX 在用户原局域网地址及原失败会话中重新发送用户提供的“你好”：真实页面显示用户消息与完整 Mock 回复，会话计数更新为 2，运行恢复空闲，输入框清空。证据截图位于 `.cache/usage-validation/ui-proof/lan-http-send-20261010.png`。未将 Mock 回复当作商业模型验收。

原 Vite 开发服务自动加载改动，本次没有新建服务器、数据库或容器，没有替换或重启用户进程；核对当前 API 5600 PID 81152、前端 3680 PID 38472 保持不变。日志还发现旧 Token 元数据为空导致列表反序列化异常，该错误与本次消息发送无关，本次没有修改 Token 数据或后端逻辑。

## 2026-10-10 默认环境与真实模型验收

实现全局 Sandbox/Terminal 默认启用，系统个人 Pool 启动时同步默认真实引擎，新用户继续自动分配独立工作台；实际终端/命令容器按需启动。旧 Mock 会话只清除引擎会话标识，消息、文件和模型配置保留。验证同时覆盖关闭同步、手动配置的 Mock Pool 和管理员禁止终端的策略。

发现现有平台模型的 apiKeyEnv 误填了密钥值。增加启动时同层级加密迁移、保存格式校验及管理页面提示；验证迁移幂等、不丢失凭据、不返回明文。完整回归同时暴露并修正当前源码中的个人 Workspace/服务账号边界、普通用户管理权、受限管理员 Token、共享默认 RO、根目录等价路径保护、快照恢复完整性/活动进程检查和 API 硬存储配额问题。

最终验证：

- 后端 SQL/运行回归 43/43：`.cache/usage-validation/runtime-results/runtime-defaults-final.trx`。
- API 与 Docker 验收 28/28：`.cache/usage-validation/api-checks.json`，含真实 PTY、RO/RW、权限撤销、配额、应用 HTTP/WebSocket 预览及项目引用。
- 前端类型检查、生产构建、路由、消息 UUID、项目引用解析及实际输入组件检查通过；实际前端代理 POST/SSE/WebSocket 验证通过。
- 生产隔离 DSH CLI 与模型代理契约验证通过，代理策略验证确认默认拒绝 fake-IP，明确许可仅放行 198.18.0.0/15，其他私网/回环/链路本地仍拒绝。
- 默认禁止 fake-IP 的实际失败路径复验通过：任务及时结束为 Failed，页面消息保留明确 DNS/VPN 原因，初始化失败不再因 FUSE 子进程持续停在 Running；结果为 `.cache/usage-validation/runtime-dns-denial-checks.json`。
- 独立测试库中的旧个人 Pool 自动从 Mock 切至 DeepSeekHarness；旧文件、消息保留，新用户独立工作台使用真实引擎；无需逐用户开启终端。
- 使用现有 MiniMax 平台模型和已有密钥，在用户授权的 api.minimaxi.com VPN fake-IP 配置下实际调用隔离 Agent，收到“真实模型连接成功”，运行 Completed（实际报告 5289 输入 / 6 输出 Token）。该结果不来自 Mock 或传输替身。
- Edge AX 正常登录临时普通账号，初始地址为自己的 `/w/<id>`，对话、项目、文件模块可见；页面显示上述真实模型回复。终端点击连接后执行 id/pwd，输出 uid=1000、/workspace/files 和 TERMINAL_UI_OK。

页面证据：`.cache/usage-validation/ui-proof/real-model-defaults-20261010.png` 和 `default-terminal-20261010.png`。默认配置与真实调用结果：`.cache/usage-validation/runtime-defaults-checks.json`。所有验证均未使用 Playwright。

本次未替换或重启原 API 5600（PID 81152），现有业务库只读用于复制模型配置到一次性测试库。测试 API/UI、临时数据库和容器均由本轮清理；Edge 用户原标签页保持不变。开发环境的 MiniMax fake-IP 兼容已配置，原服务仍需按原方式重新编译并重启才能应用新的后端配置和启动迁移。自动开通不等于千人并发容量验收，部署仍需设置相应工作空间数量和计算资源上限。
# 模型预算与上下文配置（2026-10-10）

本轮增加模型参数 `taskBudget`、`contextWindow`，默认及旧配置缺失值均为 0。平台管理员通过模型编辑表单保存；普通用户不能修改。任务读取当前所选模型的有效预算；上下文传给 DSH，已知模型的配置不超过镜像目录记录的原生容量。未知自定义模型不会伪造供应商容量，自动模式使用 DSH 默认值，管理员可手动填写公开规格。

- 后端 SQL 回归 46/46 通过，包括配置保存、整数校验、权限、继承、模型选择、零预算及每日额度约束。
- 前端类型检查及构建通过。Edge 实测新增表单双 0 默认、保存预算 1280000/上下文 65536、刷新列表和重新打开表单保持数值。
- 真实 HTTP 模型代理测试通过：0 模式累计请求超过旧预算及旧传输上限仍可执行、输出可超过旧 4096 上限、正数预算返回可解释的 JSON、原生容量/显式上下文生效、调用次数与预算错误区分、模型隔离仍有效。
- 真实 DSH CLI 在隔离容器中使用测试 HTTPS 传输完成模型回复和工具写文件；上下文及输出配置被真实插件消费，用户命令仍为 uid 1000，上游密钥不进入用户环境。此项不等同于供应商真实回复测试。
- 临时 API/数据库完成平台模型配置保存回读、下一任务立即采用新预算、预算 1 在代理中阻断并展示中文提示、不输出 HTML；临时终端及新旧用户初始工作台检查通过。
- 本轮 MiniMax 真实回复复测未通过：现有平台密文无法被临时服务的默认主密钥解密。按用户要求检查项目配置、Compose 默认配置和 Git 历史，未找到匹配主密钥，项目也没有 UserSecretsId 声明。现有平台密钥未被修改；真实复测需恢复原启动主密钥或由管理员重新保存现有 MiniMax API Key。历史真实模型成功记录保留，不将其计作本轮成功。

后端已编译到正常 `bin/Debug/net10.0`，前端构建更新；隔离镜像新增 `model_capacity.py`。采用新版本需按原配置启动 API，保留原 MasterKey。此后模型限额保存即对下一任务生效，无需重启。测试服务、测试数据库和本轮临时容器均已关闭/清理；现有业务空间的容器没有被删除。

详细机器回执位于 `.cache/usage-validation/model-limits-results/model-limits-final.trx`、`model-limits-api-checks.json`；页面截图为 `.cache/usage-validation/ui-proof/model-limits-config.png`。

# 密钥恢复诊断回归（2026-10-12）

复查确认项目配置、Compose、历史版本均未找到可解密现有平台模型密钥的 MasterKey；当前 5600/3680 没有业务服务监听。本轮不修改现有密钥，增加明确的“未配置 / 无法解密 / 空值”诊断；高层级损坏密钥不能静默回退到低层级密钥，避免错误使用另一份凭据。格式损坏和过短密文均不会导致无说明的异常。

后端 SQL 回归更新为 47/47 通过，正常 API 构建 0 错误、0 警告。临时 API 实测无法解密的模型密钥会快速结束任务并返回恢复说明；管理员在对应层级重新保存密钥后，服务读取新密文的恢复路径也已通过单元测试。真实供应商回复仍待恢复原主密钥或重新保存现有 MiniMax API Key，未将负向测试记为真实模型成功。

管理员恢复路径：平台配置 → 模型 → 查看“上游密钥名称” → 组织密钥 → 对应条目的“更新值” → 保存现有 MiniMax API Key。名称保持与模型引用一致。测试密钥仅写入临时数据库，未改动 DshPlatform 的密钥。

