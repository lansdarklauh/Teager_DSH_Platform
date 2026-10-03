<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { message, Modal } from 'ant-design-vue'
import { PlusOutlined, DeleteOutlined } from '@ant-design/icons-vue'
import { wsApi } from '@/api'
import Tags from './Tags.vue'
import UserSelect from './UserSelect.vue'

/** Workspace 成员：显式成员 / 显式拒绝 + 我的访问来源。默认继承 Pool 有效成员；显式拒绝优先于一切授权。 */
const props = defineProps<{ workspaceId: string; canManage: boolean; inheritPoolMembers?: boolean }>()
const data = ref<any>({ members: [], myAccess: null })
const loading = ref(false)
const open = ref(false)
const form = ref<any>({ userId: undefined, role: 'Member', deny: false })
const roles = [{ value: 'Owner', label: '所有者' }, { value: 'Admin', label: '管理员' }, { value: 'Member', label: '成员' }, { value: 'Viewer', label: '只读' }]
async function load() { loading.value = true; try { data.value = await wsApi.members(props.workspaceId) } finally { loading.value = false } }
watch(() => props.workspaceId, load)
onMounted(load)
async function save() {
  if (!form.value.userId) { message.warning('请选择用户'); return }
  await wsApi.setMember(props.workspaceId, { userId: form.value.userId, role: form.value.role, effect: form.value.deny ? 'Deny' : 'Allow' })
  message.success('已保存'); open.value = false; form.value = { userId: undefined, role: 'Member', deny: false }; load()
}
async function changeRole(r: any, role: string) { await wsApi.setMember(props.workspaceId, { userId: r.userId, role, effect: 'Allow' }); load() }
function remove(r: any) { Modal.confirm({ title: `移除「${r.displayName}」的显式记录？`, content: '移除后该用户是否可访问，取决于 Pool 成员继承。', okType: 'danger', okText: '移除', cancelText: '取消', onOk: async () => { await wsApi.removeMember(props.workspaceId, r.userId); load() } }) }
const my = computed(() => data.value.myAccess)
</script>

<template>
  <div class="stack" data-mod="ws">
    <div v-if="my" class="note"><span>我的访问：<Tags kind="role" :value="my.role" />　来源 <Tags kind="source" :value="my.source" /><template v-if="my.path?.length">　路径：{{ my.path.join(' → ') }}</template>　·　<b>{{ inheritPoolMembers ? '已启用“继承 Pool 有效成员”' : '未继承 Pool 成员，仅显式成员可进入' }}</b></span></div>
    <div class="card flush">
      <div class="card-head"><h3>显式成员与拒绝</h3><a-button v-if="canManage" type="primary" @click="open = true"><PlusOutlined /> 添加</a-button></div>
      <a-table :data-source="data.members" :loading="loading" row-key="id" :pagination="false" size="middle" :locale="{ emptyText: '没有显式成员（访问来自 Pool 成员继承）' }">
        <a-table-column title="用户" key="u"><template #default="{ record }"><div class="row"><span class="chip sm">{{ record.displayName.slice(0, 1) }}</span><div><b>{{ record.displayName }}</b><div class="muted small">{{ record.username }}</div></div></div></template></a-table-column>
        <a-table-column title="类型 / 角色" key="r" :width="220">
          <template #default="{ record }">
            <span v-if="record.effect === 'Deny'" class="tag danger dot">显式拒绝</span>
            <a-select v-else-if="canManage" :value="record.role" :options="roles" size="small" style="width: 110px" @change="(v: any) => changeRole(record, v)" />
            <Tags v-else kind="role" :value="record.role" />
          </template>
        </a-table-column>
        <a-table-column v-if="canManage" title="" key="op" :width="70" align="right"><template #default="{ record }"><a-button type="text" size="small" danger @click="remove(record)"><DeleteOutlined /></a-button></template></a-table-column>
      </a-table>
    </div>
    <a-modal v-model:open="open" title="添加显式成员 / 拒绝" ok-text="保存" cancel-text="取消" @ok="save">
      <a-form layout="vertical">
        <a-form-item label="用户" required><UserSelect v-model="form.userId" /></a-form-item>
        <a-form-item><a-checkbox v-model:checked="form.deny">显式拒绝该用户访问（优先于 Pool 继承与其他授权）</a-checkbox></a-form-item>
        <a-form-item v-if="!form.deny" label="角色"><a-select v-model:value="form.role" :options="roles" /></a-form-item>
      </a-form>
    </a-modal>
  </div>
</template>
