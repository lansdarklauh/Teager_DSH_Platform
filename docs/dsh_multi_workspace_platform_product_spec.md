# DSH 多工作空间管理平台产品方案

> 版本：V1.0  
> 目标：基于 DeepSeek Harness（DSH）构建一套支持多用户、层级 Pool、独立 Workspace、共享空间、统一登录、统一 SDK/API 和可扩展 Runtime 的 Agent 工作平台。

---

## 1. 产品定位

本产品不是“给每个用户部署一个 DSH 地址”的简单管理器，而是一套位于 DSH 之上的 **多用户 Agent Workspace Platform**。

平台负责：

- 用户、组织、分组与层级关系
- Pool（工作空间组）管理
- Workspace（工作空间）管理
- Shared Space（共享空间）管理
- Pool 层级继承与共享权限
- DSH Runtime 调度与生命周期
- Session 管理
- 模型、Skill、MCP、插件配置
- API Token 与统一 SDK/API
- 资源限制
- 日志、审计、用量统计

DSH 负责：

- Agent Loop
- 模型调用
- Shell / 文件操作
- Tool / Skill / Plugin 执行
- Session 上下文
- Agent 任务执行
- 流式结果输出

平台对用户隐藏 DSH 的内部端口、Host 地址、Launch Token、Runtime 进程等细节。

---

# 2. 产品核心原则

整个系统遵循以下原则：

1. **一个统一地址**
   - 所有 Web 用户统一访问一个平台地址。
   - 不为每个 Pool 或 Workspace 单独暴露 DSH 地址。

2. **一套统一身份**
   - Web 使用平台登录态。
   - SDK/API 使用统一 Bearer Token。
   - DSH 原生认证仅在平台内部处理。

3. **Pool 是逻辑工作空间组，不是 Docker 容器池**
   - Pool 用于组织用户、Workspace、共享资源和运行环境。
   - Pool 可以存在父子层级。

4. **Workspace 是真正的 AI 工作边界**
   - 每个 Workspace 有自己的文件、Session 和任务上下文。

5. **Shared Space 是独立一级资源**
   - 可授权给一个或多个 Pool。
   - 支持 RO / RW。
   - 支持通过 Pool 层级向下继承。

6. **Runtime 与数据分离**
   - Runtime 可以启动、停止、升级、迁移。
   - Workspace、Shared Space、Session、配置必须持久化。

7. **平台 API 是正式接口**
   - 外部客户端永远不直接依赖 DSH 原生 API。
   - DSH 只作为内部 Runtime。

8. **未来不绑定死 DSH**
   - Runtime Provider 层允许未来接入其他 Agent Runtime。

---

# 3. 术语定义

| 中文名称 | 内部名称 | 含义 |
|---|---|---|
| 容器池 | Pool / Workspace Pool | 一组用户、Workspace、共享资源与运行配置组成的逻辑工作环境 |
| 容器 | Workspace | 真正供 Agent 执行任务的工作空间 |
| 共享空间 | Shared Space | 可被多个 Pool / Workspace 挂载使用的共享文件资源 |
| 会话 | Session | 某个 Workspace 下持续存在的一次 Agent 对话/任务上下文 |
| 运行环境 | Runtime | 后台实际运行 DSH 的进程或实例 |
| 运行节点 | Worker Node | 承载 Runtime 的 Linux 服务器 |
| 分组 | Group | 用户归属与授权的组织单元，可根据产品需要与 Pool 分开或映射 |
| Runtime Provider | Runtime Provider | DSH、Codex、Claude Code 等运行时适配器 |

---

# 4. 总体对象关系

```text
Organization
    │
    ├── Users
    │
    └── Pool Tree
         │
         ├── Pool A
         │    ├── Workspace A1
         │    ├── Workspace A2
         │    └── Shared Spaces
         │
         ├── Pool B
         │    ├── Workspace B1
         │    └── Shared Spaces
         │
         └── Pool C
              ├── Child Pool A
              ├── Child Pool B
              └── Shared Spaces
```

运行关系：

```text
用户
 │
 ▼
统一平台
 │
 ▼
Pool
 │
 ├── Workspace
 │     ├── Session
 │     └── Files
 │
 ├── Shared Space
 │
 └── Runtime
       │
       ▼
      DSH
```

---

# 5. Pool：工作空间组

Pool 是整个平台最重要的业务对象之一。

它不等同于 Docker Container，也不等同于单个文件夹。

一个 Pool 可以包含：

