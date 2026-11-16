<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { http, unwrap } from '@/api/http'
const props = defineProps<{ workspaceId: string }>()
const rows = ref<any[]>([])
const command = ref('')
const ports = ref('')
const preview = ref('')
const logs = ref('')
const saving = ref(false)
const selected = ref('')
const environment = ref<any>(null)
let timer: ReturnType<typeof setInterval> | undefined
const base = () => `/workspaces/${props.workspaceId}`
async function load() {
  rows.value = await unwrap(http.get(`${base()}/executions`))
  if (selected.value) logs.value = (await unwrap(http.get(`${base()}/executions/${selected.value}/logs`))).output
}
async function start() {
  saving.value = true
  try { const execution = await unwrap(http.post(`${base()}/commands`, { command: command.value, ports: ports.value.split(',').map(p => Number(p.trim())).filter(p => p > 0) })); selected.value = execution.id; await load() }
  finally { saving.value = false }
}
function rowPorts(row: any): number[] { return JSON.parse(row.portsJson || '[]') }
async function openPreview(row: any, port: number) { const result = await unwrap(http.post(`${base()}/executions/${row.id}/preview`, { port })); preview.value = result.url }
async function stop(id: string) { await http.delete(`${base()}/executions/${id}`); await load() }
async function show(id: string) { selected.value = id; await load() }
onMounted(async () => { environment.value = await unwrap(http.get(`${base()}/environment`)); await load(); timer = setInterval(() => load().catch(() => {}), 3000) })
onBeforeUnmount(() => clearInterval(timer))
</script>
<template>
  <div class="processes">
    <a-alert v-if="environment && !environment.ready" type="warning" :message="environment.message" show-icon />
    <p class="muted small">命令从 /workspace/files 运行；依赖安装到项目或 /home/user。后台进程按管理员配置的时长运行，关闭页面后继续运行。</p>
    <a-textarea v-model:value="command" placeholder="例如：node projects/prj_xxx/server.js" :rows="3" />
    <a-input v-model:value="ports" placeholder="应用端口，例如 8000,3000（可选）" class="mt-8" /><a-button type="primary" class="mt-8" :disabled="!environment?.ready || !command.trim()" :loading="saving" @click="start">执行命令 / 启动应用</a-button>
    <div v-for="row in rows" :key="row.id" class="card execution">
      <div class="row-wrap"><span class="mono small">{{ row.id }}</span><span class="tag">{{ row.kind }} · {{ row.status }}</span></div>
      <div class="row-wrap mt-8"><a-button size="small" @click="show(row.id)">日志</a-button><a-button v-if="row.status === 'Running'" size="small" danger @click="stop(row.id)">停止</a-button><a-button v-for="port in rowPorts(row)" v-if="row.status === 'Running'" :key="port" size="small" @click="openPreview(row, port)">预览 {{ port }}</a-button><span v-if="row.exitCode != null" class="muted small">退出码 {{ row.exitCode }}</span></div>
    </div>
    <pre v-if="selected" class="logs mono">{{ logs || '等待输出…' }}</pre>
    <a-drawer :open="!!preview" title="个人应用预览" width="80vw" @close="preview = ''"><iframe v-if="preview" :src="preview" sandbox="allow-scripts allow-forms allow-same-origin allow-downloads" referrerpolicy="no-referrer" style="width: 100%; height: 80vh; border: 0" /></a-drawer>
    <a-empty v-if="!rows.length" description="尚未启动命令或应用" />
  </div>
</template>
<style scoped>.processes { padding: 16px; }.execution { padding: 12px; margin-top: 12px; }.logs { max-height: 350px; overflow: auto; white-space: pre-wrap; background: #0b1220; color: #d1e7ff; padding: 12px; margin-top: 12px; }</style>
