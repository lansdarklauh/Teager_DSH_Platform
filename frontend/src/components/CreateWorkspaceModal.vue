<script setup lang="ts">
import { ref, watch } from 'vue'
import { message } from 'ant-design-vue'
import { wsApi } from '@/api'
import PoolTreeSelect from './PoolTreeSelect.vue'

/** 新建 Workspace：选择所属 Pool（一个 Workspace 只属于一个 Pool）。 */
const open = defineModel<boolean>('open', { required: true })
const props = defineProps<{ poolId?: string }>()
const emit = defineEmits<{ created: [ws: any] }>()
const form = ref<any>({ poolId: props.poolId, name: '', description: '', inheritPoolMembers: true })
const saving = ref(false)
watch(open, (v) => { if (v) form.value = { poolId: props.poolId, name: '', description: '', inheritPoolMembers: true } })
async function submit() {
  if (!form.value.poolId || !form.value.name.trim()) { message.warning('请选择所属 Pool 并填写名称'); return }
  saving.value = true
  try { const ws = await wsApi.create(form.value); message.success('Workspace 已创建'); open.value = false; emit('created', ws) } finally { saving.value = false }
}
</script>

<template>
  <a-modal v-model:open="open" title="新建 Workspace" :confirm-loading="saving" ok-text="创建" cancel-text="取消" @ok="submit">
    <a-form layout="vertical">
      <a-form-item label="所属 Pool" required><PoolTreeSelect v-model="form.poolId" placeholder="选择所属 Pool" /></a-form-item>
      <a-form-item label="名称" required><a-input v-model:value="form.name" placeholder="例如：酒店研究" /></a-form-item>
      <a-form-item label="描述"><a-textarea v-model:value="form.description" :rows="2" /></a-form-item>
      <a-form-item>
        <a-checkbox v-model:checked="form.inheritPoolMembers">继承 Pool 有效成员的访问权限</a-checkbox>
        <div class="muted small">关闭后仅显式添加的成员可进入该 Workspace。</div>
      </a-form-item>
    </a-form>
  </a-modal>
</template>