```text
Pool
├── Members
├── Child Pools
├── Workspaces
├── Shared Spaces
├── Models
├── Skills
├── MCP
├── Plugins
├── Secrets
├── Runtime Config
├── Usage Policy
└── Permission Policy
```

Pool 可以表示：

- 部门
- 项目组
- 业务线
- 专题组
- 个人工作组
- 更大的组织级资源组

例如：

```text
环球旅讯
├── 编辑部
├── 市场部
├── 技术部
└── AI 实验室
```

---

# 6. Pool Tree：层级 Pool

本方案正式支持 **父子 Pool / 多级 Pool Tree**。

这是为了满足以下场景：

```text
用户：
A B C D E

Pool A：
A、B

Pool B：
C、D

Pool C：
Pool A、Pool B

用户 E：
可根据业务需要直接加入 Pool C
```

结构：

```text
Pool C
├── Pool A
│    ├── User A
│    └── User B
│
└── Pool B
     ├── User C
     └── User D

User E
└── 可直接属于 Pool C
```

因此：

- A、B 可以访问 Pool A 自己授权的资源。
- C、D 可以访问 Pool B 自己授权的资源。
- A、B、C、D 因为其所属 Pool 位于 Pool C 下，所以可以继承 Pool C 向下共享的资源。
- E 如果直接属于 Pool C，则只能获得 Pool C 的资源，不自动获得 Pool A 或 Pool B 的私有资源。

---

# 7. Pool 层级访问原则

默认采用：

> **父级资源可以向下继承，子级资源不会自动向上暴露，也不会自动横向共享。**

例如：

```text
Pool C
├── Pool A
└── Pool B
```

则：

### Pool C 的 Shared Space

可以被：

```text
Pool C
Pool A
Pool B
```

访问。

### Pool A 的 Shared Space

默认只可以被：

```text
Pool A
```

访问。

Pool B 不会因为与 Pool A 同属于 Pool C 就自动获得 Pool A 的资源。

### Pool B 同理。

因此：

```text
父 → 子：可继承
子 → 父：默认不继承
兄弟 Pool：默认不共享
```

这样可以防止权限无限扩散。

---

# 8. Pool 层级共享示例

假设：

```text
集团 Pool C
├── 编辑组 Pool A
│    ├── A
│    └── B
│
└── 市场组 Pool B
     ├── C
     └── D
```

共享资源：

```text
Shared Space C1：
集团公共资料

Shared Space A1：
编辑内部资料

Shared Space B1：
市场内部资料
```

访问结果：

| 用户 | 集团公共资料 C1 | 编辑内部资料 A1 | 市场内部资料 B1 |
|---|---:|---:|---:|
| A | 是 | 是 | 否 |
| B | 是 | 是 | 否 |
| C | 是 | 否 | 是 |
| D | 是 | 否 | 是 |
| E（直属 C） | 是 | 否 | 否 |

这就是层级 Pool 的核心访问逻辑。

---

# 9. Pool 层级深度

第一版建议支持：

```text
最多 5 层
```

例如：

```text
集团
└── 事业部
    └── 部门
        └── 项目组
            └── 专题组
```

技术上可支持无限层级，但产品层面不建议无限嵌套，否则：

- 权限难以理解
- 资源继承难以排查
- UI 容易复杂
- 审计成本增加

因此建议：

- 默认最大 5 层
- 后端通过配置控制最大深度

---

# 10. Pool 成员继承

用户可以：

1. 直接属于某个 Pool
2. 通过子 Pool 间接属于父 Pool

例如：

```text
A ∈ Pool A
Pool A ∈ Pool C
```

则：

```text
A effective pools =
Pool A
Pool C
```

即 A 的有效 Pool 集合为：

```text
直接 Pool
+
所有祖先 Pool
```

但是不包含：

```text
兄弟 Pool
子 Pool
```

---

# 11. Effective Membership

平台需要维护一个概念：

> Effective Membership：用户最终有效所属的 Pool 集合。

例如：

```text
Pool Root
└── Pool C
    ├── Pool A
    │    └── A
    └── Pool B
         └── C
```

A 的 Effective Membership：

```text
Pool A
Pool C
Pool Root
```

C 的 Effective Membership：

```text
Pool B
Pool C
Pool Root
```

权限判断时不需要逐层临时查询，而可以通过：

- Closure Table
- Materialized Path
- Nested Set
- Cache

等方式快速计算祖先关系。

第一版推荐：

```text
Pool
+
PoolClosure
```

---

# 12. PoolClosure 建议

数据表：

```text
PoolClosure

ancestor_pool_id
descendant_pool_id
depth
```

