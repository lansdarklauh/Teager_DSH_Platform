<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { message, Modal } from 'ant-design-vue'
import { KeyOutlined, PlusOutlined, CopyOutlined, StopOutlined } from '@ant-design/icons-vue'
import { tokenApi, wsApi, userApi, spaceApi } from '@/api'
import { useAuthStore } from '@/stores/auth'
import { copyText, fmtAgo, fmtTime } from '@/utils/format'
import PageHeader from '@/components/PageHeader.vue'
import PoolTreeMultiSelect from '@/components/PoolTreeMultiSelect.vue'
import SdkSnippet from '@/components/SdkSnippet.vue'

const auth = useAuthStore()
const rows = ref<any[]>([])
const loading = ref(false)
const all = ref(false)
const open = ref(false)
const created = ref<any>(null)
const workspaces = ref<any[]>([])
const sharedSpaces = ref<any[]>([])
const services = ref<any[]>([])
const saving = ref(false)
const form = ref<any>({})

/** Scope 分组与说明（对应产品方案第 26 节）。 */
const allScopeGroups = [
  { title: 'Pool', items: [['pool:read', '读取 Pool'], ['pool:manage', '管理 Pool']] },
  { title: 'Workspace', items: [['workspace:read', '读取'], ['workspace:write', '写入'], ['workspace:execute', '执行（终端）'], ['workspace:manage', '管理']] },
  { title: 'Session', items: [['session:read', '读取'], ['session:create', '创建'], ['session:execute', '发送消息 / 执行']] },
  { title: '文件', items: [['files:read', '读取'], ['files:write', '写入']] },
  { title: '共享空间', items: [['share:read', '读取'], ['share:write', '写入'], ['share:manage', '管理']] },
  { title: 'Runtime', items: [['runtime:read', '读取'], ['runtime:manage', '管理']] }
]
const allPresets = [
  { label: '只读', scopes: ['pool:read', 'workspace:read', 'session:read', 'files:read', 'share:read', 'runtime:read'] },
  { label: 'SDK 会话', scopes: ['workspace:read', 'session:read', 'session:create', 'session:execute', 'files:read', 'files:write'] },
  { label: '全部', scopes: allScopeGroups.flatMap((g) => g.items.map((i) => i[0])) }
]
const forbiddenScopes = ['pool:manage', 'workspace:write', 'workspace:manage', 'share:manage', 'runtime:read', 'runtime:manage']
const scopeGroups = computed(() => allScopeGroups.map(g => ({ ...g, items: g.items.filter(i => auth.isAdmin || !forbiddenScopes.includes(i[0])) })).filter(g => g.items.length))
const presets = computed(() => allPresets.filter(p => auth.isAdmin || p.label !== '全部').map(p => ({ ...p, scopes: p.scopes.filter(scope => auth.isAdmin || !forbiddenScopes.includes(scope)) })))
function blank() { return { name: '', type: 'Personal', scopes: [...presets.value[1].scopes], allowedPools: [], allowedWorkspaces: auth.isAdmin ? [] : [auth.user.defaultWorkspaceId], expiresInDays: 90, expiresInHours: 1, serviceAccountId: undefined } }

