# 对话中的项目引用

登录后在自己的工作台中使用项目引用：

1. 在“我的项目”中新建项目，并通过“打开文件”管理项目目录内的文件。
2. 在对话输入框中输入 `@` 或 `@项目名称`，选择候选项目；也可以点击项目卡片的“引用到对话”。
3. 输入任务，例如“检查这个项目的代码并修复登录问题”，然后发送。项目显示为独立标签，可在发送前移除。

候选项目支持名称、目录和描述搜索。使用 ↑↓ 选择、Enter 确认、Esc 关闭；确认项目后再次 Enter 发送，Shift+Enter 换行。每条消息最多引用 8 个项目。发送成功后本次引用清空；后续消息可以重新选择。

Agent 获得项目 ID、名称、目录和描述，并按需读取项目文件。不会在发送时把整个项目所有文件塞进模型上下文，也不会把引用当作额外授权。普通用户只能引用自己工作空间中的项目；访问共享文件仍遵守管理员指定的只读/可编辑权限。

历史消息保存发送时的项目元数据。项目后来重命名或移除，旧消息仍能展示当时的引用；新消息引用已经移除的项目或已经不存在的目录会被拒绝。引用不改变终端工作目录、沙箱挂载或隔离边界。

API 消息请求增加可选 `projectIds`：

```http
POST /api/v1/sessions/{sessionId}/messages
Content-Type: application/json

{
  "content": "检查这个项目的代码",
  "projectIds": ["prj_xxx"],
  "requestId": "my-unique-request-id"
}
```

通过 `GET /api/v1/workspaces/{workspaceId}/projects` 获取项目 ID。Token 引用项目需要 `files:read`；隔离任务执行还需要 `files:write`。消息历史和 `run_start` 事件包含 `projects` 元数据，复制会话和 JSON/Markdown 导出也保留引用。

Python SDK：

```python
session.send("检查项目结构", project_ids=["prj_xxx"])
for event in session.stream("补充项目说明", project_ids=["prj_xxx"]):
    if event["type"] == "text":
        print(event["text"], end="", flush=True)
```

同一 `requestId` 的相同消息和引用返回原任务；更换正文、模型或引用后复用该 ID 返回 409。省略 `projectIds` 的原有调用保持兼容。

发布需应用 `20261004112938_ProjectMessageReferences` 迁移，并同时更新 API、前端与需要使用新参数的 Python SDK。现有运行服务未在测试期间重启。验证结果见 [验证记录](usage-only-verification.md)；当前浏览器策略阻止真实页面验收，已完成组件逻辑、API/SDK 和容器回归。
