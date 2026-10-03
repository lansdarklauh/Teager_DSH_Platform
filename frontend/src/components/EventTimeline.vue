<script setup lang="ts">
import { computed } from 'vue'
import { ToolOutlined, BulbOutlined, WarningOutlined, ExclamationCircleOutlined, PlayCircleOutlined, CheckCircleOutlined } from '@ant-design/icons-vue'

/**
 * Agent 运行事件时间线：把 DSH / Mock 的事件流（thinking / tool_call / tool_result / status / alert / error）整理为可读的执行过程。
 * mode = all 全部；tools 只看工具调用；plan 只看执行进度（turn / step / 用量）。
 */
const props = defineProps<{ events: any[]; mode?: 'all' | 'tools' | 'plan' }>()

interface Item { key: string; kind: string; title: string; body?: string; status?: string; input?: string; result?: string; ts?: string }
const items = computed<Item[]>(() => {
  const out: Item[] = []
  const calls = new Map<string, Item>()
  for (const e of props.events) {
    const k = `${e.seq}`
    switch (e.type) {
      case 'run_start': out.push({ key: k, kind: 'start', title: '开始运行', body: e.prompt, ts: e.ts }); break
      case 'thinking': if ((e.text || '').trim()) out.push({ key: k, kind: 'thinking', title: '思考', body: e.text, ts: e.ts }); break
      case 'tool_call': {
        const it: Item = { key: k, kind: 'tool', title: e.tool, status: 'running', input: JSON.stringify(e.input ?? {}, null, 2), ts: e.ts }
        calls.set(e.callId, it); out.push(it); break
      }
      case 'tool_result': {
        const it = calls.get(e.callId)
        const txt = typeof e.result === 'string' ? e.result : JSON.stringify(e.result, null, 2)
        if (it) { it.status = e.status === 'error' ? 'error' : 'ok'; it.result = txt } else out.push({ key: k, kind: 'tool', title: '工具结果', status: e.status, result: txt, ts: e.ts })
        break
      }
      case 'status':
        if (e.phase === 'step_end' && e.usage) out.push({ key: k, kind: 'step', title: `第 ${e.turn ?? ''}${e.turn ? ' 轮 ' : ''}第 ${e.step ?? ''} 步完成`, body: `输入 ${e.usage.inputTokens ?? 0} · 输出 ${e.usage.outputTokens ?? 0} tokens`, ts: e.ts })
        else if (e.phase === 'turn_start') out.push({ key: k, kind: 'step', title: '开始新一轮', ts: e.ts })
        else if (e.phase === 'turn_end') out.push({ key: k, kind: 'step', title: `本轮结束：${e.reason?.kind === 'error' ? '出错' : '完成'}`, body: e.reason?.error?.message, status: e.reason?.kind === 'error' ? 'error' : 'ok', ts: e.ts })
        break
      case 'alert': out.push({ key: k, kind: 'alert', title: '告警', body: e.message, status: e.level, ts: e.ts }); break
      case 'error': out.push({ key: k, kind: 'error', title: '错误', body: e.message, status: 'error', ts: e.ts }); break
      case 'run_end': out.push({ key: k, kind: 'end', title: `运行结束：${({ Completed: '完成', Failed: '失败', Cancelled: '已取消' } as any)[e.status] || e.status}`, body: `耗时 ${((e.durationMs || 0) / 1000).toFixed(1)}s · 输入 ${e.inputTokens || 0} · 输出 ${e.outputTokens || 0} tokens`, status: e.status === 'Completed' ? 'ok' : 'error', ts: e.ts }); break
    }
  }
  return out
})
const shown = computed(() => items.value.filter((i) => props.mode === 'tools' ? i.kind === 'tool' : props.mode === 'plan' ? ['start', 'step', 'end'].includes(i.kind) : true))
const time = (t?: string) => (t ? t.slice(11, 19) : '')
</script>

<template>
  <div v-if="shown.length" class="tl">
    <div v-for="i in shown" :key="i.key" class="it" :class="[i.kind, i.status]">
      <span class="dot">
        <ToolOutlined v-if="i.kind === 'tool'" /><BulbOutlined v-else-if="i.kind === 'thinking'" /><WarningOutlined v-else-if="i.kind === 'alert'" />
        <ExclamationCircleOutlined v-else-if="i.kind === 'error' || i.status === 'error'" /><PlayCircleOutlined v-else-if="i.kind === 'start'" /><CheckCircleOutlined v-else />
      </span>
      <div class="bd">
        <div class="hd"><b>{{ i.title }}</b><span v-if="i.kind === 'tool'" class="tag" :class="i.status === 'ok' ? 'ok' : i.status === 'error' ? 'danger' : 'warn'">{{ i.status === 'ok' ? '成功' : i.status === 'error' ? '失败' : '执行中' }}</span><span class="spacer" /><span class="muted small">{{ time(i.ts) }}</span></div>
        <div v-if="i.body" class="txt" :class="{ think: i.kind === 'thinking' }">{{ i.body }}</div>
        <details v-if="i.input" class="det"><summary>输入参数</summary><pre>{{ i.input }}</pre></details>
        <details v-if="i.result" class="det"><summary>执行结果</summary><pre>{{ i.result.length > 4000 ? i.result.slice(0, 4000) + '\n…（已截断）' : i.result }}</pre></details>
      </div>
    </div>
  </div>
  <a-empty v-else :image="false" description="暂无执行记录" />
</template>

<style scoped>
.tl { display: flex; flex-direction: column; gap: 14px; }
.it { display: flex; gap: 12px; }
.dot { width: 28px; height: 28px; border-radius: 9px; background: var(--chat-soft); color: var(--chat); display: flex; align-items: center; justify-content: center; flex: none; font-size: 14px; }
.it.tool .dot { background: var(--ws-soft); color: var(--ws); }
.it.thinking .dot { background: var(--space-soft); color: var(--space); }
.it.alert .dot, .it.error .dot, .it.error .dot { background: var(--danger-soft); color: var(--danger); }
.it.end.ok .dot { background: var(--ok-soft); color: var(--ok); }
.bd { flex: 1; min-width: 0; }
.hd { display: flex; align-items: center; gap: 8px; }
.txt { color: var(--text-2); margin-top: 4px; white-space: pre-wrap; word-break: break-word; font-size: 13px; max-height: 160px; overflow: auto; }
.txt.think { color: var(--text-3); font-style: italic; }
.det { margin-top: 6px; } .det summary { cursor: pointer; color: var(--text-3); font-size: 12px; }
.det pre { margin: 6px 0 0; padding: 10px 12px; background: var(--surface-2); border: 1px solid var(--border); border-radius: 8px; font-size: 12px; max-height: 220px; overflow: auto; white-space: pre-wrap; word-break: break-all; }
</style>