async function load() { loading.value = true; try { rows.value = (await tokenApi.list(all.value)) as any[] } finally { loading.value = false } }
async function openCreate() {
  form.value = blank(); sharedSpaces.value = await spaceApi.list() as any[]; open.value = true
  workspaces.value = (await wsApi.list()) as any[]
  if (auth.isAdmin) services.value = ((await userApi.list()) as any[]).filter((u) => u.isServiceAccount)
}
async function create() {
  saving.value = true
  try {
    const r: any = await tokenApi.create({ name: form.value.name, type: form.value.type, scopes: form.value.scopes, allowedPools: form.value.allowedPools, allowedWorkspaces: form.value.allowedWorkspaces, allowedSharedSpaces: form.value.allowedSharedSpaces || [],
      expiresInDays: form.value.type === 'Temporary' ? undefined : form.value.expiresInDays, expiresInHours: form.value.type === 'Temporary' ? form.value.expiresInHours : undefined, serviceAccountId: form.value.serviceAccountId })
    created.value = r; open.value = false; load()
  } finally { saving.value = false }
}
function revoke(r: any) { Modal.confirm({ title: `吊销 Token「${r.name}」？`, content: '吊销后立即失效，使用它的 SDK / 系统将无法访问。', okType: 'danger', okText: '吊销', cancelText: '取消', onOk: async () => { await tokenApi.revoke(r.id); load() } }) }
async function copy(t: string) { await copyText(t); message.success('已复制') }
onMounted(load)
const typeText: Record<string, string> = { Personal: '个人', Service: '服务', Temporary: '临时' }
const statusCls = (s: string) => (s === '有效' ? 'ok' : 'danger')
const typeOptions = computed(() => [{ label: '个人 Token', value: 'Personal' }, { label: '临时 Token（短期）', value: 'Temporary' }, ...(auth.isAdmin ? [{ label: '服务 Token', value: 'Service' }] : [])])
</script>

