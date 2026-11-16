<script setup lang="ts">
import { ref, watch } from 'vue'
import { message, Modal } from 'ant-design-vue'
import { http, unwrap } from '@/api/http'
const props = defineProps<{ workspaceId: string; referenceDisabled?: boolean }>()
const emit = defineEmits<{ openFiles: [path: string]; reference: [project: any]; changed: [] }>()
const rows = ref<any[]>([])
const open = ref(false)
const saving = ref(false)
const form = ref({ id: '', name: '', description: '' })
const url = () => `/workspaces/${props.workspaceId}/projects`
let generation = 0
async function load() { const current = ++generation; const result = await unwrap(http.get(url())); if (current === generation) { rows.value = result; emit('changed') } }
function edit(project?: any) { form.value = { id: project?.id || '', name: project?.name || '', description: project?.description || '' }; open.value = true }
async function save() {
  saving.value = true
  try {
    await (form.value.id ? http.patch(`${url()}/${form.value.id}`, form.value) : http.post(url(), form.value))
    open.value = false; await load(); message.success('项目已保存')
  } finally { saving.value = false }
}
function remove(project: any) {
  Modal.confirm({ title: `移除项目「${project.name}」？`, content: '移除项目条目后，文件仍保留在文件模块中，可继续管理。', onOk: async () => { await http.delete(`${url()}/${project.id}`); await load() } })
}
watch(() => props.workspaceId, () => { rows.value = []; load() }, { immediate: true })
</script>
<template>
  <div class="projects">
    <div class="row-wrap"><b>我的项目</b><span class="spacer" /><a-button type="primary" @click="edit()">新建项目</a-button></div>
    <p class="muted small">项目位于当前工作空间的私有文件区，可通过文件模块和终端开发。</p>
    <div v-for="project in rows" :key="project.id" class="card project">
      <b>{{ project.name }}</b><p class="muted small">{{ project.description || '暂无描述' }}</p>
      <div class="mono small">{{ project.directory }}</div>
      <div class="row-wrap mt-8"><a-button @click="emit('openFiles', project.directory)">打开文件</a-button><a-button :disabled="referenceDisabled" @click="emit('reference', project)">引用到对话</a-button><a-button @click="edit(project)">编辑</a-button><a-button danger @click="remove(project)">移除</a-button></div>
    </div>
    <a-empty v-if="!rows.length" description="创建你的第一个项目" />
    <a-modal v-model:open="open" :title="form.id ? '编辑项目' : '新建项目'" :confirm-loading="saving" @ok="save">
      <a-form layout="vertical"><a-form-item label="名称"><a-input v-model:value="form.name" :maxlength="100" /></a-form-item><a-form-item label="描述"><a-textarea v-model:value="form.description" :maxlength="1000" /></a-form-item></a-form>
    </a-modal>
  </div>
</template>
<style scoped>.projects { padding: 16px; }.project { padding: 14px; margin-bottom: 12px; }</style>