例如：

```text
C -> C  depth 0
C -> A  depth 1
C -> B  depth 1
A -> A  depth 0
B -> B  depth 0
```

如果未来：

```text
Root -> C -> A
```

则：

```text
Root -> A depth 2
```

这样判断：

> Pool A 是否属于 Pool C？

只需要：

```text
PoolClosure
ancestor = C
descendant = A
```

即可。

---

# 13. Shared Space：共享空间

Shared Space 是独立一级对象，不应只是某个 Workspace 下的目录。

例如：

```text
集团公共资料
编辑素材库
市场素材库
研究数据
代码公共库
品牌资产
知识库导出
```

数据结构：

```text
SharedSpace
├── Owner
├── Storage
├── Grants
├── Mount Config
├── Quota
└── Audit
```

---

# 14. Shared Space 授权模型

共享空间通过：

```text
SharedSpaceGrant
```

授权。

字段建议：

```text
shared_space_id
subject_type
subject_id
permission
inherit_to_children
```

subject_type：

```text
POOL
USER
GROUP
```

第一版推荐优先支持：

```text
POOL
```

permission：

```text
RO
RW
MANAGE
```

inherit_to_children：

```text
true
false
```

---

# 15. 父 Pool 共享资源的继承

例如：

```text
SharedSpace:
集团公共资料

Grant:
Pool C
Permission: RO
inherit_to_children: true
```

则：

```text
Pool C
Pool A
Pool B
```

全部获得：

```text
RO
```

访问权。

如果：

```text
inherit_to_children: false
```

则只允许 Pool C 自身直接成员 / Workspace 使用。

---

# 16. 子 Pool 资源默认不向父级共享

例如：

```text
SharedSpace:
编辑部内部资料

Grant:
Pool A
RW
```

Pool C 不自动获得。

Pool B 不自动获得。

如果确实需要 Pool C 使用，则显式授权：

```text
Grant:
Pool C
RO
```

因此所有跨层级共享都遵循：

> 默认最小权限 + 显式授权。

---

# 17. Workspace：真正的 Agent 工作空间

Workspace 是 Agent 工作的实际目录与会话边界。

例如：

```text
编辑部 Pool

├── 酒店研究
├── 航空研究
├── 峰会项目
└── 临时工作
```

每一个都是独立 Workspace。

Workspace 包含：

```text
Workspace
├── Private Files
├── Mounted Shared Spaces
├── Sessions
├── Runtime Config Override
├── Model Config Override
├── Skill Config Override
└── Usage Statistics
```

---

# 18. Workspace 与 Pool 的关系

一个 Workspace 第一版只属于一个 Pool：

```text
Workspace
→ Pool
```

不建议一个 Workspace 同时属于多个 Pool。

跨 Pool 协作使用：

```text
Shared Space
```

解决。

这样权限模型会简单很多。

---

# 19. Workspace 文件结构

建议 Agent 看到统一结构：

```text
/workspace
├── files/
│   └── Workspace 私有文件
│
└── shared/
    ├── company/
    ├── department/
    └── project/
```

例如：

```text
/workspace/shared/group-public
/workspace/shared/editorial
/workspace/shared/report-data
```

用户不用关心底层物理存储位置。

---

# 20. Shared Space 挂载规则

Shared Space 是否真正进入 Workspace，应经过两层判断：

```text
Pool 是否有权访问
+
Workspace 是否启用挂载
```

例如 Pool A 有：

```text
集团公共资料 RO
编辑部资料 RW
```

但 Workspace A1 可能只挂载：

```text
集团公共资料
```

Workspace A2 则挂载：

```text
集团公共资料
编辑部资料
```

因此：

```text
权限
≠
实际挂载
```

这可以避免 Workspace 被大量无关目录污染。

---

# 21. Workspace 访问权限

第一版推荐：

```text
Owner
Admin
Member
Viewer
```

Pool 权限与 Workspace 权限相结合。

一个用户能进入 Workspace，需要至少满足：

```text
1. 用户拥有该 Workspace 显式权限

或

2. 用户属于 Workspace 所属 Pool 的有效成员
```

可配置 Workspace 是否：

```text
inherit_pool_members = true / false
```

默认：

```text
true
```

---

# 22. 权限覆盖原则

推荐采用：

```text
显式拒绝
>
显式授权
>
继承授权
>
默认拒绝
```

例如：

Pool C：

```text
集团资料 RO
```

用户 A 通过 Pool A 继承到 Pool C。

如果 Workspace A1 显式将该 Shared Space 关闭：

