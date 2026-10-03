<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ApartmentOutlined, AppstoreOutlined, TeamOutlined, FolderOpenOutlined, ThunderboltOutlined, DatabaseOutlined, PlusOutlined, CloudServerOutlined } from '@ant-design/icons-vue'
import { poolApi } from '@/api'
import { fmtBytes, sourceText } from '@/utils/format'
import PageHeader from '@/components/PageHeader.vue'
import StatCard from '@/components/StatCard.vue'
import Tags from '@/components/Tags.vue'
import WorkspaceCard from '@/components/WorkspaceCard.vue'
import PoolMembersPanel from '@/components/PoolMembersPanel.vue'
import PoolSpacesPanel from '@/components/PoolSpacesPanel.vue'
import ConfigPanel from '@/components/ConfigPanel.vue'
import SecretPanel from '@/components/SecretPanel.vue'
import SdkSnippet from '@/components/SdkSnippet.vue'
import RuntimePanel from '@/components/RuntimePanel.vue'
import UsagePanel from '@/components/UsagePanel.vue'
import AuditPanel from '@/components/AuditPanel.vue'
import PolicyPanel from '@/components/PolicyPanel.vue'
import PoolSettingsPanel from '@/components/PoolSettingsPanel.vue'
import CreatePoolModal from '@/components/CreatePoolModal.vue'
import CreateWorkspaceModal from '@/components/CreateWorkspaceModal.vue'

const route = useRoute()
const router = useRouter()
const id = computed(() => route.params.id as string)
const pool = ref<any>(null)
const loading = ref(true)
const tab = ref((route.query.tab as string) || 'overview')
const showChild = ref(false)
const showWs = ref(false)
const workspaces = ref<any[]>([])
const includeSub = ref(false)
const children = ref<any[]>([])
const grantedSpaces = ref<any[]>([])
const settingsView = ref('info')

async function load() {
  loading.value = true
  try { pool.value = await poolApi.get(id.value) } finally { loading.value = false }
  loadWs(); loadChildren()
  const sp: any = await poolApi.sharedSpaces(id.value)
  grantedSpaces.value = [...sp.direct, ...sp.inherited].filter((x: any) => !x.denied)
}
async function loadWs() { workspaces.value = (await poolApi.workspaces(id.value, { includeDescendants: includeSub.value ? true : undefined })) as any[] }
async function loadChildren() { children.value = ((await poolApi.tree()) as any[]).filter((n) => n.parentPoolId === id.value) }
watch(id, load, { immediate: true })
watch(includeSub, loadWs)
watch(tab, (t) => router.replace({ query: { ...route.query, tab: t } }))
const canManage = computed(() => !!pool.value?.my?.canManage)
const s = computed(() => pool.value?.stats || {})
</script>

