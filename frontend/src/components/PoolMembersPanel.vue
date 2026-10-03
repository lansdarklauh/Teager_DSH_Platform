<script setup lang="ts">
import { ref, watch } from 'vue'
import { message, Modal } from 'ant-design-vue'
import { PlusOutlined, DeleteOutlined } from '@ant-design/icons-vue'
import { poolApi } from '@/api'
import { fmtAgo } from '@/utils/format'
import Tags from './Tags.vue'
import UserSelect from './UserSelect.vue'

/** Pool 成员：直接成员（可增删改）+ 继承成员（来自子孙 Pool 的成员，天然是本 Pool 的“有效成员”）。 */
const props = defineProps<{ poolId: string; canManage: boolean }>()
const direct = ref<any[]>([])
const inherited = ref<any[]>([])
const loading = ref(false)
const open = ref(false)
const form = ref<any>({ userId: undefined, role: 'Member' })
const roles = [{ value: 'Owner', label: '所有者' }, { value: 'Admin', label: '管理员' }, { value: 'Member', label: '成员' }, { value: 'Viewer', label: '只读' }]
async function load() { loading.value = true; try { const r: any = await poolApi.members(props.poolId); direct.value = r.direct; inherited.value = r.inherited } finally { loading.value = false } }
watch(() => props.poolId, load, { immediate: true })
async function add() {
  if (!form.value.userId) { message.warning('请选择用户'); return }
  await poolApi.addMember(props.poolId, form.value.userId, form.value.role); message.success('成员已添加'); open.value = false; form.value = { userId: undefined, role: 'Member' }; load()
}
async function changeRole(r: any, role: string) { await poolApi.setRole(props.poolId, r.userId, role); message.success('角色已更新'); load() }
function remove(r: any) {
  Modal.confirm({ title: `移除成员「${r.displayName}」？`, content: '移除后其在本 Pool 及其子 Pool 的继承权限将立即失效。', okType: 'danger', okText: '移除', cancelText: '取消', onOk: async () => { await poolApi.removeMember(props.poolId, r.userId); load() } })
}
</script>

<template>
  <div class="stack" data-mod="pool">
    <div class="card flush">
      <div class="card-head"><h3>直接成员<span class="muted small" style="font-weight: 400">　直接加入本 Pool 的用户</span></h3><a-button v-if="canManage" type="primary" @click="open = true"><PlusOutlined /> 添加成员</a-button></div>
      <a-table :data-source="direct" :loading="loading" row-key="userId" :pagination="false" size="middle" :locale="{ emptyText: '暂无直接成员' }">
        <a-table-column title="用户" key="u"><template #default="{ record }"><div class="row"><span class="chip sm">{{ record.displayName.slice(0, 1) }}</span><div><b>{{ record.displayName }}</b><div class="muted small">{{ record.username }}</div></div></div></template></a-table-column>
        <a-table-column title="角色" key="r" :width="200">
          <template #default="{ record }">
            <a-select v-if="canManage" :value="record.role" :options="roles" size="small" style="width: 110px" @change="(v: any) => changeRole(record, v)" />
            <Tags v-else kind="role" :value="record.role" />
          </template>
        </a-table-column>
        <a-table-column title="加入时间" key="t" :width="140"><template #default="{ record }"><span class="muted">{{ fmtAgo(record.createdAt) }}</span></template></a-table-column>
        <a-table-column v-if="canManage" title="操作" key="op" :width="80" align="right"><template #default="{ record }"><a-button type="text" size="small" danger @click="remove(record)"><DeleteOutlined /></a-button></template></a-table-column>
      </a-table>
    </div>
    <div class="card flush">
      <div class="card-head"><h3>继承成员<span class="muted small" style="font-weight: 400">　来自子 Pool，自动成为本 Pool 的有效成员（只读关系，不在此处维护）</span></h3></div>
      <a-table :data-source="inherited" row-key="userId" :pagination="false" size="middle" :locale="{ emptyText: '没有来自子 Pool 的继承成员' }">
        <a-table-column title="用户" key="u"><template #default="{ record }"><div class="row"><span class="chip sm" data-mod="space">{{ record.displayName.slice(0, 1) }}</span><div><b>{{ record.displayName }}</b><div class="muted small">{{ record.username }}</div></div></div></template></a-table-column>
        <a-table-column title="来源路径" key="p"><template #default="{ record }"><span class="tag space">{{ record.path.join(' → ') }}</span></template></a-table-column>
        <a-table-column title="在子 Pool 的角色" key="r" :width="160"><template #default="{ record }"><Tags kind="role" :value="record.role" /></template></a-table-column>
      </a-table>
    </div>
    <a-modal v-model:open="open" title="添加成员" ok-text="添加" cancel-text="取消" @ok="add">
      <a-form layout="vertical"><a-form-item label="用户" required><UserSelect v-model="form.userId" /></a-form-item><a-form-item label="角色"><a-select v-model:value="form.role" :options="roles" /></a-form-item></a-form>
      <div class="note"><span>一个用户可以同时直接加入多个 Pool（多 Membership），但一个 Pool 只能有一个父 Pool。</span></div>
    </a-modal>
  </div>
</template>
