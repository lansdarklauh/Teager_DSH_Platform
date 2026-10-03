<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { message } from 'ant-design-vue'
import { CloudServerOutlined, PlusOutlined } from '@ant-design/icons-vue'
import { adminApi, poolApi } from '@/api'
import { fmtAgo } from '@/utils/format'
import PageHeader from '@/components/PageHeader.vue'
import Tags from '@/components/Tags.vue'

const router = useRouter()
const runtimes = ref<any[]>([])
const nodes = ref<any[]>([])
const loading = ref(false)
const open = ref(false)
const form = ref<any>({ name: '', host: '', maxRuntimes: 100 })
let timer: any
async function load() { loading.value = true; try { [runtimes.value, nodes.value] = await Promise.all([adminApi.runtimes(), adminApi.nodes()]) as any } finally { loading.value = false } }
onMounted(() => { load(); timer = setInterval(load, 8000) })
onBeforeUnmount(() => clearInterval(timer))
async function act(r: any, a: 'start' | 'stop' | 'restart') { await poolApi.runtimeAction(r.poolId, a); message.success('操作完成'); load() }
async function addNode() { await adminApi.createNode(form.value); open.value = false; form.value = { name: '', host: '', maxRuntimes: 100 }; load() }
async function setNode(n: any, status: string) { await adminApi.updateNode(n.id, { status }); load() }
</script>

<template>
  <div class="page" data-mod="runtime">
    <PageHeader title="运行环境" sub="每个 Pool 一个逻辑独立的 Runtime（独立 DSH_HOME），运行在运行节点上">
      <template #icon><CloudServerOutlined /></template>
    </PageHeader>
    <div class="stack">
      <div class="card flush">
        <div class="card-head"><h3>Pool Runtime</h3><span class="tag runtime">{{ runtimes.filter((r) => ['RUNNING', 'IDLE'].includes(r.status)).length }} / {{ runtimes.length }} 在线</span></div>
        <a-table :data-source="runtimes" :loading="loading" row-key="poolId" :pagination="{ pageSize: 12, hideOnSinglePage: true }" size="middle">
          <a-table-column title="Pool" key="p"><template #default="{ record }"><a @click="router.push(`/p/${record.poolId}?tab=runtime`)"><b>{{ record.poolName }}</b></a></template></a-table-column>
          <a-table-column title="Provider" key="v" :width="150"><template #default="{ record }"><span class="tag runtime">{{ record.runtimeProvider }}</span></template></a-table-column>
          <a-table-column title="状态" key="s" :width="120"><template #default="{ record }"><Tags kind="runtime" :value="record.status" /></template></a-table-column>
          <a-table-column title="运行任务" data-index="activeRuns" key="a" :width="90" />
          <a-table-column title="节点" data-index="nodeId" key="n" :width="90" />
          <a-table-column title="最近活跃" key="l" :width="120"><template #default="{ record }"><span class="muted">{{ fmtAgo(record.lastActiveAt) }}</span></template></a-table-column>
          <a-table-column title="操作" key="op" :width="200" align="right"><template #default="{ record }"><a-space :size="0"><a-button type="text" size="small" @click="act(record, 'start')">启动</a-button><a-button type="text" size="small" @click="act(record, 'stop')">停止</a-button><a-button type="text" size="small" @click="act(record, 'restart')">重启</a-button></a-space></template></a-table-column>
        </a-table>
      </div>
      <div class="card flush">
        <div class="card-head"><h3>运行节点<span class="muted small" style="font-weight: 400">　当前为单节点（本机），多节点调度属于后续阶段</span></h3><a-button @click="open = true"><PlusOutlined /> 登记节点</a-button></div>
        <a-table :data-source="nodes" row-key="id" :pagination="false" size="middle">
          <a-table-column title="节点" key="n"><template #default="{ record }"><b>{{ record.name }}</b><div class="muted small">{{ record.host }}</div></template></a-table-column>
          <a-table-column title="状态" key="s" :width="180"><template #default="{ record }"><a-select :value="record.status" size="small" style="width: 120px" :options="[{ value: 'Online', label: '在线' }, { value: 'Draining', label: '排空中' }, { value: 'Offline', label: '离线' }]" @change="(v: any) => setNode(record, v)" /></template></a-table-column>
          <a-table-column title="Runtime 数" key="r" :width="120"><template #default="{ record }">{{ record.runtimes }} / {{ record.maxRuntimes }}</template></a-table-column>
        </a-table>
      </div>
    </div>
    <a-modal v-model:open="open" title="登记运行节点" ok-text="登记" cancel-text="取消" @ok="addNode"><a-form layout="vertical"><a-form-item label="名称" required><a-input v-model:value="form.name" /></a-form-item><a-form-item label="主机 / 说明"><a-input v-model:value="form.host" /></a-form-item><a-form-item label="Runtime 数量上限"><a-input-number v-model:value="form.maxRuntimes" :min="1" style="width: 100%" /></a-form-item></a-form></a-modal>
  </div>
</template>
