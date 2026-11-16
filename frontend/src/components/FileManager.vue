<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { message, Modal } from 'ant-design-vue'
import { FolderFilled, FileTextOutlined, UploadOutlined, FolderAddOutlined, ReloadOutlined, DownloadOutlined, DeleteOutlined, EditOutlined, LockOutlined } from '@ant-design/icons-vue'
import { fileApi, type FileTarget } from '@/api'
import { fmtBytes, fmtShort } from '@/utils/format'

/**
 * 文件管理器：Workspace 私有文件 / 挂载的共享空间 / 直接访问共享空间共用。
 * areas 用于在“私有文件 / 各共享挂载”之间切换；只读区域会隐藏写操作。
 */
const props = defineProps<{ base: string; areas?: { value: string; label: string; hint?: string }[]; canWrite?: boolean; initialPath?: string }>()
const area = ref<string | undefined>(props.areas?.[0]?.value)
const path = ref(props.initialPath || '')
const entries = ref<any[]>([])
const writable = ref(false)
const loading = ref(false)
const target = computed<FileTarget>(() => ({ base: props.base, area: area.value }))
const crumbs = computed(() => { const parts = path.value.split('/').filter(Boolean); return parts.map((p, i) => ({ name: p, path: parts.slice(0, i + 1).join('/') })) })

async function load() {
  loading.value = true
  try {
    const r: any = await fileApi.list(target.value, path.value)
    entries.value = r.entries; writable.value = !!r.writable && props.canWrite !== false
  } catch { entries.value = []; writable.value = false } finally { loading.value = false }
}
watch(() => [props.base, props.areas?.map((a) => a.value).join(',')], () => { if (!props.areas?.some((a) => a.value === area.value)) area.value = props.areas?.[0]?.value; path.value = ''; load() })
watch(() => props.initialPath, (value) => { area.value = props.areas?.[0]?.value; path.value = value || ''; load() })
watch(area, () => { path.value = ''; load() }, { flush: 'sync' })
watch(path, load, { immediate: true })

function open(e: any) { if (e.isDir) path.value = e.path; else preview(e) }

// 预览 / 编辑
const editor = ref({ open: false, path: '', content: '', saving: false, err: '' })
async function preview(e: any) {
  try {
    const r: any = await fileApi.read(target.value, e.path)
    editor.value = { open: true, path: e.path, content: r.content, saving: false, err: '' }
  } catch (err: any) { editor.value = { open: true, path: e.path, content: '', saving: false, err: err.friendly || '无法预览该文件（可能是二进制或过大），请下载查看' } }
}
async function save() {
  editor.value.saving = true
  try { await fileApi.write(target.value, editor.value.path, editor.value.content); message.success('已保存'); editor.value.open = false; load() } finally { editor.value.saving = false }
}

// 上传 / 新建目录 / 重命名 / 删除
async function upload(opt: any) {
  try { await fileApi.upload(target.value, path.value, [opt.file]); message.success(`已上传 ${opt.file.name}`); load(); opt.onSuccess?.({}) } catch (e) { opt.onError?.(e as Error) }
}
const newDir = ref({ open: false, name: '' })
async function mkdir() {
  if (!newDir.value.name.trim()) return
  await fileApi.mkdir(target.value, [path.value, newDir.value.name.trim()].filter(Boolean).join('/'))
  newDir.value.open = false; newDir.value.name = ''; load()
}
const rename = ref({ open: false, from: '', name: '' })
function askRename(e: any) { rename.value = { open: true, from: e.path, name: e.name } }
async function doRename() {
  const dir = rename.value.from.includes('/') ? rename.value.from.slice(0, rename.value.from.lastIndexOf('/')) : ''
  await fileApi.move(target.value, rename.value.from, [dir, rename.value.name.trim()].filter(Boolean).join('/'))
  rename.value.open = false; load()
}
function del(e: any) {
  Modal.confirm({ title: `删除「${e.name}」？`, content: e.isDir ? '目录及其全部内容将被删除，且不可恢复。' : '删除后不可恢复。', okType: 'danger', okText: '删除', cancelText: '取消',
    onOk: async () => { await fileApi.remove(target.value, e.path); load() } })
}
const areaHint = computed(() => props.areas?.find((a) => a.value === area.value)?.hint)
defineExpose({ reload: load })
</script>