<template>
  <div class="page" data-mod="token">
    <PageHeader title="API Token" sub="SDK / API 使用统一的 Bearer Token：Authorization: Bearer dshp_…；可限定 Scope 与可访问范围">
      <template #icon><KeyOutlined /></template>
      <template #actions><a-checkbox v-if="auth.isAdmin" v-model:checked="all" @change="load">查看全部用户的 Token</a-checkbox><a-button type="primary" @click="openCreate"><PlusOutlined /> 创建 Token</a-button></template>
    </PageHeader>
    <div class="stack">
      <div class="card flush">
        <a-table :data-source="rows" :loading="loading" row-key="id" :pagination="{ pageSize: 10, hideOnSinglePage: true }" size="middle" :locale="{ emptyText: '还没有 Token' }">
          <a-table-column title="名称" key="n"><template #default="{ record }"><b>{{ record.name }}</b><div class="mono muted small">{{ record.tokenPrefix }}…　<span v-if="all || auth.isAdmin">{{ record.owner }}</span></div></template></a-table-column>
          <a-table-column title="类型" key="t" :width="90"><template #default="{ record }"><span class="tag token">{{ typeText[record.type] }}</span></template></a-table-column>
          <a-table-column title="Scope" key="s"><template #default="{ record }"><a-tooltip :title="record.scopes.join('、')"><span class="tag">{{ record.scopes.length }} 项</span></a-tooltip> <span v-if="record.allowedPools.length" class="tag pool">限 {{ record.allowedPools.length }} 个 Pool</span> <span v-if="record.allowedWorkspaces.length" class="tag ws">限 {{ record.allowedWorkspaces.length }} 个 Workspace</span></template></a-table-column>
          <a-table-column title="状态" key="st" :width="90"><template #default="{ record }"><span class="tag dot" :class="statusCls(record.status)">{{ record.status }}</span></template></a-table-column>
          <a-table-column title="过期 / 最近使用" key="e" :width="200"><template #default="{ record }"><div class="small"><span class="muted">过期</span> {{ record.expiresAt ? fmtTime(record.expiresAt).slice(0, 16) : '永不' }}</div><div class="small"><span class="muted">使用</span> {{ fmtAgo(record.lastUsedAt) }}</div></template></a-table-column>
          <a-table-column title="" key="op" :width="90" align="right"><template #default="{ record }"><a-button v-if="!record.revokedAt" type="text" size="small" danger @click="revoke(record)"><StopOutlined /> 吊销</a-button></template></a-table-column>
        </a-table>
      </div>
      <SdkSnippet />
    </div>

    <a-modal v-model:open="open" title="创建 API Token" :confirm-loading="saving" ok-text="创建" cancel-text="取消" width="760px" @ok="create">
      <a-form layout="vertical">
        <div class="grid grid-2">
          <a-form-item label="名称" required><a-input v-model:value="form.name" placeholder="例如：CI 流水线 / 我的脚本" /></a-form-item>
          <a-form-item label="类型"><a-select v-model:value="form.type" :options="typeOptions" /></a-form-item>
          <a-form-item v-if="form.type === 'Service'" label="服务账号" required><a-select v-model:value="form.serviceAccountId" placeholder="选择服务账号（在“用户管理”中创建）" :options="services.map((s) => ({ value: s.id, label: s.displayName + '（' + s.username + '）' }))" /></a-form-item>
          <a-form-item v-if="form.type === 'Temporary'" label="有效小时（1-24）"><a-input-number v-model:value="form.expiresInHours" :min="1" :max="24" style="width: 100%" /></a-form-item>
          <a-form-item v-else label="有效天数"><a-input-number v-model:value="form.expiresInDays" :min="1" :max="form.type === 'Service' ? 730 : 365" style="width: 100%" /></a-form-item>
        </div>
        <a-form-item label="Scope（权限范围；实际能力仍受你自身权限约束）">
          <div class="row-wrap" style="margin-bottom: 10px"><span class="muted small">快速选择：</span><a-button v-for="p in presets" :key="p.label" size="small" @click="form.scopes = [...p.scopes]">{{ p.label }}</a-button></div>
          <a-checkbox-group v-model:value="form.scopes" style="width: 100%">
            <div class="scopes"><div v-for="g in scopeGroups" :key="g.title" class="sg"><div class="muted small" style="margin-bottom: 6px">{{ g.title }}</div><div class="col"><a-checkbox v-for="i in g.items" :key="i[0]" :value="i[0]">{{ i[1] }}</a-checkbox></div></div></div>
          </a-checkbox-group>
        </a-form-item>
        <div class="grid grid-2">
          <a-form-item v-if="auth.isAdmin" label="限定 Pool（含其子 Pool；留空不限）"><PoolTreeMultiSelect v-model="form.allowedPools" /></a-form-item>
          <a-form-item label="限定 Workspace（普通用户仅自己的空间）"><a-select v-model:value="form.allowedWorkspaces" mode="multiple" placeholder="选择 Workspace" :options="workspaces.map((w) => ({ value: w.id, label: `${w.name}（${w.poolName}）` }))" option-filter-prop="label" /></a-form-item>
        </div>
        <a-form-item label="限定共享空间（留空无法通过 Token 访问共享资源）"><a-select v-model:value="form.allowedSharedSpaces" mode="multiple" :options="sharedSpaces.map(s => ({ value: s.id, label: `${s.name}（${s.my?.permission || '只读'}）` }))" option-filter-prop="label" /></a-form-item>
      </a-form>
    </a-modal>

    <a-modal :open="!!created" title="Token 已创建" :footer="null" :closable="true" @cancel="created = null" width="640px">
      <a-alert type="warning" show-icon message="请立即复制并妥善保存：Token 明文只显示这一次，之后无法再次查看。" style="margin-bottom: 18px" />
      <div class="tokenbox mono">{{ created?.token }}</div>
      <div class="row mt-16"><span class="muted small">到期：{{ created?.expiresAt ? fmtTime(created.expiresAt) : '永不' }}</span><span class="spacer" /><a-button type="primary" @click="copy(created.token)"><CopyOutlined /> 复制 Token</a-button></div>
    </a-modal>
  </div>
</template>



<style scoped>
.scopes { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 18px 24px; padding: 16px 18px; background: var(--surface-2); border-radius: 12px; }
.col { display: flex; flex-direction: column; gap: 6px; }
.tokenbox { padding: 16px 18px; background: #0f172a; color: #a5f3fc; border-radius: 12px; word-break: break-all; font-size: 14px; user-select: all; }
</style>
