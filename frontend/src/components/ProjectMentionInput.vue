<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { message } from 'ant-design-vue'
import { projectApi } from '@/api'
import { filterProjects, projectQuery, removeQuery, type MentionQuery, type ProjectReference } from '@/utils/projectMentions'

const props = defineProps<{ modelValue: string; projects: ProjectReference[]; workspaceId: string; disabled?: boolean }>()
const emit = defineEmits<{ 'update:modelValue': [text: string]; 'update:projects': [projects: ProjectReference[]]; send: [] }>()
const rows = ref<ProjectReference[]>([])
const query = ref<MentionQuery | null>(null)
const active = ref(0)
const loading = ref(false)
const error = ref('')
const textarea = ref<any>()
let generation = 0
const candidates = computed(() => query.value ? filterProjects(rows.value, query.value.query, props.projects) : [])
watch(() => props.workspaceId, () => { rows.value = []; query.value = null; refresh() }, { immediate: true })
watch(() => props.modelValue, value => { if (!value) query.value = null })

async function refresh() {
  const current = ++generation
  loading.value = true; error.value = ''
  try { const result = await projectApi.list(props.workspaceId) as ProjectReference[]; if (current === generation) rows.value = result }
  catch { if (current === generation) { rows.value = []; error.value = '无法加载项目，请重试' } }
  finally { if (current === generation) loading.value = false }
}
function inspect(event: Event) {
  const target = event.target as HTMLTextAreaElement
  query.value = projectQuery(target.value, target.selectionStart ?? target.value.length)
  active.value = 0
}
function add(project: ProjectReference) {
  if (props.disabled) return
  if (props.projects.some(p => p.id === project.id)) return
  if (props.projects.length >= 8) { message.info('每条消息最多引用 8 个项目'); return }
  emit('update:projects', [...props.projects, project])
  if (query.value) emit('update:modelValue', removeQuery(props.modelValue, query.value))
  query.value = null
  textarea.value?.focus()
}
function keydown(event: KeyboardEvent) {
  if (event.isComposing || event.keyCode === 229) return
  if (query.value && !event.shiftKey) {
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault()
      active.value = candidates.value.length ? (active.value + (event.key === 'ArrowDown' ? 1 : -1) + candidates.value.length) % candidates.value.length : 0
      return
    }
    if (event.key === 'Escape') { event.preventDefault(); query.value = null; return }
    if (event.key === 'Enter') { event.preventDefault(); if (candidates.value[active.value]) add(candidates.value[active.value]); return }
  }
  if (event.key === 'Enter' && !event.shiftKey) { event.preventDefault(); if (!props.disabled) emit('send') }
}
defineExpose({ add, refresh })
</script>
<template>
  <div class="project-composer">
    <div v-if="projects.length" class="reference-tags">
      <a-tag v-for="project in projects" :key="project.id" :closable="!disabled" :title="project.directory" @close.prevent="emit('update:projects', projects.filter(p => p.id !== project.id))">@{{ project.name }}</a-tag>
    </div>
    <div v-if="query && !disabled" class="project-options" role="listbox" aria-label="引用项目">
      <div class="option-note">引用当前工作空间的项目 · ↑↓ 选择，Enter 确认，Esc 关闭</div>
      <div v-if="loading" class="option-note">正在加载项目…</div>
      <div v-else-if="error" class="option-note">{{ error }} <a-button size="small" @mousedown.prevent @click="refresh">重试</a-button></div>
      <div v-else-if="!candidates.length" class="option-note">没有匹配的未引用项目</div>
      <template v-else><button v-for="(project, index) in candidates" :key="project.id" type="button" role="option" :aria-selected="index === active" :class="{ active: index === active }" @mousedown.prevent @click="add(project)">
        <b>@{{ project.name }}</b><span>{{ project.directory }}</span><small v-if="project.description">{{ project.description }}</small>
      </button></template>
    </div>
    <a-textarea ref="textarea" :value="modelValue" :auto-size="{ minRows: 1, maxRows: 8 }" :bordered="false" :disabled="disabled" placeholder="描述任务，输入 @ 引用项目…（Enter 发送，Shift+Enter 换行）" @update:value="emit('update:modelValue', $event)" @input="inspect" @click="inspect" @keyup="['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes($event.key) && inspect($event)" @keydown="keydown" @focus="refresh" />
  </div>
</template>
<style scoped>
.project-composer { position: relative; }
.reference-tags { padding: 10px 10px 0; display: flex; flex-wrap: wrap; gap: 6px; }
.project-options { position: absolute; bottom: 100%; left: 0; right: 0; z-index: 20; background: var(--surface); border: 1px solid var(--border); border-radius: 10px; padding: 6px; box-shadow: 0 6px 24px #0002; max-height: 320px; overflow-y: auto; }
.option-note { padding: 8px; font-size: 12px; color: #666; }
.project-options button { display: flex; flex-direction: column; width: 100%; text-align: left; border: 0; background: transparent; padding: 8px; border-radius: 6px; cursor: pointer; }
.project-options button.active, .project-options button:hover { background: #e6f4ff; }
.project-options span, .project-options small { color: #666; overflow-wrap: anywhere; }
</style>
