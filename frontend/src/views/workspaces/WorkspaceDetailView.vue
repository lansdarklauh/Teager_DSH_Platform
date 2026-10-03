<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { message, Modal } from 'ant-design-vue'
import { AppstoreOutlined, MessageOutlined, CameraOutlined, LinkOutlined, DatabaseOutlined, ArrowRightOutlined, CopyOutlined, InboxOutlined } from '@ant-design/icons-vue'
import { wsApi, sessionApi } from '@/api'
import { fmtBytes, fmtTime, sourceText } from '@/utils/format'
import PageHeader from '@/components/PageHeader.vue'
import StatCard from '@/components/StatCard.vue'
import Tags from '@/components/Tags.vue'
import SessionsPanel from '@/components/SessionsPanel.vue'
import FileManager from '@/components/FileManager.vue'
import WorkspaceMountsPanel from '@/components/WorkspaceMountsPanel.vue'
import WorkspaceMembersPanel from '@/components/WorkspaceMembersPanel.vue'
import ConfigPanel from '@/components/ConfigPanel.vue'
import SecretPanel from '@/components/SecretPanel.vue'
import SnapshotsPanel from '@/components/SnapshotsPanel.vue'
import SdkSnippet from '@/components/SdkSnippet.vue'
import UsagePanel from '@/components/UsagePanel.vue'
import AuditPanel from '@/components/AuditPanel.vue'

const route = useRoute()
const router = useRouter()
const id = computed(() => route.params.id as string)
const data = ref<any>(null)
const mounts = ref<any[]>([])
const tab = ref((route.query.tab as string) || 'overview')
const cfgKind = ref<'Model' | 'Skill' | 'Mcp'>('Model')
const settings = ref<any>({ name: '', description: '', inheritPoolMembers: true })
const del = ref({ open: false, mode: 'archive', confirmName: '' })

async function load() {
  data.value = await wsApi.get(id.value)
  const v = data.value.view
  settings.value = { name: v.name, description: v.description || '', inheritPoolMembers: v.inheritPoolMembers }
  loadMounts()
}
async function loadMounts() { try { mounts.value = ((await wsApi.mounts(id.value)) as any).mounts } catch { mounts.value = [] } }
watch(id, load, { immediate: true })
watch(tab, (t) => router.replace({ query: { ...route.query, tab: t } }))

const v = computed(() => data.value?.view)
const role = computed(() => v.value?.myRole)
const canWrite = computed(() => role.value === 'Owner' || role.value === 'Admin' || role.value === 'Member')
const canManage = computed(() => !!v.value?.canManage)
const archived = computed(() => v.value?.status === 'Archived')
const fileAreas = computed(() => [{ value: 'files', label: '私有文件', hint: '本 Workspace 独有，Agent 在 files/ 下读写' },
  ...mounts.value.filter((m) => m.active).map((m) => ({ value: `shared/${m.mountName}`, label: `共享：${m.mountName}`, hint: `共享空间「${m.sharedSpaceName}」 · 有效权限 ${m.effectivePermission}` }))])

async function enter() { router.push(`/w/${id.value}`) }
async function newSession() { const s: any = await sessionApi.create({ workspaceId: id.value }); router.push(`/w/${id.value}/s/${s.id}`) }
async function saveInfo() { await wsApi.update(id.value, settings.value); message.success('已保存'); load() }
async function clone() {
  Modal.confirm({ title: '克隆 Workspace', content: '将复制私有文件与共享挂载配置（不含会话与成员），你将成为新 Workspace 的所有者。', okText: '克隆', cancelText: '取消',
    onOk: async () => { const c: any = await wsApi.clone(id.value); message.success('已克隆'); router.push(`/workspaces/${c.id}`) } })
}
async function restore() { await wsApi.restore(id.value); message.success('已恢复'); load() }
async function doDelete() {
  if (del.value.mode === 'permanent' && del.value.confirmName !== v.value.name) { message.warning('请输入 Workspace 名称确认永久删除'); return }
  await wsApi.remove(id.value, del.value.mode)
  message.success(del.value.mode === 'runtime_context' ? '运行上下文已清理' : del.value.mode === 'archive' ? '已归档' : '已永久删除')
  del.value.open = false
  if (del.value.mode === 'permanent') router.replace('/workspaces'); else load()
}
</script>