```text
不挂载
```

则 A 在 A1 内看不到该目录。

---

# 23. 统一访问地址

整个平台只有一个外部入口：

```text
https://dsh.company.com
```

建议路由：

```text
/login

/app

/p/{poolId}

/w/{workspaceId}

/w/{workspaceId}/s/{sessionId}

/api/v1/*
```

用户永远不需要知道：

```text
Runtime IP
Runtime Port
DSH Host URL
Launch Token
内部 Worker Node
```

---

# 24. 登录体系

Web 用户：

```text
用户名 + 密码
```

后续可支持：

```text
LDAP
OIDC
SSO
企业微信
飞书
钉钉
```

登录后：

```text
Platform Session
+
HttpOnly Cookie
```

用户访问：

```text
Pool
Workspace
Session
Files
```

统一使用平台登录态。

---

# 25. API / SDK 身份体系

SDK/API 使用：

```http
Authorization: Bearer dshp_xxxxxxxxx
```

Token 类型：

```text
Personal Token
Service Token
Temporary Token
```

Token 必须包含：

```text
owner
scope
expiration
allowed pools
allowed workspaces
```

---

# 26. Token Scope

建议：

```text
pool:read
pool:manage

workspace:read
workspace:write
workspace:execute
workspace:manage

session:read
session:create
session:execute

files:read
files:write

share:read
share:write
share:manage

runtime:read
runtime:manage
```

---

# 27. API 权限判断链

例如：

```http
POST /api/v1/sessions
```

请求：

```json
{
  "workspace_id": "ws_123"
}
```

Gateway 判断：

```text
Token
 ↓
User
 ↓
Workspace
 ↓
Workspace Pool
 ↓
Effective Membership
 ↓
Workspace ACL
 ↓
Token Scope
 ↓
Allow / Deny
```

---

# 28. Pool Runtime 模型

推荐：

> 一个 Pool 对应一个逻辑独立 DSH Runtime Environment。

即：

```text
Pool A
├── DSH_HOME A
├── Runtime Config A
├── Models A
├── Skills A
├── MCP A
├── Secrets A
└── Workspaces A*
```

Pool B：

```text
Pool B
├── DSH_HOME B
├── Runtime Config B
├── Models B
├── Skills B
├── MCP B
├── Secrets B
└── Workspaces B*
```

这样：

```text
Session
Credential
Plugin
Model Config
Skill Config
```

不会混用。

---

# 29. 父子 Pool Runtime 不自动合并

虽然 Pool A 属于 Pool C：

```text
Pool C
└── Pool A
```

但：

```text
Pool A Runtime
≠
Pool C Runtime
```

Pool 层级主要用于：

```text
成员关系
资源共享
权限继承
策略继承
```

而不是把 Runtime 合并为一个进程。

---

# 30. Pool 配置继承

Pool 配置可以使用：

```text
Platform Default
      ↓
Parent Pool
      ↓
Child Pool
      ↓
Workspace Override
```

适合继承：

```text
Allowed Models
Skills
MCP
Quota Policy
Network Policy
Security Policy
Default Shared Space
```

例如：

```text
Pool C：
允许 DeepSeek + GPT

Pool A：
继承 Pool C
并禁用 GPT

Workspace A1：
只能使用 DeepSeek
```

---

# 31. 配置继承原则

推荐：

```text
父级提供默认值
子级可以收紧
子级不能默认扩大安全权限
```

特别是：

```text
Network
Secret
Model
MCP
```

权限。

如果要扩大，需要管理员显式授权。

---

# 32. Runtime 与数据分离

必须遵循：

```text
Runtime 生命周期
≠
Data 生命周期
```

Runtime 可以：

```text
Start
Stop
Restart
Upgrade
Move
Replace
```

而以下数据必须持久化：

```text
Workspace Files
Shared Spaces
Session Metadata
Pool Config
Model Config
Skill Config
Audit Log
Secrets
```

---

# 33. Runtime 生命周期

状态建议：

```text
STOPPED
STARTING
RUNNING
IDLE
STOPPING
ERROR
MAINTENANCE
```

当用户访问 Workspace：

```text
Workspace Request
      ↓
Pool
      ↓
Runtime Manager
      ↓
Runtime Running?
   ┌──┴──┐
   │     │
 Yes     No
   │     ↓
   │   Start
   │     ↓
   └── Runtime
```

---

# 34. 单一统一 Gateway

所有请求经过：

```text
Platform Gateway
```

职责：

