<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { message } from 'ant-design-vue'
import { TeamOutlined, PlusOutlined, KeyOutlined } from '@ant-design/icons-vue'
import { http } from '@/api/http'
import { userApi, wsApi } from '@/api'
import { fmtAgo } from '@/utils/format'
import PageHeader from '@/components/PageHeader.vue'

const rows = ref<any[]>([])
const loading = ref(false)
const keyword = ref('')
const open = ref(false)
const form = ref<any>({})
const saving = ref(false)
const edit = ref<any>({ open: false })
const pwd = ref<any>({ open: false, id: '', name: '', value: '' })
const assignment = ref<any>({ open: false })
const workspaces = ref<any[]>([])
async function assign(r: any) {
  const [spaces, users] = await Promise.all([wsApi.list(), userApi.list()]) as [any[], any[]]
  const occupied = new Set(users.filter(u => u.id !== r.id && u.defaultWorkspaceId).map(u => u.defaultWorkspaceId))
  workspaces.value = spaces.filter(w => !occupied.has(w.id))
  assignment.value = { open: true, id: r.id, workspaceId: users.find(u => u.id === r.id)?.defaultWorkspaceId }
}
async function saveAssignment() { await http.put(`/users/${assignment.value.id}/workspace`, { workspaceId: assignment.value.workspaceId }); assignment.value.open = false; message.success('个人工作空间已分配'); await load() }
async function load() { loading.value = true; try { rows.value = (await userApi.list(keyword.value)) as any[] } finally { loading.value = false } }
onMounted(load)
function blank() { return { username: '', displayName: '', email: '', password: '', role: 'User', isServiceAccount: false } }
async function create() {
  saving.value = true
  try { await userApi.create(form.value); message.success('已创建'); open.value = false; load() } finally { saving.value = false }
}
async function saveEdit() { await userApi.update(edit.value.id, { displayName: edit.value.displayName, email: edit.value.email, role: edit.value.role, status: edit.value.status }); message.success('已保存'); edit.value.open = false; load() }
async function resetPwd() { await userApi.resetPassword(pwd.value.id, pwd.value.value); message.success('密码已重置，该用户所有登录已失效'); pwd.value.open = false }
</script>

<template>
  <div class="page" data-mod="admin">
    <PageHeader title="用户管理" sub="平台账号与服务账号；Pool 成员关系在各 Pool 的“成员”页维护">
      <template #icon><TeamOutlined /></template>
      <template #actions><a-input-search v-model:value="keyword" placeholder="搜索登录名 / 显示名" style="width: 240px" allow-clear @search="load" /><a-button type="primary" @click="form = blank(); open = true"><PlusOutlined /> 新建用户</a-button></template>
    </PageHeader>
    <div class="card flush">
      <a-table :data-source="rows" :loading="loading" row-key="id" :pagination="{ pageSize: 15, hideOnSinglePage: true }" size="middle">
        <a-table-column title="用户" key="u"><template #default="{ record }"><div class="row"><span class="chip sm">{{ record.displayName.slice(0, 1) }}</span><div><b>{{ record.displayName }}</b><span v-if="record.isServiceAccount" class="tag token" style="margin-left: 8px">服务账号</span><div class="muted small">{{ record.username }}<span v-if="record.email"> · {{ record.email }}</span></div></div></div></template></a-table-column>
        <a-table-column title="平台角色" key="r" :width="120"><template #default="{ record }"><span class="tag" :class="record.role === 'Admin' ? 'audit' : ''">{{ record.role === 'Admin' ? '平台管理员' : '普通用户' }}</span></template></a-table-column>
        <a-table-column title="状态" key="s" :width="100"><template #default="{ record }"><span class="tag dot" :class="record.status === 'Active' ? 'ok' : 'danger'">{{ record.status === 'Active' ? '正常' : '已禁用' }}</span></template></a-table-column>
        <a-table-column title="最近登录" key="l" :width="130"><template #default="{ record }"><span class="muted">{{ fmtAgo(record.lastLoginAt) }}</span></template></a-table-column>
        <a-table-column title="操作" key="op" :width="190" align="right"><template #default="{ record }"><a-space :size="4"><a-button type="text" size="small" @click="edit = { open: true, ...record }">编辑</a-button><a-button v-if="record.role !== 'Admin' && !record.isServiceAccount" type="text" size="small" @click="assign(record)">分配工作空间</a-button><a-button v-if="!record.isServiceAccount" type="text" size="small" @click="pwd = { open: true, id: record.id, name: record.displayName, value: '' }"><KeyOutlined /> 重置密码</a-button></a-space></template></a-table-column>
      </a-table>
    </div>

    <a-modal v-model:open="open" title="新建用户" :confirm-loading="saving" ok-text="创建" cancel-text="取消" @ok="create">
      <a-form layout="vertical">
        <a-form-item><a-checkbox v-model:checked="form.isServiceAccount">服务账号（无密码，仅用于服务 Token / 系统集成）</a-checkbox></a-form-item>
        <div class="grid grid-2">
          <a-form-item label="登录名" required><a-input v-model:value="form.username" /></a-form-item>
          <a-form-item label="显示名"><a-input v-model:value="form.displayName" /></a-form-item>
        </div>
        <a-form-item label="邮箱"><a-input v-model:value="form.email" /></a-form-item>
        <a-form-item v-if="!form.isServiceAccount" label="初始密码（至少 8 位，含字母和数字）" required><a-input-password v-model:value="form.password" autocomplete="new-password" /></a-form-item>
        <a-form-item v-if="!form.isServiceAccount" label="平台角色"><a-radio-group v-model:value="form.role"><a-radio value="User">普通用户</a-radio><a-radio value="Admin">平台管理员</a-radio></a-radio-group></a-form-item>
      </a-form>
    </a-modal>
    <a-modal v-model:open="edit.open" title="编辑用户" ok-text="保存" cancel-text="取消" @ok="saveEdit">
      <a-form layout="vertical">
        <a-form-item label="登录名"><a-input :value="edit.username" disabled /></a-form-item>
        <a-form-item label="显示名"><a-input v-model:value="edit.displayName" /></a-form-item>
        <a-form-item label="邮箱"><a-input v-model:value="edit.email" /></a-form-item>
        <a-form-item v-if="!edit.isServiceAccount" label="平台角色"><a-radio-group v-model:value="edit.role"><a-radio value="User">普通用户</a-radio><a-radio value="Admin">平台管理员</a-radio></a-radio-group></a-form-item>
        <a-form-item label="状态"><a-radio-group v-model:value="edit.status"><a-radio value="Active">正常</a-radio><a-radio value="Disabled">禁用（立即退出登录并使 Token 失效）</a-radio></a-radio-group></a-form-item>
      </a-form>
    </a-modal>
    <a-modal v-model:open="assignment.open" title="分配个人工作空间" ok-text="分配" cancel-text="取消" @ok="saveAssignment"><a-alert type="info" message="分配后旧空间访问立即失效；不能分配已属于其他用户的空间。" style="margin-bottom: 16px" /><a-select v-model:value="assignment.workspaceId" :options="workspaces.map(w => ({ value: w.id, label: w.name }))" style="width: 100%" /></a-modal>
    <a-modal v-model:open="pwd.open" :title="`重置「${pwd.name}」的密码`" ok-text="重置" cancel-text="取消" @ok="resetPwd"><a-input-password v-model:value="pwd.value" placeholder="新密码（至少 8 位，含字母和数字）" autocomplete="new-password" /></a-modal>
  </div>
</template>
