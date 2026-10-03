<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { message, Modal } from 'ant-design-vue'
import { PlayCircleOutlined, PauseCircleOutlined, ReloadOutlined, HeartOutlined, ToolOutlined } from '@ant-design/icons-vue'
import { poolApi } from '@/api'
import { fmtTime, fmtAgo } from '@/utils/format'
import Tags from './Tags.vue'

/** Runtime 面板：每个 Pool 对应一个逻辑独立的运行环境；访问 Workspace 时自动启动，空闲后自动回收。 */
const props = defineProps<{ poolId: string; canManage: boolean }>()
const rt = ref<any>(null)
const health = ref<any>(null)
const busy = ref('')
let timer: any
async function load() { try { rt.value = await poolApi.runtime(props.poolId) } catch { /* 忽略轮询错误 */ } }
async function act(a: 'start' | 'stop' | 'restart') {
  busy.value = a
  try { rt.value = await poolApi.runtimeAction(props.poolId, a); message.success({ start: '已启动', stop: '已停止', restart: '已重启' }[a]) } finally { busy.value = '' }
}
async function check() { busy.value = 'health'; try { health.value = await poolApi.runtimeHealth(props.poolId) } finally { busy.value = '' } }
function maintenance() {
  const on = rt.value?.status !== 'MAINTENANCE'
  Modal.confirm({ title: on ? '进入维护模式？' : '退出维护模式？', content: on ? '维护期间该 Pool 不再接受新的 Agent 任务，已在运行的任务不受影响。' : '退出后 Runtime 将处于停止状态，下次访问时自动启动。', okText: '确定', cancelText: '取消',
    onOk: async () => { rt.value = await poolApi.maintenance(props.poolId, on) } })
}
watch(() => props.poolId, load)
onMounted(() => { load(); timer = setInterval(load, 6000) })
onBeforeUnmount(() => clearInterval(timer))
</script>

<template>
  <div class="stack" data-mod="runtime">
    <div class="grid" style="grid-template-columns: minmax(0, 7fr) minmax(0, 5fr)">
      <div class="card">
        <div class="row" style="margin-bottom: 20px">
          <span class="chip lg solid"><ToolOutlined /></span>
          <div><div class="row"><h3 style="margin: 0; font-size: 18px">Pool Runtime</h3><Tags kind="runtime" :value="rt?.status" /></div><div class="muted">Provider：{{ rt?.provider || '—' }}　·　版本：{{ rt?.version || '—' }}</div></div>
        </div>
        <dl class="kv">
          <dt>运行节点</dt><dd>{{ rt?.nodeId || '—' }}</dd>
          <dt>启动时间</dt><dd>{{ fmtTime(rt?.startedAt) }}</dd>
          <dt>最近活跃</dt><dd>{{ fmtAgo(rt?.lastActiveAt) }}</dd>
          <dt v-if="rt?.lastError">最近错误</dt><dd v-if="rt?.lastError" style="color: var(--danger)">{{ rt.lastError }}</dd>
        </dl>
        <div v-if="canManage" class="row-wrap mt-24">
          <a-button type="primary" :loading="busy === 'start'" @click="act('start')"><PlayCircleOutlined /> 启动</a-button>
          <a-button :loading="busy === 'stop'" @click="act('stop')"><PauseCircleOutlined /> 停止</a-button>
          <a-button :loading="busy === 'restart'" @click="act('restart')"><ReloadOutlined /> 重启</a-button>
          <a-button :loading="busy === 'health'" @click="check"><HeartOutlined /> 健康检查</a-button>
          <span class="spacer" />
          <a-button danger ghost @click="maintenance">{{ rt?.status === 'MAINTENANCE' ? '退出维护' : '维护模式' }}</a-button>
        </div>
        <a-alert v-if="health" class="mt-16" :type="health.healthy ? 'success' : 'error'" show-icon :message="health.healthy ? 'Runtime 健康' : 'Runtime 异常'" :description="`${health.message || ''}　运行中任务：${health.activeRuns}`" />
      </div>
      <div class="card">
        <div class="card-title">生命周期说明</div>
        <div class="info-list small">
          <div class="info-item"><span class="tag">STOPPED</span><span class="text-2">已停止：不占用进程，数据完整保留。</span></div>
          <div class="info-item"><span class="tag ok">RUNNING / IDLE</span><span class="text-2">运行中 / 空闲：空闲一段时间自动降为 IDLE，长时间空闲自动停止。</span></div>
          <div class="info-item"><span class="tag warn">STARTING / STOPPING</span><span class="text-2">切换中：访问 Workspace 时自动启动。</span></div>
          <div class="info-item"><span class="tag danger">ERROR / 维护中</span><span class="text-2">启动失败或人工维护：暂不接受新任务。</span></div>
        </div>
        <div class="muted small mt-16">Runtime 生命周期与数据生命周期相互独立：启停、重启、升级都不会影响 Workspace 文件、共享空间、会话与配置。</div>
      </div>
    </div>
  </div>
</template>