```text
Authentication
Authorization
RBAC
Pool Resolution
Workspace Resolution
Runtime Routing
API Token Validation
WebSocket Proxy
SSE Streaming
Rate Limit
Audit
```

---

# 35. 完整 Web 请求链

用户进入：

```text
酒店研究 Workspace
```

流程：

```text
Browser
 ↓
Platform Session
 ↓
Gateway
 ↓
User
 ↓
Workspace
 ↓
Pool
 ↓
Effective Membership
 ↓
Workspace Permission
 ↓
Runtime Manager
 ↓
Pool Runtime
 ↓
DSH
 ↓
Session
 ↓
Agent
 ↓
Streaming
 ↓
Gateway
 ↓
Browser
```

---

# 36. 完整 API 请求链

```text
SDK
 ↓
Bearer Token
 ↓
Gateway
 ↓
Token Scope
 ↓
Workspace
 ↓
Pool
 ↓
Effective Membership
 ↓
Runtime Router
 ↓
DSH Adapter
 ↓
DSH Runtime
```

Web 和 SDK 最终进入同一套 Runtime。

---

# 37. Session 设计

Session 必须是一等对象。

关系：

```text
Workspace
   ↓
Session
   ↓
Message
   ↓
Agent Run
```

Session 支持：

```text
Create
Resume
Rename
Archive
Delete
Search
Export
```

Session 保持：

```text
Conversation Context
Model Selection
Tool State
Execution History
```

---

# 38. 统一 API

Base URL：

```text
https://dsh.company.com/api/v1
```

推荐核心接口：

## Pool

```http
GET    /pools
POST   /pools
GET    /pools/{id}
PATCH  /pools/{id}
DELETE /pools/{id}
```

层级关系：

```http
GET  /pools/{id}/children
POST /pools/{id}/children
GET  /pools/{id}/ancestors
GET  /pools/{id}/descendants
```

成员：

```http
GET    /pools/{id}/members
POST   /pools/{id}/members
DELETE /pools/{id}/members/{userId}
```

---

# 39. Workspace API

```http
GET    /workspaces
POST   /workspaces
GET    /workspaces/{id}
PATCH  /workspaces/{id}
DELETE /workspaces/{id}
```

---

# 40. Shared Space API

```http
GET    /shared-spaces
POST   /shared-spaces
GET    /shared-spaces/{id}
PATCH  /shared-spaces/{id}
DELETE /shared-spaces/{id}
```

授权：

```http
GET    /shared-spaces/{id}/grants
POST   /shared-spaces/{id}/grants
DELETE /shared-spaces/{id}/grants/{grantId}
```

---

# 41. Session API

```http
POST /sessions

GET /sessions/{id}

POST /sessions/{id}/messages

GET /sessions/{id}/events

DELETE /sessions/{id}
```

---

# 42. 流式输出

推荐：

```text
SSE
```

用于普通 Agent 输出。

需要双向交互时：

```text
WebSocket
```

适合：

```text
Terminal
Approval
Interactive Tool
Realtime Control
```

---

# 43. SDK

Python 示例：

```python
from dsh_platform import Client

client = Client(
    base_url="https://dsh.company.com",
    token="dshp_xxxxx"
)

workspace = client.workspace("ws_123")

session = workspace.create_session()

result = session.send(
    "分析当前项目并找出问题"
)

print(result.text)
```

继续对话：

```python
session.send("把这些问题修复")
```

---

# 44. Web UI 信息架构

平台首页：

```text
DSH Platform

我的 Pool
├── 集团公共 Pool
├── 编辑部 Pool
├── 市场部 Pool
└── AI 实验室
```

Pool 支持树形显示：

```text
集团
└── 内容中心
    ├── 编辑部
    │    ├── 酒店组
    │    └── 航空组
    └── 市场部
```

---

# 45. Pool 详情页

建议菜单：

```text
概览

Workspace

子 Pool

共享空间

成员

模型

Skills

MCP

API

Runtime

用量

审计日志

设置
```

---

# 46. Workspace 详情页

```text
Workspace 名称

所属 Pool

共享空间

Sessions

Files

Models

Skills

Runtime Status

Storage Usage

API Access
```

操作：

```text
进入工作台
创建 Session
重命名
复制
快照
归档
删除
```

---

# 47. DSH Workbench

进入：

```text
/w/{workspaceId}
```

用户看到：

```text
Pool / Workspace

Sessions

Agent Chat

Files

Terminal

Plan

Tools

Skills

Approvals
```

用户无需离开统一平台。

---

# 48. Pool Tree UI

建议左侧支持树：

