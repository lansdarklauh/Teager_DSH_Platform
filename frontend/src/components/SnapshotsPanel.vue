<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { message, Modal } from 'ant-design-vue'
import { CameraOutlined, RollbackOutlined, DeleteOutlined, DownloadOutlined } from '@ant-design/icons-vue'
import { wsApi } from '@/api'
import { fmtBytes, fmtTime } from '@/utils/format'

/** Workspace 快照：把私有文件打包保存到数据卷，可恢复（覆盖当前文件）或下载。 */
const props = defineProps<{ workspaceId: string; canWrite: boolean; canManage: boolean }>()
const rows = ref<any[]>([])
const loading = ref(false)
const open = ref(false)
const form = ref({ name: '', description: '' })
const saving = ref(false)
async function load() { loading.value = true; try { rows.value = (await wsApi.snapshots(props.workspaceId)) as any[] } finally { loading.value = false } }
watch(() => props.workspaceId, load)
onMounted(load)
async function create() { saving.value = true; try { await wsApi.createSnapshot(props.workspaceId, form.value); message.success('快照已创建'); open.value = false; form.value = { name: '', description: '' }; load() } finally { saving.value = false } }
function restore(s: any) { Modal.confirm({ title: `从快照「${s.name}」恢复？`, content: '当前私有文件将被快照内容覆盖，建议先创建一个新快照备份。', okType: 'danger', okText: '恢复', cancelText: '取消', onOk: async () => { await wsApi.restoreSnapshot(props.workspaceId, s.id); message.success('已恢复') } }) }
function del(s: any) { Modal.confirm({ title: `删除快照「${s.name}」？`, okType: 'danger', okText: '删除', cancelText: '取消', onOk: async () => { await wsApi.deleteSnapshot(props.workspaceId, s.id); load() } }) }
</script>

<template>
  <div class="stack" data-mod="ws">
    <div class="row"><div class="muted">快照仅包含私有文件（files/），不含共享空间内容。</div><span class="spacer" /><a-button v-if="canWrite" type="primary" @click="open = true"><CameraOutlined /> 创建快照</a-button></div>
    <div class="card flush">
      <a-table :data-source="rows" :loading="loading" row-key="id" :pagination="false" size="middle" :locale="{ emptyText: '还没有快照' }">
        <a-table-column title="名称" key="n"><template #default="{ record }"><b>{{ record.name }}</b><div class="muted small">{{ record.description }}</div></template></a-table-column>
        <a-table-column title="大小" key="s" :width="110"><template #default="{ record }">{{ fmtBytes(record.sizeBytes) }}</template></a-table-column>
        <a-table-column title="创建时间" key="c" :width="180"><template #default="{ record }"><span class="muted">{{ fmtTime(record.createdAt) }}</span></template></a-table-column>
        <a-table-column title="操作" key="op" :width="150" align="right">
          <template #default="{ record }"><a-space :size="2"><a-tooltip title="下载"><a-button type="text" size="small" :href="wsApi.snapshotUrl(workspaceId, record.id)"><DownloadOutlined /></a-button></a-tooltip>
            <a-tooltip v-if="canManage" title="恢复"><a-button type="text" size="small" @click="restore(record)"><RollbackOutlined /></a-button></a-tooltip>
            <a-tooltip v-if="canManage" title="删除"><a-button type="text" size="small" danger @click="del(record)"><DeleteOutlined /></a-button></a-tooltip></a-space></template>
        </a-table-column>
      </a-table>
    </div>
    <a-modal v-model:open="open" title="创建快照" :confirm-loading="saving" ok-text="创建" cancel-text="取消" @ok="create">
      <a-form layout="vertical"><a-form-item label="名称"><a-input v-model:value="form.name" placeholder="留空自动命名" /></a-form-item><a-form-item label="备注"><a-input v-model:value="form.description" /></a-form-item></a-form>
    </a-modal>
  </div>
</template>
