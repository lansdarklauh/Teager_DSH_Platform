<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { DashboardOutlined, DatabaseOutlined, FileTextOutlined, ThunderboltOutlined } from '@ant-design/icons-vue'
import { adminApi } from '@/api'
import { fmtBytes, fmtTime } from '@/utils/format'
import PageHeader from '@/components/PageHeader.vue'
import StatCard from '@/components/StatCard.vue'

const info = ref<any>(null)
onMounted(async () => { info.value = await adminApi.systemInfo() })
const pct = (d: any) => (d?.totalBytes ? Math.round(((d.totalBytes - d.freeBytes) / d.totalBytes) * 100) : 0)
const dirs = (i: any) => [
  { key: 'data', name: '数据目录', desc: 'Workspace 文件 / 共享空间 / 各 Pool DSH_HOME / 快照（必须持久化）', icon: DatabaseOutlined, mod: 'ws', d: i.directories.data },
  { key: 'logs', name: '日志目录', desc: '应用日志与 Agent 运行事件日志（JSONL）', icon: FileTextOutlined, mod: 'audit', d: i.directories.logs },
  { key: 'cache', name: '缓存目录', desc: '上传 / 打包临时文件，可随时清空', icon: ThunderboltOutlined, mod: 'space', d: i.directories.cache }
]
</script>

<template>
  <div class="page" data-mod="admin">
    <PageHeader title="系统信息" sub="仅平台管理员可见；不包含任何密钥或连接串"><template #icon><DashboardOutlined /></template></PageHeader>
    <div v-if="info" class="stack">
      <div class="grid grid-4">
        <StatCard label="用户" :value="info.counts.users" mod="admin"><template #icon><DashboardOutlined /></template></StatCard>
        <StatCard label="Pool" :value="info.counts.pools" mod="pool"><template #icon><DashboardOutlined /></template></StatCard>
        <StatCard label="Workspace" :value="info.counts.workspaces" mod="ws"><template #icon><DashboardOutlined /></template></StatCard>
        <StatCard label="运行中任务" :value="info.counts.runningRuns" mod="runtime"><template #icon><DashboardOutlined /></template></StatCard>
      </div>
      <div class="grid grid-3">
        <div v-for="x in dirs(info)" :key="x.key" class="card" :data-mod="x.mod">
          <div class="row"><span class="chip"><component :is="x.icon" /></span><div><b>{{ x.name }}</b><div class="muted small">{{ x.desc }}</div></div></div>
          <div class="mono small ellipsis" style="margin: 16px 0 10px" :title="x.d.path">{{ x.d.path }}</div>
          <a-progress :percent="pct(x.d)" :stroke-color="'var(--mod)'" size="small" />
          <div class="muted small">磁盘可用 {{ fmtBytes(x.d.freeBytes) }} / 共 {{ fmtBytes(x.d.totalBytes) }}</div>
        </div>
      </div>
      <div class="card">
        <div class="card-title">运行参数</div>
        <dl class="kv">
          <dt>版本</dt><dd>{{ info.version }}</dd>
          <dt>服务器时间</dt><dd>{{ fmtTime(info.now) }}（{{ info.timeZone }}）</dd>
          <dt>Runtime Provider</dt><dd><span v-for="p in info.providers" :key="p.name" class="tag runtime" style="margin-right: 6px">{{ p.name }}</span>　默认：{{ info.defaultProvider }}</dd>
          <dt>dsh 可执行文件</dt><dd class="mono">{{ info.dshExecutable }}</dd>
          <dt>Workspace 终端</dt><dd><span class="tag dot" :class="info.terminalEnabled ? 'warn' : ''">{{ info.terminalEnabled ? '已启用（等同容器内 shell，请谨慎）' : '已关闭' }}</span></dd>
          <dt>限制</dt><dd>Pool 最大 {{ info.limits.maxPoolDepth }} 层 · 上传 ≤ {{ info.limits.maxUploadMb }} MB · 归档保留 {{ info.limits.archiveRetentionDays }} 天 · 审计保留 {{ info.limits.auditRetentionDays || '永久' }} 天</dd>
        </dl>
      </div>
    </div>
    <a-skeleton v-else active />
  </div>
</template>