```text
▼ 集团 Pool
   ▼ 内容中心
      ▼ 编辑部
         酒店组
         航空组
      ▶ 市场部
   ▶ 技术中心
```

Pool 页面需要明确显示：

```text
直接成员
继承成员

直接共享空间
继承共享空间
```

避免管理员不知道权限从哪里来的。

---

# 49. 权限来源可解释性

每个权限应允许查看：

```text
权限来源
```

例如：

```text
集团公共资料

当前权限：
RO

来源：
Pool C

继承路径：
Pool C
→ Pool A
→ 当前 Workspace
```

这是层级权限系统非常重要的产品功能。

---

# 50. 禁止复杂隐式权限

不建议第一版支持：

```text
多个父 Pool
循环继承
DAG Pool
```

第一版采用严格树结构：

```text
一个 Pool
最多一个 Parent Pool
```

这样：

```text
ancestor
descendant
inheritance
```

都可以明确解释。

如果以后确有需要，再升级为 DAG。

---

# 51. User E 场景

对于你提出的：

```text
A B → Pool A
C D → Pool B

Pool A
Pool B
   ↓
Pool C

User E
```

推荐支持 E 三种模式。

### 模式 1：E 直属 Pool C

```text
Pool C
├── E
├── Pool A
└── Pool B
```

E 获得：

```text
Pool C 资源
```

但不会获得：

```text
Pool A 私有资源
Pool B 私有资源
```

---

### 模式 2：E 属于 Pool A

则 E：

```text
Pool A
Pool C
```

均可访问。

---

### 模式 3：E 同时显式加入 A 和 B

如果业务确实需要：

```text
E → Pool A
E → Pool B
```

则 E 获得：

```text
Pool A
Pool B
Pool C
```

资源。

因此：

> Pool Tree 是单父级树，但 User Membership 可以是多 Membership。

---

# 52. 用户与 Pool 的多对多关系

推荐：

```text
User
↔
Pool
```

是多对多。

即：

```text
一个用户可以直接加入多个 Pool
```

但：

```text
一个 Pool 只能有一个 Parent Pool
```

这能够兼顾：

```text
组织层级清晰
+
跨团队人员协作
```

---

# 53. Effective Permission 计算

最终权限：

```text
Direct User Grant

+

Direct Pool Grant

+

Inherited Ancestor Pool Grant

+

Workspace Grant

-

Explicit Deny
```

推荐优先级：

```text
Explicit Deny

>

Explicit User / Workspace Grant

>

Direct Pool Grant

>

Inherited Pool Grant

>

Default Deny
```

---

# 54. 数据库核心表

建议：

```text
Users

Organizations

Pools

PoolClosure

PoolMembers

Workspaces

WorkspaceMembers

SharedSpaces

SharedSpaceGrants

WorkspaceSharedSpaceMounts

Sessions

SessionMessages

Runtimes

RuntimeNodes

ApiTokens

ModelConfigs

SkillConfigs

McpConfigs

Secrets

UsageLogs

AuditLogs
```

---

# 55. Pools 表

建议字段：

```text
id

organization_id

parent_pool_id

name

description

status

runtime_provider

inherit_config

created_by

created_at

updated_at
```

---

# 56. PoolMembers

```text
id

pool_id

user_id

role

membership_type

created_at
```

membership_type：

```text
DIRECT
```

祖先 membership 不需要物理重复存储，可通过 PoolClosure 计算。

---

# 57. SharedSpaceGrants

建议：

```text
id

shared_space_id

subject_type

subject_id

permission

inherit_to_children

created_by

created_at
```

其中：

```text
subject_type =
POOL
USER
```

第一版 UI 主要面向：

```text
POOL
```

---

# 58. WorkspaceSharedSpaceMounts

```text
workspace_id

shared_space_id

mount_name

enabled

permission_override
```

注意：

```text
permission_override
```

只能：

```text
降低权限
```

不能扩大上层 Grant 权限。

例如：

```text
Pool Grant = RO
```

Workspace 不允许 override 成：

```text
RW
```

---

# 59. Runtime 数据表

```text
Runtime

id
pool_id
provider
node_id
status
internal_endpoint
version
started_at
last_active_at
```

---

# 60. Runtime Provider

第一版：

```text
DeepSeekHarnessProvider
```

接口：

```text
StartRuntime

StopRuntime

RestartRuntime

HealthCheck

CreateSession

ResumeSession

SendMessage

StreamEvents

GetFiles

OpenTerminal
```

以后：

```text
CodexProvider

ClaudeCodeProvider

OpenCodeProvider
```

