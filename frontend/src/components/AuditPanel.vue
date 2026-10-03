<script setup lang="ts">
import { reactive, ref, watch } from 'vue'
import dayjs, { type Dayjs } from 'dayjs'
import { ReloadOutlined } from '@ant-design/icons-vue'
import { auditApi } from '@/api'
import { fmtTime } from '@/utils/format'
import { actionName, actionColor, auditCategories } from '@/utils/audit'

/** 审计日志面板：可限定 Pool（含子孙）/ Workspace；按类别、结果、操作人、时间、关键字过滤。 */
const props = defineProps<{ poolId?: string; workspaceId?: string; defaultAction?: string; defaultKeyword?: string }>()
const filter = reactive<any>({ action: props.defaultAction, result: undefined, actor: '', keyword: props.defaultKeyword || '', range: null as null | [Dayjs, Dayjs] })
const page = ref(1)
const pageSize = ref(20)
const total = ref(0)
const rows = ref<any[]>([])
const loading = ref(false)

async function load() {
  loading.value = true
  try {
    const r: any = await auditApi.query({
      poolId: props.poolId, includeDescendants: props.poolId ? true : undefined, workspaceId: props.workspaceId, action: filter.action, result: filter.result, actor: filter.actor, keyword: filter.keyword,
      from: filter.range?.[0]?.startOf('day').format('YYYY-MM-DDTHH:mm:ss'), to: filter.range?.[1]?.endOf('day').format('YYYY-MM-DDTHH:mm:ss'), page: page.value, pageSize: pageSize.value
    })
    rows.value = r.items; total.value = r.total
  } finally { loading.value = false }
}
watch(() => [props.poolId, props.workspaceId], () => { page.value = 1; load() })
function search() { page.value = 1; load() }
load()
const resultText: Record<string, string> = { success: '成功', denied: '拒绝', failed: '失败' }
const resultCls: Record<string, string> = { success: 'ok', denied: 'danger', failed: 'warn' }
defineExpose({ reload: load })
</script>

<template>
  <div class="stack">
    <div class="card">
      <div class="row-wrap">
        <a-select v-model:value="filter.action" allow-clear placeholder="操作类别" style="width: 150px" :options="auditCategories" @change="search" />
        <a-select v-model:value="filter.result" allow-clear placeholder="结果" style="width: 110px" :options="[{ value: 'success', label: '成功' }, { value: 'denied', label: '拒绝' }, { value: 'failed', label: '失败' }]" @change="search" />
        <a-input v-model:value="filter.actor" placeholder="操作人" style="width: 130px" allow-clear @press-enter="search" />
        <a-range-picker v-model:value="filter.range" style="width: 250px" :presets="[{ label: '今天', value: [dayjs(), dayjs()] }, { label: '近 7 天', value: [dayjs().subtract(6, 'day'), dayjs()] }, { label: '近 30 天', value: [dayjs().subtract(29, 'day'), dayjs()] }]" @change="search" />
        <a-input-search v-model:value="filter.keyword" placeholder="关键字（详情 / 资源 ID）" style="width: 240px" allow-clear @search="search" />
        <span class="spacer" />
        <a-button @click="load"><ReloadOutlined /> 刷新</a-button>
      </div>
    </div>
    <div class="card flush">
      <a-table :data-source="rows" :loading="loading" row-key="id" size="middle" :pagination="{ current: page, pageSize, total, showSizeChanger: true, showTotal: (t: number) => `共 ${t} 条`, onChange: (p: number, s: number) => { page = p; pageSize = s; load() } }">
        <a-table-column title="时间" key="t" :width="170"><template #default="{ record }"><span class="muted">{{ fmtTime(record.time) }}</span></template></a-table-column>
        <a-table-column title="操作人" key="a" :width="140"><template #default="{ record }"><b>{{ record.actorName }}</b><div class="muted small">{{ record.via === 'token' ? 'API Token' : record.via === 'system' ? '系统' : 'Web' }}<template v-if="record.ip"> · {{ record.ip }}</template></div></template></a-table-column>
        <a-table-column title="操作" key="o" :width="170"><template #default="{ record }"><span class="tag" :class="actionColor(record.action)">{{ actionName(record.action) }}</span></template></a-table-column>
        <a-table-column title="详情" key="d"><template #default="{ record }"><span class="text-2">{{ record.detail || '—' }}</span><div v-if="record.resourceId" class="muted small mono">{{ record.resourceType }} · {{ record.resourceId }}</div></template></a-table-column>
        <a-table-column title="结果" key="r" :width="90"><template #default="{ record }"><span class="tag dot" :class="resultCls[record.result]">{{ resultText[record.result] || record.result }}</span></template></a-table-column>
      </a-table>
    </div>
  </div>
</template>
