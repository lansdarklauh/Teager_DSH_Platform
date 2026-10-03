<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { message, Modal } from 'ant-design-vue'
import { PlusOutlined, MessageOutlined, EditOutlined, CopyOutlined, DownloadOutlined, InboxOutlined, DeleteOutlined } from '@ant-design/icons-vue'
import { sessionApi } from '@/api'
import { fmtAgo } from '@/utils/format'

/** 会话列表：创建 / 检索 / 重命名 / 复制 / 导出 / 归档 / 删除。会话是 Workspace 下的一等对象。 */
const props = defineProps<{ workspaceId: string; canWrite: boolean }>()
const router = useRouter()
const rows = ref<any[]>([])
const keyword = ref('')
const archived = ref(false)
const loading = ref(false)
async function load() { loading.value = true; try { rows.value = (await sessionApi.list(props.workspaceId, { q: keyword.value, includeArchived: archived.value ? true : undefined })) as any[] } finally { loading.value = false } }
watch(() => [props.workspaceId, archived.value], load)
onMounted(load)
async function create() { const s: any = await sessionApi.create({ workspaceId: props.workspaceId }); router.push(`/w/${props.workspaceId}/s/${s.id}`) }
const ren = ref({ open: false, id: '', title: '' })
async function doRename() { await sessionApi.update(ren.value.id, { title: ren.value.title }); ren.value.open = false; load() }
async function dup(s: any) { await sessionApi.duplicate(s.id); message.success('已复制'); load() }
async function archive(s: any) { await sessionApi.update(s.id, { archived: s.status !== 'Archived' }); load() }
function del(s: any) { Modal.confirm({ title: `删除会话「${s.title}」？`, content: '消息与事件日志将一并删除，不可恢复。', okType: 'danger', okText: '删除', cancelText: '取消', onOk: async () => { await sessionApi.remove(s.id); load() } }) }
</script>

<template>
  <div class="stack" data-mod="chat">
    <div class="row-wrap">
      <a-input-search v-model:value="keyword" placeholder="搜索会话标题或消息内容" style="width: 320px" allow-clear @search="load" />
      <a-checkbox v-model:checked="archived">显示已归档</a-checkbox>
      <span class="spacer" />
      <a-button v-if="canWrite" type="primary" @click="create"><PlusOutlined /> 新建会话</a-button>
    </div>
    <div class="card flush">
      <a-table :data-source="rows" :loading="loading" row-key="id" :pagination="{ pageSize: 12, hideOnSinglePage: true }" size="middle" :locale="{ emptyText: '还没有会话' }">
        <a-table-column title="会话" key="t">
          <template #default="{ record }">
            <div class="row" style="cursor: pointer" @click="router.push(`/w/${workspaceId}/s/${record.id}`)">
              <span class="chip sm"><MessageOutlined /></span>
              <div><b>{{ record.title }}</b><span v-if="record.running" class="tag ok dot" style="margin-left: 8px">运行中</span><span v-if="record.status === 'Archived'" class="tag" style="margin-left: 8px">已归档</span></div>
            </div>
          </template>
        </a-table-column>
        <a-table-column title="消息" data-index="messageCount" key="m" :width="80" />
        <a-table-column title="模型" key="mo" :width="140"><template #default="{ record }"><span class="muted">{{ record.modelKey || '默认' }}</span></template></a-table-column>
        <a-table-column title="最近更新" key="u" :width="120"><template #default="{ record }"><span class="muted">{{ fmtAgo(record.updatedAt) }}</span></template></a-table-column>
        <a-table-column title="操作" key="op" :width="230" align="right">
          <template #default="{ record }">
            <a-space :size="2">
              <a-tooltip v-if="canWrite" title="重命名"><a-button type="text" size="small" @click="ren = { open: true, id: record.id, title: record.title }"><EditOutlined /></a-button></a-tooltip>
              <a-tooltip v-if="canWrite" title="复制"><a-button type="text" size="small" @click="dup(record)"><CopyOutlined /></a-button></a-tooltip>
              <a-dropdown><a-button type="text" size="small"><DownloadOutlined /></a-button><template #overlay><a-menu><a-menu-item><a :href="sessionApi.exportUrl(record.id, 'md')">导出 Markdown</a></a-menu-item><a-menu-item><a :href="sessionApi.exportUrl(record.id, 'json')">导出 JSON</a></a-menu-item></a-menu></template></a-dropdown>
              <a-tooltip v-if="canWrite" :title="record.status === 'Archived' ? '取消归档' : '归档'"><a-button type="text" size="small" @click="archive(record)"><InboxOutlined /></a-button></a-tooltip>
              <a-tooltip v-if="canWrite" title="删除"><a-button type="text" size="small" danger @click="del(record)"><DeleteOutlined /></a-button></a-tooltip>
            </a-space>
          </template>
        </a-table-column>
      </a-table>
    </div>
    <a-modal v-model:open="ren.open" title="重命名会话" ok-text="确定" cancel-text="取消" @ok="doRename"><a-input v-model:value="ren.title" @press-enter="doRename" /></a-modal>
  </div>
</template>
