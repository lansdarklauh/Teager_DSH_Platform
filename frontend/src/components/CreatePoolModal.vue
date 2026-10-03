<script setup lang="ts">
import { ref, watch } from 'vue'
import { message } from 'ant-design-vue'
import { poolApi } from '@/api'
import { useAuthStore } from '@/stores/auth'
import PoolTreeSelect from './PoolTreeSelect.vue'

/** 新建 Pool：可指定父 Pool（严格单父树，最多 5 层）；顶级 Pool 仅平台管理员可建。 */
const open = defineModel<boolean>('open', { required: true })
const props = defineProps<{ parentId?: string }>()
const emit = defineEmits<{ created: [pool: any] }>()
const auth = useAuthStore()
const form = ref<any>({ name: '', description: '', parentPoolId: props.parentId, runtimeProvider: undefined })
const saving = ref(false)
watch(open, (v) => { if (v) form.value = { name: '', description: '', parentPoolId: props.parentId, runtimeProvider: undefined } })
async function submit() {
  if (!form.value.name.trim()) { message.warning('请填写名称'); return }
  if (!form.value.parentPoolId && !auth.isAdmin) { message.warning('请选择父 Pool（顶级 Pool 仅平台管理员可创建）'); return }
  saving.value = true
  try { const p = await poolApi.create(form.value); message.success('Pool 已创建'); open.value = false; emit('created', p) } finally { saving.value = false }
}
</script>

<template>
  <a-modal v-model:open="open" title="新建 Pool" :confirm-loading="saving" ok-text="创建" cancel-text="取消" @ok="submit">
    <a-form layout="vertical">
      <a-form-item label="父 Pool"><PoolTreeSelect v-model="form.parentPoolId" manage-only allow-clear :placeholder="auth.isAdmin ? '不选则创建为顶级 Pool' : '选择父 Pool'" /></a-form-item>
      <a-form-item label="名称" required><a-input v-model:value="form.name" placeholder="例如：编辑部" /></a-form-item>
      <a-form-item label="描述"><a-textarea v-model:value="form.description" :rows="2" placeholder="部门 / 项目组 / 业务线 / 专题组…" /></a-form-item>
      <a-form-item v-if="auth.isAdmin && auth.meta?.runtimeProviders?.length > 1" label="Runtime Provider">
        <a-select v-model:value="form.runtimeProvider" allow-clear placeholder="默认继承父级 / 平台默认" :options="auth.meta.runtimeProviders.map((p: string) => ({ value: p, label: p }))" />
      </a-form-item>
    </a-form>
    <div class="note"><span>创建者自动成为该 Pool 的所有者。子 Pool 可访问父 Pool 向下继承的共享资源，父 Pool 的私有资源不会自动向上暴露。</span></div>
  </a-modal>
</template>
