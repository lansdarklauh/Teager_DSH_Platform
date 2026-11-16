<script setup lang="ts">
import { computed } from 'vue'
import { message } from 'ant-design-vue'
import { CopyOutlined, KeyOutlined } from '@ant-design/icons-vue'
import { useRouter } from 'vue-router'
import { useAuthStore } from '@/stores/auth'
import { copyText } from '@/utils/format'

/** API / SDK 接入示例：使用统一 Bearer Token，Web 与 SDK 走同一套接口。 */
const props = defineProps<{ workspaceId?: string; poolId?: string }>()
const router = useRouter()
const auth = useAuthStore()
const base = computed(() => auth.meta?.publicBaseUrl || window.location.origin)
const wsId = computed(() => props.workspaceId || auth.user?.defaultWorkspaceId || 'ws_xxxxxxxxxxxx')
const py = computed(() => `from dsh_platform import Client

client = Client(base_url="${base.value}", token="dshp_xxxxx")

workspace = client.workspace("${wsId.value}")
session = workspace.create_session()

result = session.send("分析当前项目并找出问题")
print(result.text)

# 继续对话
session.send("把这些问题修复")`)
const curl = computed(() => `# 1) 创建会话
curl -X POST ${base.value}/api/v1/sessions \\
  -H "Authorization: Bearer dshp_xxxxx" -H "Content-Type: application/json" \\
  -d '{"workspaceId":"${wsId.value}"}'

# 2) 发送消息并同步等待结果（wait=true）
curl -X POST "${base.value}/api/v1/sessions/ses_xxxx/messages?wait=true" \\
  -H "Authorization: Bearer dshp_xxxxx" -H "Content-Type: application/json" \\
  -d '{"content":"分析当前项目并找出问题","requestId":"每次任务的唯一标识"}'

# 3) 订阅流式事件（SSE）
curl -N ${base.value}/api/v1/sessions/ses_xxxx/events -H "Authorization: Bearer dshp_xxxxx"`)
async function copy(t: string) { await copyText(t); message.success('已复制') }
</script>

<template>
  <div class="stack" data-mod="token">
    <div class="note"><KeyOutlined /><span>SDK / API 使用统一的 <b>Bearer Token</b>（<span class="mono">Authorization: Bearer dshp_…</span>），可限定 Scope 与可访问的 Pool / Workspace。Token 在 <a @click="router.push('/tokens')">API Token</a> 页面创建，明文只显示一次。DSH 的内部地址与端口不会暴露给外部客户端。</span></div>
    <div class="grid grid-2">
      <div class="card"><div class="card-title">Python SDK<span class="spacer" /><a-button size="small" @click="copy(py)"><CopyOutlined /> 复制</a-button></div><pre class="code">{{ py }}</pre><div class="muted small">SDK 位于仓库 <span class="mono">sdk/python</span>，<span class="mono">pip install ./sdk/python</span> 即可使用。</div></div>
      <div class="card"><div class="card-title">HTTP / curl<span class="spacer" /><a-button size="small" @click="copy(curl)"><CopyOutlined /> 复制</a-button></div><pre class="code">{{ curl }}</pre></div>
    </div>
    <div class="card"><div class="card-title">文件、命令与应用接入</div>
      <p>当前个人工作空间：<span class="mono">{{ wsId }}</span>。用 <span class="mono">GET /api/v1/workspaces/{{ wsId }}/files</span> 查看文件，用 <span class="mono">PUT /files/content</span>（JSON 包含 path、content）保存文件；项目接口为 <span class="mono">/projects</span>。</p>
      <p>执行命令：<span class="mono">POST /api/v1/workspaces/{{ wsId }}/commands</span>，例如 <span class="mono">{"command":"python3 server.py","ports":[8000]}</span>。响应中的 id 用于 <span class="mono">/executions/{id}/logs</span> 查询日志、<span class="mono">DELETE /executions/{id}</span> 停止；<span class="mono">POST /executions/{id}/preview</span>（<span class="mono">{"port":8000}</span>）获取限时应用地址。</p>
      <p>对话需 session:create、session:execute、session:read；文件需 files:read / files:write；命令需 workspace:execute 及文件读写。共享空间还需明确选择共享白名单及 share:read / share:write，最终权限不会超过管理员给你的授权。</p>
      <p>引用项目：先获取当前工作空间的 <span class="mono">/projects</span> 列表，发送消息时增加 <span class="mono">"projectIds":["prj_xxx"]</span>；Python 可用 <span class="mono">session.send("分析项目", project_ids=["prj_xxx"])</span>。每条消息最多引用 8 个当前工作空间的项目，引用信息会保存在消息历史中。</p>
      <p class="muted small">401：凭据失效；403：Scope 或操作权限不足；404：资源不存在或不在授权范围；409：正在运行或请求标识冲突；429：额度不足。请求失败后重试同一任务应复用 requestId，避免重复执行。任务过程可按事件 seq 续接。Token 明文只在创建时显示一次；遗失后请撤销并重新创建。</p>
    </div>
  </div>
</template>

<style scoped>
.code { background: #0f172a; color: #e2e8f0; padding: 16px 18px; border-radius: 12px; font-size: 12.5px; line-height: 1.7; overflow: auto; margin: 0 0 12px; white-space: pre; font-family: Consolas, 'Cascadia Mono', monospace; }
</style>