<template>
  <div class="fm">
    <div v-if="areas && areas.length > 1" class="areas">
      <a-segmented v-model:value="area" :options="areas.map((a) => ({ label: a.label, value: a.value }))" />
      <span v-if="areaHint" class="muted small">{{ areaHint }}</span>
    </div>
    <div class="bar">
      <div class="crumbs">
        <a @click="path = ''">根目录</a>
        <template v-for="c in crumbs" :key="c.path"><span class="muted">/</span><a @click="path = c.path">{{ c.name }}</a></template>
      </div>
      <div class="spacer" />
      <a-tag v-if="!writable" color="orange"><LockOutlined /> 只读</a-tag>
      <a-button @click="load"><ReloadOutlined /></a-button>
      <template v-if="writable">
        <a-button @click="newDir.open = true"><FolderAddOutlined /> 新建目录</a-button>
        <a-upload :custom-request="upload" :show-upload-list="false" multiple><a-button type="primary"><UploadOutlined /> 上传文件</a-button></a-upload>
      </template>
    </div>
    <a-table :data-source="entries" :loading="loading" :pagination="false" row-key="path" size="middle" :locale="{ emptyText: '这个目录还是空的' }">
      <a-table-column title="名称" key="name">
        <template #default="{ record }">
          <div class="row" style="cursor: pointer" @click="open(record)">
            <FolderFilled v-if="record.isDir" style="color: #f0a400; font-size: 18px" /><FileTextOutlined v-else style="color: #8a91a6; font-size: 18px" />
            <span>{{ record.name }}</span>
          </div>
        </template>
      </a-table-column>
      <a-table-column title="大小" key="size" :width="110"><template #default="{ record }"><span class="muted">{{ record.isDir ? '—' : fmtBytes(record.size) }}</span></template></a-table-column>
      <a-table-column title="修改时间" key="modified" :width="140"><template #default="{ record }"><span class="muted">{{ fmtShort(record.modified) }}</span></template></a-table-column>
      <a-table-column title="操作" key="op" :width="170" align="right">
        <template #default="{ record }">
          <a-space :size="4">
            <a-tooltip v-if="!record.isDir" title="下载"><a-button type="text" size="small" :href="fileApi.downloadUrl(target, record.path)"><DownloadOutlined /></a-button></a-tooltip>
            <template v-if="writable">
              <a-tooltip title="重命名"><a-button type="text" size="small" @click="askRename(record)"><EditOutlined /></a-button></a-tooltip>
              <a-tooltip title="删除"><a-button type="text" size="small" danger @click="del(record)"><DeleteOutlined /></a-button></a-tooltip>
            </template>
          </a-space>
        </template>
      </a-table-column>
    </a-table>

    <a-drawer v-model:open="editor.open" :title="editor.path" width="720" :destroy-on-close="true">
      <a-alert v-if="editor.err" type="warning" :message="editor.err" show-icon />
      <template v-else>
        <a-textarea v-model:value="editor.content" :rows="26" :readonly="!writable" class="mono" />
        <div v-if="writable" class="row mt-16"><div class="spacer" /><a-button @click="editor.open = false">取消</a-button><a-button type="primary" :loading="editor.saving" @click="save">保存</a-button></div>
        <div v-else class="muted small mt-16">当前区域只读，无法编辑。</div>
      </template>
    </a-drawer>
    <a-modal v-model:open="newDir.open" title="新建目录" ok-text="创建" cancel-text="取消" @ok="mkdir"><a-input v-model:value="newDir.name" placeholder="目录名称" @press-enter="mkdir" /></a-modal>
    <a-modal v-model:open="rename.open" title="重命名" ok-text="确定" cancel-text="取消" @ok="doRename"><a-input v-model:value="rename.name" @press-enter="doRename" /></a-modal>
  </div>
</template>

<style scoped>
.areas { display: flex; align-items: center; gap: 14px; margin-bottom: 18px; flex-wrap: wrap; }
.bar { display: flex; align-items: center; gap: 10px; margin-bottom: 16px; flex-wrap: wrap; }
.crumbs { display: flex; align-items: center; gap: 8px; font-size: 14px; }
</style>
