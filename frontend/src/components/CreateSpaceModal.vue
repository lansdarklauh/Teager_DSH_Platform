<script setup lang="ts">
import { ref, watch } from 'vue'
import { message } from 'ant-design-vue'
import { spaceApi } from '@/api'
import { useAuthStore } from '@/stores/auth'
import PoolTreeSelect from './PoolTreeSelect.vue'

/** 新建共享空间：所属 Pool 默认获得 RW 授权且不向子 Pool 继承（最小权限），可在授权页调整。 */
const open = defineModel<boolean>('open', { required: true })
const props = defineProps<{ poolId?: string }>()
const emit = defineEmits<{ created: [space: any] }>()
const auth = useAuthStore()
const form = ref<any>({ name: '', description: '', ownerPoolId: props.poolId, quotaMb: 0, mountName: '' })
const saving = ref(false)
watch(open, (v) => { if (v) form.value = { name: '', description: '', ownerPoolId: props.poolId, quotaMb: 0, mountName: '' } })
async function submit() {
  if (!form.value.name.trim()) { message.warning('请填写名称'); return }
  if (!form.value.ownerPoolId && !auth.isAdmin) { message.warning('请选择所属 Pool'); return }
  saving.value = true
  try { const s = await spaceApi.create(form.value); message.success('共享空间已创建'); open.value = false; emit('created', s) } finally { saving.value = false }
}
</script>

<template>
  <a-modal v-model:open="open" title="新建共享空间" :confirm-loading="saving" ok-text="创建" cancel-text="取消" @ok="submit">
    <a-form layout="vertical">
      <a-form-item label="所属 Pool"><PoolTreeSelect v-model="form.ownerPoolId" manage-only allow-clear :placeholder="auth.isAdmin ? '不选则为组织级共享空间' : '选择所属 Pool'" /></a-form-item>
      <a-form-item label="名称" required><a-input v-model:value="form.name" placeholder="例如：集团公共资料" /></a-form-item>
      <a-form-item label="描述"><a-textarea v-model:value="form.description" :rows="2" /></a-form-item>
      <div class="grid grid-2">
        <a-form-item label="默认挂载名"><a-input v-model:value="form.mountName" placeholder="留空自动生成，如 group-public" /></a-form-item>
        <a-form-item label="配额（MB，0 不限）"><a-input-number v-model:value="form.quotaMb" :min="0" style="width: 100%" /></a-form-item>
      </div>
    </a-form>
    <div class="note" data-mod="space"><span>Agent 会在 <span class="mono">shared/&lt;挂载名&gt;/</span> 下看到该目录。共享空间需要先<b>授权</b>给 Pool，再由 Workspace <b>挂载</b>后才会出现（权限 ≠ 挂载）。</span></div>
  </a-modal>
</template>
