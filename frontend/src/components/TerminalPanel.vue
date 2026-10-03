<script setup lang="ts">
import { nextTick, onBeforeUnmount, ref } from 'vue'
import { CodeOutlined, DisconnectOutlined, LinkOutlined } from '@ant-design/icons-vue'

/**
 * Workspace 终端：WebSocket 双向通道（行式，无 PTY），工作目录为 Workspace 的 files/。
 * 受平台开关与 Pool 策略控制、仅 Member 及以上可用，打开 / 关闭均写入审计。
 */
const props = defineProps<{ workspaceId: string; allowed: boolean; reason?: string }>()
const lines = ref<string>('')
const input = ref('')
const state = ref<'idle' | 'connecting' | 'open' | 'closed'>('idle')
const history = ref<string[]>([])
let hi = -1
let ws: WebSocket | null = null
const box = ref<HTMLElement>()

function connect() {
  const proto = location.protocol === 'https:' ? 'wss' : 'ws'
  state.value = 'connecting'; lines.value = ''
  ws = new WebSocket(`${proto}://${location.host}/api/v1/workspaces/${props.workspaceId}/terminal`)
  ws.onopen = () => { state.value = 'open'; lines.value += '# 终端已连接（工作目录 files/）。输入命令后回车。\n' }
  ws.onmessage = (e) => { lines.value += String(e.data); if (lines.value.length > 200000) lines.value = lines.value.slice(-150000); nextTick(() => box.value && (box.value.scrollTop = box.value.scrollHeight)) }
  ws.onerror = () => { if (state.value !== 'open') lines.value += '# 连接失败：终端可能被平台 / Pool 策略禁用，或你没有成员权限。\n' }
  ws.onclose = () => { state.value = 'closed'; lines.value += '\n# 连接已关闭\n' }
}
function send() {
  if (!ws || state.value !== 'open' || !input.value) return
  history.value.unshift(input.value); hi = -1
  lines.value += `$ ${input.value}\n`; ws.send(input.value + '\n'); input.value = ''
}
function key(e: KeyboardEvent) {
  if (e.key === 'ArrowUp' && history.value.length) { hi = Math.min(hi + 1, history.value.length - 1); input.value = history.value[hi]; e.preventDefault() }
  if (e.key === 'ArrowDown') { hi = Math.max(hi - 1, -1); input.value = hi >= 0 ? history.value[hi] : ''; e.preventDefault() }
}
function close() { ws?.close() }
onBeforeUnmount(close)
</script>

<template>
  <div class="term">
    <a-alert v-if="!allowed" type="warning" show-icon :message="reason || '终端不可用'" description="终端等同于运行环境内的 shell：默认关闭，需平台管理员开启 Dsh__Runtime__TerminalEnabled，并且 Pool 策略允许。" />
    <template v-else>
      <div class="bar">
        <span class="tag" :class="state === 'open' ? 'ok dot' : ''">{{ { idle: '未连接', connecting: '连接中…', open: '已连接', closed: '已断开' }[state] }}</span>
        <span class="spacer" />
        <a-button v-if="state !== 'open' && state !== 'connecting'" type="primary" size="small" @click="connect"><LinkOutlined /> 连接</a-button>
        <a-button v-else size="small" @click="close"><DisconnectOutlined /> 断开</a-button>
      </div>
      <pre ref="box" class="out mono">{{ lines || '点击“连接”打开终端' }}</pre>
      <div class="inp"><CodeOutlined /><input v-model="input" class="mono" :disabled="state !== 'open'" placeholder="输入命令，回车执行（↑↓ 历史）" @keydown.enter="send" @keydown="key" /></div>
      <div class="muted small" style="margin-top: 8px">提示：共享空间只读挂载（RO）在终端内无法被文件级强制，终端结束后系统会对比变更并对 RO 目录的修改记录违规审计。</div>
    </template>
  </div>
</template>

<style scoped>
.bar { display: flex; align-items: center; margin-bottom: 12px; }
.out { background: #0b1220; color: #d1e7ff; padding: 14px 16px; border-radius: 12px 12px 0 0; height: 340px; overflow: auto; margin: 0; white-space: pre-wrap; word-break: break-all; line-height: 1.6; }
.inp { display: flex; align-items: center; gap: 10px; background: #0f1a2e; color: #67e8f9; padding: 10px 16px; border-radius: 0 0 12px 12px; }
.inp input { flex: 1; background: transparent; border: 0; outline: 0; color: #e2f3ff; }
</style>
