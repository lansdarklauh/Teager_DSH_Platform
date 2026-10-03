<script setup lang="ts">
import { ref, watch } from 'vue'
import { message, Modal } from 'ant-design-vue'
import { PlusOutlined, DeleteOutlined, LockOutlined } from '@ant-design/icons-vue'
import { secretApi } from '@/api'
import { fmtAgo } from '@/utils/format'

/** 密钥管理：只写不读；值经 AES-GCM 加密，仅在启动 Runtime 时注入为环境变量，优先级 Workspace > Pool > 组织。 */
const props = defineProps<{ scope: 'Platform' | 'Pool' | 'Workspace'; scopeId?: string }>()
const rows = ref<any[]>([])
const loading = ref(false)
const open = ref(false)
const form = ref({ name: '', value: '', description: '' })
const saving = ref(false)
async function load() { loading.value = true; try { rows.value = (await secretApi.list(props.scope, props.scopeId)) as any[] } finally { loading.value = false } }
watch(() => [props.scope, props.scopeId], load, { immediate: true })
async function save() {
  saving.value = true
  try { await secretApi.upsert({ scope: props.scope, scopeId: props.scopeId, ...form.value }); message.success('密钥已保存（仅保存加密值）'); open.value = false; form.value = { name: '', value: '', description: '' }; load() } finally { saving.value = false }
}
function remove(r: any) { Modal.confirm({ title: `删除密钥 ${r.name}？`, okType: 'danger', okText: '删除', cancelText: '取消', onOk: async () => { await secretApi.remove(r.id); load() } }) }
function edit(r: any) { form.value = { name: r.name, value: '', description: r.description || '' }; open.value = true }
</script>

<template>
  <div class="stack">
    <div class="note warn"><LockOutlined />密钥只写不读：保存后无法再查看明文，界面与接口都不会返回它。名称即注入 Runtime 的环境变量名（如 <span class="mono">DEEPSEEK_API_KEY</span>）；优先级 Workspace &gt; Pool &gt; 组织，不会写入 Workspace 目录。</div>
    <div class="card flush">
      <div class="card-head"><h3>密钥列表</h3><a-button type="primary" @click="open = true"><PlusOutlined /> 新增密钥</a-button></div>
      <a-table :data-source="rows" :loading="loading" :pagination="false" row-key="id" size="middle" :locale="{ emptyText: '还没有密钥' }">
        <a-table-column title="名称（环境变量）" key="n"><template #default="{ record }"><span class="mono"><b>{{ record.name }}</b></span></template></a-table-column>
        <a-table-column title="值" key="v" :width="120"><template #default><span class="muted">●●●●●●●●</span></template></a-table-column>
        <a-table-column title="描述" data-index="description" key="d" />
        <a-table-column title="更新" key="u" :width="170"><template #default="{ record }"><span class="muted small">{{ record.updatedBy }} · {{ fmtAgo(record.updatedAt) }}</span></template></a-table-column>
        <a-table-column title="操作" key="op" :width="130" align="right"><template #default="{ record }"><a-button type="text" size="small" @click="edit(record)">更新值</a-button><a-button type="text" size="small" danger @click="remove(record)"><DeleteOutlined /></a-button></template></a-table-column>
      </a-table>
    </div>
    <a-modal v-model:open="open" title="新增 / 更新密钥" :confirm-loading="saving" ok-text="保存" cancel-text="取消" @ok="save">
      <a-form layout="vertical">
        <a-form-item label="名称（环境变量名）" required><a-input v-model:value="form.name" placeholder="DEEPSEEK_API_KEY" class="mono" /></a-form-item>
        <a-form-item label="值" required><a-input-password v-model:value="form.value" placeholder="仅本次可见，保存后不可再读取" /></a-form-item>
        <a-form-item label="描述"><a-input v-model:value="form.description" /></a-form-item>
      </a-form>
    </a-modal>
  </div>
</template>