<template>
  <div v-if="pool" class="page" data-mod="pool">
    <PageHeader :title="pool.name" back="/pools">
      <template #icon><ApartmentOutlined /></template>
      <template #tags>
        <Tags kind="role" :value="pool.my.directRole" />
        <Tags kind="source" :value="pool.my.source" :via="pool.my.viaPool" />
      </template>
      <template #sub>
        <span v-for="(a, i) in pool.ancestors" :key="a.id"><a @click="router.push(`/p/${a.id}`)">{{ a.name }}</a><span class="muted"> / </span></span><b class="text-2">{{ pool.name }}</b>
        <span v-if="pool.description" class="muted"> · {{ pool.description }}</span>
      </template>
      <template #actions>
        <a-button v-if="canManage" @click="showChild = true"><PlusOutlined /> 子 Pool</a-button>
        <a-button type="primary" @click="showWs = true"><PlusOutlined /> 新建 Workspace</a-button>
      </template>
    </PageHeader>

    <a-tabs v-model:active-key="tab" :destroy-inactive-tab-pane="true">
      <a-tab-pane key="overview" tab="概览">
        <div class="stack">
          <div class="grid grid-4">
            <StatCard label="Workspace" :value="s.workspaces" :hint="s.subPoolWorkspaces ? `子 Pool 另有 ${s.subPoolWorkspaces}` : ''" mod="ws"><template #icon><AppstoreOutlined /></template></StatCard>
            <StatCard label="子 Pool" :value="s.children" mod="pool"><template #icon><ApartmentOutlined /></template></StatCard>
            <StatCard label="成员" :value="s.directMembers + s.inheritedMembers" :hint="`直接 ${s.directMembers} / 继承 ${s.inheritedMembers}`" mod="chat"><template #icon><TeamOutlined /></template></StatCard>
            <StatCard label="共享空间" :value="s.sharedDirect + s.sharedInherited" :hint="`直接 ${s.sharedDirect} / 继承 ${s.sharedInherited}`" mod="space"><template #icon><FolderOpenOutlined /></template></StatCard>
          </div>
          <div class="grid" style="grid-template-columns: minmax(0, 7fr) minmax(0, 5fr)">
            <div class="card">
              <div class="card-title">我的访问权限</div>
              <div class="note"><span>你在该 Pool 的访问来源：<b>{{ sourceText[pool.my.source] || pool.my.source }}</b><template v-if="pool.my.viaPool">（经由「{{ pool.my.viaPool }}」）</template>。{{ canManage ? '你可以管理该 Pool 的成员、Workspace 与共享空间。' : '你可以使用该 Pool 向你开放的资源。' }}</span></div>
              <dl class="kv mt-16">
                <dt>层级</dt><dd>第 {{ pool.depth }} 层{{ pool.parentName ? `，父级：${pool.parentName}` : '（顶级）' }}</dd>
                <dt>Runtime Provider</dt><dd><span class="tag runtime">{{ pool.runtimeProvider }}</span></dd>
                <dt>配置继承</dt><dd>{{ pool.inheritConfig ? '继承父级模型 / Skill / MCP' : '不继承（独立配置）' }}</dd>
                <dt>Workspace 存储</dt><dd>{{ fmtBytes(s.storageBytes) }}</dd>
                <dt>今日运行</dt><dd>{{ s.todayRuns }} 次</dd>
              </dl>
            </div>
            <div class="card" data-mod="runtime">
              <div class="card-title"><span class="chip sm"><CloudServerOutlined /></span>Runtime</div>
              <div v-if="pool.runtime" class="row"><Tags kind="runtime" :value="pool.runtime.status" /><span class="muted small">{{ pool.runtime.provider }} · {{ pool.runtime.version || '未启动过' }}</span></div>
              <div v-else class="muted">尚未创建（首次访问 Workspace 时自动启动）</div>
              <a-button class="mt-16" @click="tab = 'runtime'">管理 Runtime</a-button>
            </div>
          </div>
        </div>
      </a-tab-pane>

      <a-tab-pane key="workspaces" tab="Workspace">
        <div class="row" style="margin-bottom: 20px"><a-switch v-model:checked="includeSub" /><span class="text-2">同时显示子 Pool 的 Workspace</span></div>
        <div v-if="workspaces.length" class="grid grid-auto"><WorkspaceCard v-for="w in workspaces" :key="w.id" :ws="w" /></div>
        <a-empty v-else description="该 Pool 还没有你可访问的 Workspace" />
      </a-tab-pane>

      <a-tab-pane key="children" tab="子 Pool">
        <div class="card flush">
          <a-table :data-source="children" row-key="id" :pagination="false" :locale="{ emptyText: '没有子 Pool' }">
            <a-table-column title="名称" key="n"><template #default="{ record }"><a @click="router.push(`/p/${record.id}`)"><b>{{ record.name }}</b></a><div class="muted small">{{ record.description }}</div></template></a-table-column>
            <a-table-column title="我的角色" key="r" :width="140"><template #default="{ record }"><Tags kind="role" :value="record.directRole" /></template></a-table-column>
            <a-table-column title="Workspace" data-index="workspaceCount" key="w" :width="110" />
            <a-table-column title="成员" data-index="memberCount" key="m" :width="90" />
            <a-table-column title="子 Pool" data-index="childCount" key="c" :width="90" />
          </a-table>
        </div>
      </a-tab-pane>

      <a-tab-pane key="spaces" tab="共享空间"><PoolSpacesPanel :pool-id="id" :can-manage="canManage" @loaded="(l: any[]) => (grantedSpaces = l)" /></a-tab-pane>
      <a-tab-pane key="members" tab="成员"><PoolMembersPanel :pool-id="id" :can-manage="canManage" /></a-tab-pane>
      <a-tab-pane key="models" tab="模型"><ConfigPanel scope="Pool" :scope-id="id" kind="Model" :can-edit="canManage" /></a-tab-pane>
      <a-tab-pane key="skills" tab="Skills"><ConfigPanel scope="Pool" :scope-id="id" kind="Skill" :can-edit="canManage" /></a-tab-pane>
      <a-tab-pane key="mcp" tab="MCP"><ConfigPanel scope="Pool" :scope-id="id" kind="Mcp" :can-edit="canManage" /></a-tab-pane>
      <a-tab-pane v-if="canManage" key="secrets" tab="密钥"><SecretPanel scope="Pool" :scope-id="id" /></a-tab-pane>
      <a-tab-pane key="api" tab="API"><SdkSnippet :pool-id="id" /></a-tab-pane>
      <a-tab-pane key="runtime" tab="Runtime"><RuntimePanel :pool-id="id" :can-manage="canManage" /></a-tab-pane>
      <a-tab-pane key="usage" tab="用量"><UsagePanel :pool-id="id" /></a-tab-pane>
      <a-tab-pane key="audit" tab="审计日志"><AuditPanel :pool-id="id" /></a-tab-pane>
      <a-tab-pane key="settings" tab="设置">
        <a-segmented v-model:value="settingsView" :options="[{ label: '基本信息与危险操作', value: 'info' }, { label: '策略与配额', value: 'policy' }]" style="margin-bottom: 24px" />
        <PoolSettingsPanel v-if="settingsView === 'info'" :pool="pool" :can-manage="canManage" @changed="load" />
        <PolicyPanel v-else :pool-id="id" :can-manage="canManage" :shared-spaces="grantedSpaces" />
      </a-tab-pane>
    </a-tabs>

    <CreatePoolModal v-model:open="showChild" :parent-id="id" @created="(p: any) => router.push(`/p/${p.id}`)" />
    <CreateWorkspaceModal v-model:open="showWs" :pool-id="id" @created="(w: any) => router.push(`/workspaces/${w.id}`)" />
  </div>
  <div v-else class="page"><a-skeleton active :loading="loading" /></div>
</template>
