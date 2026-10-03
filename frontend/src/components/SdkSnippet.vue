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
const wsId = computed(() => props.workspaceId || 'ws_xxxxxxxxxxxx')
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
  -d '{"content":"分析当前项目并找出问题"}'

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
  </div>
</template>

<style scoped>
.code { background: #0f172a; color: #e2e8f0; padding: 16px 18px; border-radius: 12px; font-size: 12.5px; line-height: 1.7; overflow: auto; margin: 0 0 12px; white-space: pre; font-family: Consolas, 'Cascadia Mono', monospace; }
</style>