---

# 61. Secret 管理

Secrets 不允许写进 Workspace。

Secret 层级：

```text
Organization Secret

Pool Secret

Workspace Secret
```

注入优先级：

```text
Workspace

>

Pool

>

Organization
```

但访问必须经过权限检查。

---

# 62. 模型与 Skill 继承

采用：

```text
Platform Default
 ↓
Organization
 ↓
Parent Pool
 ↓
Child Pool
 ↓
Workspace
```

例如：

```text
集团：
DeepSeek
GPT

编辑部：
继承两者

某 Workspace：
仅启用 DeepSeek
```

---

# 63. 审计

必须记录：

```text
谁

什么时候

从哪里

进入哪个 Pool

进入哪个 Workspace

创建哪个 Session

执行什么操作

使用哪个 Shared Space

修改哪些权限

创建/删除哪些资源
```

---

# 64. Shared Space 审计

重点记录：

```text
READ

WRITE

DELETE

RENAME

MOVE
```

尤其是：

```text
RW Shared Space
```

因为它会被多个 Pool 共用。

---

# 65. 第一阶段 MVP

第一阶段只实现真正影响可用性的功能。

## 用户

```text
Login
User
Basic Role
```

## Pool

```text
Create
Edit
Delete
Parent Pool
Child Pool
Tree View
Members
Effective Members
```

## Workspace

```text
Create
Edit
Delete
Enter
```

## Shared Space

```text
Create
Grant To Pool
RO / RW
inherit_to_children
Workspace Mount
```

## Runtime

```text
Start
Stop
Restart
Health
```

## DSH

```text
Create Session
Resume Session
Send Message
Stream Result
Files
```

## API

```text
Pool
Workspace
Session
Message
Events
```

## Token

```text
Personal Token
Basic Scope
```

## Audit

```text
Login
Pool
Workspace
Session
Permission Change
```

---

# 66. 第二阶段

增加：

```text
Model Management

Skill Management

MCP Management

Secret Manager

Workspace Snapshot

Workspace Clone

Session Search

File Versioning

Quota

Advanced Token Scope

Service Account

Usage Dashboard

Detailed Audit

Pool Policy
```

---

# 67. 第三阶段

增加：

```text
Multiple Worker Nodes

Runtime Scheduler

Runtime Migration

High Availability Gateway

SSO

LDAP

OIDC

Organization Management

Billing

Quota Packages

Public SDK

CLI

Webhook

Automation

Scheduled Agent

Runtime Provider Marketplace
```

---

# 68. 推荐技术架构

如果采用：

```text
Vue

.NET

SQL Server

Linux
```

建议：

```text
Vue
│
├── Platform UI
└── DSH Workbench UI

ASP.NET Core
│
├── Auth
├── User
├── Pool
├── Workspace
├── Shared Space
├── Permission Engine
├── Gateway
├── Runtime Manager
├── API Token
├── Audit
└── SDK API

SQL Server
│
├── Metadata
├── Permission
├── Session Index
└── Audit

Linux Worker
│
├── Pool Runtime A
├── Pool Runtime B
├── Pool Runtime C
└── Runtime Supervisor

Storage
│
├── Workspace Storage
└── Shared Space Storage
```

---

# 69. Permission Engine

建议单独做一个：

```text
Permission Engine
```

负责：

```text
Resolve User Pools

Resolve Ancestors

Resolve Descendants

Resolve Effective Shared Spaces

Resolve Workspace Access

Resolve Effective Role

Resolve Token Scope

Explain Permission Source
```

所有 API 不自己拼权限逻辑。

---

# 70. 缓存

因为 Pool Tree 权限判断频繁，建议缓存：

```text
user effective pools

pool ancestors

pool descendants

effective shared spaces
```

可使用：

```text
Memory Cache
```

第一版。

后续多节点时：

```text
Redis
```

---

# 71. 权限变更后的缓存失效

以下操作必须触发缓存失效：

```text
Add Pool Member

Remove Pool Member

Move Pool

Change Parent Pool

Add Shared Space Grant

Remove Shared Space Grant

Change inherit_to_children

Change Workspace Permission
```

---

# 72. Pool 移动

支持：

```text
Pool A
```

从：

```text
Pool C
```

移动到：

```text
Pool D
```

必须重新计算：

```text
PoolClosure

Effective Membership

Inherited Shared Spaces

Inherited Config

Permission Cache
```

移动前 UI 必须展示影响：

```text
将失去哪些父级资源

将新增哪些父级资源

影响多少用户

影响多少 Workspace
```

