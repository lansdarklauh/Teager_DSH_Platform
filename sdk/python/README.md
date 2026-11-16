# dsh-platform（Python SDK）

DSH 多工作空间平台的 Python SDK，仅依赖标准库。SDK 与 Web 走**同一套平台 API**，身份为统一的 Bearer Token（`dshp_…`），
不会也不需要接触 DSH 的内部地址、端口或 Launch Token。

## 安装

```bash
pip install ./sdk/python      # 在仓库根目录执行
```

## 快速开始

```python
from dsh_platform import Client

client = Client(base_url="https://dsh.company.com", token="dshp_xxxxx")

workspace = client.workspace("ws_123")
session = workspace.create_session()

result = session.send("分析当前项目并找出问题")   # 同步等待，返回最终文本
print(result.text, result.input_tokens, result.output_tokens)

session.send("把这些问题修复")                     # 继续同一个会话（上下文保持）
```

## 流式输出（SSE）

```python
for event in session.stream("重构 files/ 下的脚本"):
    if event["type"] == "text":
        print(event["text"], end="", flush=True)
    elif event["type"] == "tool_call":
        print(f"\n[调用工具 {event['tool']}]")
    elif event["type"] == "run_end":
        print("\n完成：", event["status"])
```

## 在消息中引用项目

先通过 `GET /api/v1/workspaces/{workspaceId}/projects` 查询当前工作空间的项目 ID，随后在消息中传入它们：

```python
result = session.send("检查项目结构并修复问题", project_ids=["prj_xxx"])
for event in session.stream("为项目补充说明", project_ids=["prj_xxx"]):
    if event["type"] == "text":
        print(event["text"], end="", flush=True)
```

每条消息最多引用 8 个不同项目，项目必须属于该会话的工作空间且目录存在。引用需要 Token 的 `files:read` Scope；隔离任务执行还需要 `files:write`。项目名称、目录和描述作为独立元数据保留在消息历史中，Agent 按需读取项目文件；引用不会扩大权限。引用只随本次消息发送，后续消息需要再次传入 `project_ids`。

## 文件与共享空间

```python
ws = client.workspace("ws_123")
ws.files.write("notes/todo.md", "# 待办")            # 私有文件（Agent 的 files/ 目录）
print(ws.files.list("notes"))
ws.files.upload("./report.pdf", "docs")

print(ws.mounts())                                   # 共享空间挂载与我的有效权限
print(ws.shared_files("group-public").list())        # 读取已挂载共享空间（RO 空间写入会返回 403）
print(ws.explain(shared_space_id="ss_xxx"))          # 权限来源与继承路径
```

## 错误处理

```python
from dsh_platform import DshError

try:
    session.send("…")
except DshError as e:
    print(e.status, e.code, e.message)   # 如 403 forbidden / 429 quota_exceeded / 409 conflict
```

## Token 与 Scope

在 Web 的“API Token”页面创建 Token，可限定 Scope（如 `session:execute`、`files:read`）以及可访问的 Pool / Workspace。
Token 的实际能力 = Token 的 Scope 白名单 ∩ 所有者本人的权限。
