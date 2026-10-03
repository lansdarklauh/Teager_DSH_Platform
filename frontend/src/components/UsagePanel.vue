<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { ThunderboltOutlined, FieldTimeOutlined, ToolOutlined, TeamOutlined, WarningOutlined } from '@ant-design/icons-vue'
import { usageApi } from '@/api'
import { fmtCompact, fmtNum, fmtBytes } from '@/utils/format'
import StatCard from './StatCard.vue'
import MiniBars from './MiniBars.vue'

/** 用量面板：运行次数 / token / 工具调用趋势，按模型 / 用户 / Workspace / Pool 排行，以及配额使用情况。 */
const props = defineProps<{ poolId?: string; workspaceId?: string }>()
const days = ref(14)
const data = ref<any>(null)
const loading = ref(false)
async function load() { loading.value = true; try { data.value = await usageApi.summary({ poolId: props.poolId, workspaceId: props.workspaceId, days: days.value }) } finally { loading.value = false } }
watch(() => [props.poolId, props.workspaceId, days.value], load, { immediate: true })
const runsBars = computed(() => (data.value?.daily || []).map((d: any) => ({ label: d.date.slice(5), a: d.runs })))
const tokenBars = computed(() => (data.value?.daily || []).map((d: any) => ({ label: d.date.slice(5), a: d.inputTokens, b: d.outputTokens })))
const rank = (list: any[]) => { const m = Math.max(1, ...(list || []).map((x) => x.tokens)); return (list || []).map((x) => ({ ...x, pct: Math.round((x.tokens / m) * 100) })) }
const q = computed(() => data.value?.quota)
</script>

<template>
  <a-spin :spinning="loading">
    <div class="stack" data-mod="usage">
      <div class="row">
        <span class="muted">统计范围：{{ data?.scope }}</span><span class="spacer" />
        <a-segmented v-model:value="days" :options="[{ label: '7 天', value: 7 }, { label: '14 天', value: 14 }, { label: '30 天', value: 30 }, { label: '90 天', value: 90 }]" />
      </div>
      <div class="grid grid-4">
        <StatCard label="Agent 运行次数" :value="fmtNum(data?.totals?.runs)" :hint="`失败 ${data?.totals?.failed ?? 0}`" mod="usage"><template #icon><ThunderboltOutlined /></template></StatCard>
        <StatCard label="Token 总量" :value="fmtCompact((data?.totals?.inputTokens || 0) + (data?.totals?.outputTokens || 0))" :hint="`入 ${fmtCompact(data?.totals?.inputTokens)} / 出 ${fmtCompact(data?.totals?.outputTokens)}`" mod="chat"><template #icon><FieldTimeOutlined /></template></StatCard>
        <StatCard label="工具调用" :value="fmtNum(data?.totals?.toolCalls)" :hint="`平均耗时 ${((data?.totals?.avgDurationMs || 0) / 1000).toFixed(1)}s`" mod="ws"><template #icon><ToolOutlined /></template></StatCard>
        <StatCard label="活跃用户" :value="data?.totals?.activeUsers ?? 0" mod="pool"><template #icon><TeamOutlined /></template></StatCard>
      </div>

      <div v-if="q" class="note warn" style="align-items: center">
        <WarningOutlined />
        <span v-if="q.storageQuotaMb !== undefined">存储：{{ fmtBytes(q.storageBytes) }} / {{ q.storageQuotaMb ? q.storageQuotaMb + ' MB' : '不限' }}</span>
        <span v-else>
          今日消息 {{ q.todayMessages }} / {{ q.maxMessagesPerDay || '不限' }}　·　今日 Token {{ fmtCompact(q.todayTokens) }} / {{ q.maxTokensPerDay ? fmtCompact(q.maxTokensPerDay) : '不限' }}　·　Workspace 存储 {{ fmtBytes(q.workspaceStorageBytes) }}
        </span>
      </div>

      <div class="grid grid-2">
        <div class="card"><div class="card-title">每日运行次数</div><MiniBars :data="runsBars" color-a="#db2777" unit-a="次" /></div>
        <div class="card"><div class="card-title">每日 Token（<span style="color:#db2777">输入</span> / <span style="color:#f9a8d4">输出</span>）</div><MiniBars :data="tokenBars" color-a="#2563eb" color-b="#93c5fd" /></div>
      </div>

      <div class="grid grid-3">
        <div v-for="blk in [{ t: '按模型', l: rank(data?.byModel) }, { t: '按 Workspace', l: rank(data?.byWorkspace) }, { t: data?.byPool?.length ? '按 Pool' : '按用户', l: rank(data?.byPool?.length ? data.byPool : data?.byUser) }]" :key="blk.t" class="card">
          <div class="card-title">{{ blk.t }}<span class="sub">Top · Token</span></div>
          <div v-if="blk.l.length" class="rank">
            <div v-for="x in blk.l" :key="x.name" class="rk"><div class="row"><span class="ellipsis">{{ x.name }}</span><span class="spacer" /><b>{{ fmtCompact(x.tokens) }}</b></div><div class="bar"><i :style="{ width: x.pct + '%' }" /></div></div>
          </div>
          <a-empty v-else :image="false" description="暂无数据" />
        </div>
      </div>
    </div>
  </a-spin>
</template>

<style scoped>
.rank { display: flex; flex-direction: column; gap: 14px; }
.bar { height: 6px; background: var(--usage-soft); border-radius: 6px; margin-top: 6px; overflow: hidden; }
.bar i { display: block; height: 100%; background: var(--usage); border-radius: 6px; }
</style>