---

# 73. 删除 Pool

删除 Pool 不应直接级联删除全部数据。

建议：

```text
删除 Pool

选项：

○ 将子 Pool 移到父 Pool

○ 将子 Pool 移到 Root

○ 同时归档子 Pool
```

Workspace：

```text
○ 归档

○ 转移到其他 Pool
```

Shared Space 默认不删除。

---

# 74. 删除 Workspace

提供：

```text
Delete Runtime Context

Archive Workspace Data

Permanent Delete
```

默认：

```text
Archive 30 Days
```

---

# 75. 产品最终体验

用户登录：

```text
dsh.company.com
```

看到：

```text
我的工作空间

集团
└── 内容中心
    ├── 编辑部
    │    ├── 酒店研究
    │    ├── 航空研究
    │    └── 峰会项目
    │
    └── 市场部
         └── 市场项目
```

用户 A 属于：

```text
编辑部
```

因此自动获得：

```text
编辑部资源

内容中心资源

集团资源
```

但不会获得：

```text
市场部私有资源
```

用户点击：

```text
酒店研究
```

直接进入：

```text
DSH Workbench
```

开始 Agent 任务。

无需再次登录。

---

# 76. 产品最终架构

```text
                          User
                           │
                  ┌────────┴────────┐
                  │                 │
                 Web              SDK/API
                  │                 │
                  └────────┬────────┘
                           │
                           ▼
                ┌────────────────────┐
                │  Platform Gateway  │
                │                    │
                │ Auth               │
                │ Token              │
                │ Permission Engine  │
                │ Runtime Router     │
                │ Stream Proxy       │
                │ Audit              │
                └─────────┬──────────┘
                          │
                          ▼
                    Pool Resolver
                          │
             ┌────────────┴────────────┐
             │                         │
           Pool C                    Other Pool
         /        \
     Pool A      Pool B
       │            │
   Workspace     Workspace
       │            │
       └──── Shared Space ────┐
                              │
                           Storage
                              │
                              ▼
                      Runtime Manager
                              │
               ┌──────────────┴──────────────┐
               │                             │
         Pool A Runtime                Pool B Runtime
               │                             │
              DSH                           DSH
```

---

# 77. 最终产品抽象

整个产品可以最终浓缩为五个核心对象。

## 1. Pool

决定：

```text
谁属于哪个工作环境

上下级关系

资源继承

配置继承
```

## 2. Workspace

决定：

```text
AI 在哪里工作

文件在哪里

Session 属于哪里
```

## 3. Shared Space

决定：

```text
哪些资料可以跨 Workspace / Pool 共用

谁 RO

谁 RW

是否向子 Pool 继承
```

## 4. Gateway + Permission Engine

决定：

```text
你是谁

你能访问哪个 Pool

你能访问哪个 Workspace

权限从哪里继承

你的 API Token 能做什么
```

## 5. Runtime

决定：

```text
哪个 DSH 实例真正执行任务
```

---

# 78. 最终推荐模型

不采用：

```text
一个 Pool
=
一个 DSH 地址

一个 Workspace
=
一个账号密码
```

而采用：

```text
统一平台地址
      │
      ▼
统一身份
      │
      ▼
Pool Tree
      │
      ├── Membership
      ├── Resource Inheritance
      └── Config Inheritance
      │
      ▼
Workspace
      │
      ▼
Session
      │
      ▼
Runtime Router
      │
      ▼
DSH
```

外部：

```text
Web

SDK

CLI

Third-party System
```

全部走同一套平台 API。

---

# 79. 针对 A/B/C/D/E 场景的最终定义

最终正式支持以下模型：

```text
Pool C
├── Pool A
│    ├── A
│    └── B
│
├── Pool B
│    ├── C
│    └── D
│
└── E
```

资源：

```text
Pool A Private
→ A B

Pool B Private
→ C D

Pool C Shared
→ A B C D E
```

并保证：

```text
A / B
不会自动看到 Pool B Private

C / D
不会自动看到 Pool A Private

E
不会自动看到 A / B 的子 Pool 私有内容

所有人
均可访问 Pool C 向下继承的共享资源
```

这应作为平台权限模型的基础规则，而不是后续补丁。

---

# 80. 一句话产品定义

> **这是一个以层级 Pool 组织用户和资源、以 Workspace 承载 Agent 工作、以 Shared Space 实现跨团队共享、以统一 Gateway 和 API 管理身份与路由、并以 DSH 作为底层 Agent Runtime 的多租户 AI 工作平台。**