<template>
  <div v-if="v" class="page" data-mod="ws">
    <PageHeader :title="v.name" back="/workspaces">
      <template #icon><AppstoreOutlined /></template>
      <template #tags>
        <Tags v-if="role" kind="role" :value="role" :path="v.path" />
        <span v-if="v.source === 'pool-inherited'" class="tag space">继承访问</span>
        <span v-if="archived" class="tag warn"><InboxOutlined /> 已归档（{{ fmtTime(v.purgeAfter) }} 后清理）</span>
      </template>
      <template #sub><a @click="router.push(`/p/${v.poolId}`)">{{ v.poolName }}</a><span v-if="v.description" class="muted"> · {{ v.description }}</span></template>
      <template #actions>
        <a-button v-if="archived && canManage" type="primary" @click="restore">恢复</a-button>
        <template v-else-if="role">
          <a-button v-if="canWrite" @click="newSession"><MessageOutlined /> 新建会话</a-button>
          <a-button v-if="canWrite" @click="clone"><CopyOutlined /> 克隆</a-button>
          <a-button type="primary" @click="enter">进入工作台 <ArrowRightOutlined /></a-button>
        </template>
      </template>
    </PageHeader>

    <a-tabs v-model:active-key="tab" :destroy-inactive-tab-pane="true">
      <a-tab-pane key="overview" tab="概览">
        <div class="stack">
          <div class="grid grid-4">
            <StatCard label="活跃会话" :value="data.sessions" mod="chat"><template #icon><MessageOutlined /></template></StatCard>
            <StatCard label="私有文件占用" :value="fmtBytes(v.storageBytes)" :hint="data.policy.maxStorageMbPerWorkspace ? `配额 ${data.policy.maxStorageMbPerWorkspace} MB` : '不限'" mod="ws"><template #icon><DatabaseOutlined /></template></StatCard>
            <StatCard label="共享空间挂载" :value="data.mounts" mod="space"><template #icon><LinkOutlined /></template></StatCard>
            <StatCard label="快照" :value="data.snapshots" mod="usage"><template #icon><CameraOutlined /></template></StatCard>
          </div>
          <div class="grid" style="grid-template-columns: minmax(0, 7fr) minmax(0, 5fr)">
            <div class="card">
              <div class="card-title">我的访问</div>
              <div class="note"><span>你的角色：<b>{{ role ? { Owner: '所有者', Admin: '管理员', Member: '成员', Viewer: '只读' }[role as string] : '无数据访问权（仅管理）' }}</b>；来源：<b>{{ sourceText[v.source] || v.source }}</b><template v-if="v.path?.length">；路径：{{ v.path.join(' → ') }}</template>。{{ v.inheritPoolMembers ? '该 Workspace 继承 Pool 有效成员的访问权限。' : '该 Workspace 不继承 Pool 成员，仅显式成员可进入。' }}</span></div>
              <dl class="kv mt-16">
                <dt>所属 Pool</dt><dd><a @click="router.push(`/p/${v.poolId}`)">{{ v.poolName }}</a></dd>
                <dt>创建时间</dt><dd>{{ fmtTime(v.createdAt) }}</dd>
                <dt>最近活跃</dt><dd>{{ fmtTime(v.lastActiveAt) }}</dd>
                <dt>Agent 目录</dt><dd class="mono">/workspace/files　·　/workspace/shared/*</dd>
              </dl>
            </div>
            <div class="card" data-mod="runtime">
              <div class="card-title">Runtime 状态</div>
              <div class="row"><Tags v-if="data.runtime" kind="runtime" :value="data.runtime.status" /><span v-else class="muted">尚未启动（发送消息时自动启动）</span><span class="muted small">{{ data.runtimeProvider }}</span></div>
              <div class="muted small mt-16">该 Workspace 所属 Pool 拥有独立的 Runtime 环境；终端：{{ data.terminalAllowed ? '允许' : '已被策略禁用' }}；沙箱：{{ data.policy.sandboxMode }}。</div>
              <a-button class="mt-16" @click="router.push(`/p/${v.poolId}?tab=runtime`)">查看 Pool Runtime</a-button>
            </div>
          </div>
        </div>
      </a-tab-pane>
      <a-tab-pane key="sessions" tab="Sessions"><SessionsPanel :workspace-id="id" :can-write="canWrite && !archived" /></a-tab-pane>
      <a-tab-pane key="files" tab="Files">
        <div class="card"><FileManager :base="`/workspaces/${id}`" :areas="fileAreas" :can-write="canWrite && !archived" /></div>
      </a-tab-pane>
      <a-tab-pane key="mounts" tab="共享空间"><WorkspaceMountsPanel :workspace-id="id" :can-manage="canManage && !archived" @changed="loadMounts" /></a-tab-pane>
      <a-tab-pane key="members" tab="成员"><WorkspaceMembersPanel :workspace-id="id" :can-manage="canManage" :inherit-pool-members="v.inheritPoolMembers" /></a-tab-pane>
      <a-tab-pane key="config" tab="模型 / Skills / MCP">
        <a-segmented v-model:value="cfgKind" :options="[{ label: '模型', value: 'Model' }, { label: 'Skills', value: 'Skill' }, { label: 'MCP', value: 'Mcp' }]" style="margin-bottom: 22px" />
        <ConfigPanel :key="cfgKind" scope="Workspace" :scope-id="id" :kind="cfgKind" :can-edit="canManage" />
      </a-tab-pane>
      <a-tab-pane v-if="canManage" key="secrets" tab="密钥"><SecretPanel scope="Workspace" :scope-id="id" /></a-tab-pane>
      <a-tab-pane key="snapshots" tab="快照"><SnapshotsPanel :workspace-id="id" :can-write="canWrite && !archived" :can-manage="canManage && !archived" /></a-tab-pane>
      <a-tab-pane key="api" tab="API"><SdkSnippet :workspace-id="id" /></a-tab-pane>
      <a-tab-pane key="usage" tab="用量"><UsagePanel :workspace-id="id" /></a-tab-pane>
      <a-tab-pane key="audit" tab="审计日志"><AuditPanel :workspace-id="id" /></a-tab-pane>
      <a-tab-pane v-if="canManage" key="settings" tab="设置">
        <div class="stack">
          <div class="card">
            <div class="card-title">基本信息</div>
            <a-form layout="vertical" style="max-width: 640px">
              <a-form-item label="名称"><a-input v-model:value="settings.name" /></a-form-item>
              <a-form-item label="描述"><a-textarea v-model:value="settings.description" :rows="2" /></a-form-item>
              <a-form-item><a-checkbox v-model:checked="settings.inheritPoolMembers">继承 Pool 有效成员的访问权限</a-checkbox></a-form-item>
              <a-button type="primary" @click="saveInfo">保存</a-button>
            </a-form>
          </div>
          <div class="card danger-zone">
            <div class="card-title" style="color: var(--danger)">删除 Workspace</div>
            <div class="row"><div><b>选择删除方式</b><div class="muted small">默认归档并保留 30 天；也可仅清理运行上下文（保留文件），或永久删除。</div></div><span class="spacer" /><a-button danger @click="del.open = true">删除…</a-button></div>
          </div>
        </div>
      </a-tab-pane>
    </a-tabs>

    <a-modal v-model:open="del.open" :title="`删除 Workspace「${v.name}」`" ok-text="确认" ok-type="danger" cancel-text="取消" @ok="doDelete">
      <a-radio-group v-model:value="del.mode" class="vgroup">
        <a-radio value="runtime_context"><b>仅清理运行上下文</b><div class="muted small">删除会话、消息与事件日志、Skill 与挂载链接；保留文件与成员</div></a-radio>
        <a-radio value="archive"><b>归档（推荐，默认保留 30 天）</b><div class="muted small">停止使用并隐藏，期限内可恢复，到期后由系统永久清理</div></a-radio>
        <a-radio value="permanent"><b style="color: var(--danger)">永久删除</b><div class="muted small">立即删除数据库记录、文件、快照，不可恢复（共享空间数据不受影响）</div></a-radio>
      </a-radio-group>
      <a-input v-if="del.mode === 'permanent'" v-model:value="del.confirmName" :placeholder="`输入「${v.name}」以确认`" style="margin-top: 16px" />
    </a-modal>
  </div>
  <div v-else class="page"><a-skeleton active /></div>
</template>

<style scoped>
.danger-zone { border-color: #f5c6c6; }
.vgroup { display: flex; flex-direction: column; gap: 16px; }
</style>
