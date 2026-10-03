<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { message, Modal } from 'ant-design-vue'
import { FolderOpenOutlined, PlusOutlined, DeleteOutlined } from '@ant-design/icons-vue'
import { spaceApi } from '@/api'
import { fmtBytes, fmtTime } from '@/utils/format'
import PageHeader from '@/components/PageHeader.vue'
import Tags from '@/components/Tags.vue'
import FileManager from '@/components/FileManager.vue'
import PoolTreeSelect from '@/components/PoolTreeSelect.vue'
import UserSelect from '@/components/UserSelect.vue'
import AuditPanel from '@/components/AuditPanel.vue'

const route = useRoute()
const router = useRouter()
const id = computed(() => route.params.id as string)
const space = ref<any>(null)
const tab = ref((route.query.tab as string) || 'files')
const grants = ref<any[]>([])
const mounts = ref<any[]>([])
const explain = ref<any>(null)
const info = ref<any>({ name: '', description: '', quotaMb: 0, mountName: '' })

const perm = computed(() => space.value?.my?.permission)
const canManage = computed(() => perm.value === 'MANAGE')
const canWrite = computed(() => perm.value === 'RW' || perm.value === 'MANAGE')
const archived = computed(() => space.value?.status === 'Archived')

async function load() {
  space.value = await spaceApi.get(id.value)
  info.value = { name: space.value.name, description: space.value.description || '', quotaMb: space.value.quotaMb, mountName: space.value.mountName }
  grants.value = (await spaceApi.grants(id.value)) as any[]
  explain.value = await spaceApi.explain(id.value)
  if (canManage.value) mounts.value = (await spaceApi.mounts(id.value)) as any[]
}
watch(id, load, { immediate: true })
watch(tab, (t) => router.replace({ query: { ...route.query, tab: t } }))

// 授权
const g = ref<any>({ open: false, subjectType: 'POOL', subjectId: undefined, permission: 'RO', deny: false, inherit: true })
async function addGrant() {
  if (!g.value.subjectId) { message.warning('请选择授权对象'); return }
  await spaceApi.addGrant(id.value, { subjectType: g.value.subjectType, subjectId: g.value.subjectId, permission: g.value.permission, effect: g.value.deny ? 'Deny' : 'Allow', inheritToChildren: g.value.inherit })
  message.success('授权已保存'); g.value.open = false; g.value = { open: false, subjectType: 'POOL', subjectId: undefined, permission: 'RO', deny: false, inherit: true }; load()
}
function removeGrant(r: any) { Modal.confirm({ title: `撤销对「${r.subjectName}」的授权？`, content: '已挂载该共享空间的 Workspace 将因失去授权而自动失效。', okType: 'danger', okText: '撤销', cancelText: '取消', onOk: async () => { await spaceApi.removeGrant(id.value, r.id); load() } }) }

async function saveInfo() { await spaceApi.update(id.value, info.value); message.success('已保存'); load() }
const del = ref({ open: false, permanent: false })
async function doDelete() { await spaceApi.remove(id.value, del.value.permanent); message.success(del.value.permanent ? '已永久删除' : '已归档（数据保留）'); del.value.open = false; if (del.value.permanent) router.replace('/shared-spaces'); else load() }
const pct = computed(() => (space.value?.quotaMb ? Math.min(100, Math.round((space.value.storageBytes / (space.value.quotaMb * 1024 * 1024)) * 100)) : 0))
</script>

<template>
  <div v-if="space" class="page" data-mod="space">
    <PageHeader :title="space.name" back="/shared-spaces">
      <template #icon><FolderOpenOutlined /></template>
      <template #tags><Tags kind="role" :value="perm" /><Tags kind="source" :value="space.my.source" :path="space.my.path" /><span v-if="archived" class="tag warn">已归档</span></template>
      <template #sub>所属：{{ space.ownerPoolName }}　·　<span class="mono">shared/{{ space.mountName }}</span>　·　占用 {{ fmtBytes(space.storageBytes) }}{{ space.quotaMb ? ` / ${space.quotaMb} MB` : '' }}<span v-if="space.description"> · {{ space.description }}</span></template>
    </PageHeader>

    <a-tabs v-model:active-key="tab" :destroy-inactive-tab-pane="true">
      <a-tab-pane key="files" tab="文件"><div class="card"><FileManager :base="`/shared-spaces/${id}`" :can-write="canWrite && !archived" /></div></a-tab-pane>

      <a-tab-pane key="grants" tab="授权">
        <div class="stack">
          <div class="row"><div class="muted">授权决定谁能使用；<b>显式拒绝</b>优先于一切授权；<b>向下继承</b>关闭时，只有被授权 Pool 的直接成员可用。</div><span class="spacer" /><a-button v-if="canManage" type="primary" @click="g.open = true"><PlusOutlined /> 新增授权</a-button></div>
          <div class="card flush">
            <a-table :data-source="grants" row-key="id" :pagination="false" size="middle" :locale="{ emptyText: '暂无授权' }">
              <a-table-column title="授权对象" key="s"><template #default="{ record }"><span class="tag" :class="record.subjectType === 'POOL' ? 'pool' : 'chat'">{{ record.subjectType === 'POOL' ? 'Pool' : '用户' }}</span> <b style="margin-left: 8px">{{ record.subjectName }}</b></template></a-table-column>
              <a-table-column title="权限" key="p" :width="130"><template #default="{ record }"><span v-if="record.effect === 'Deny'" class="tag danger dot">显式拒绝</span><Tags v-else kind="role" :value="record.permission" /></template></a-table-column>
              <a-table-column title="向下继承" key="i" :width="110"><template #default="{ record }"><span v-if="record.subjectType === 'POOL'" class="tag dot" :class="record.inheritToChildren ? 'ok' : ''">{{ record.inheritToChildren ? '继承给子 Pool' : '仅自身' }}</span><span v-else class="muted">—</span></template></a-table-column>
              <a-table-column title="授权时间" key="t" :width="180"><template #default="{ record }"><span class="muted">{{ fmtTime(record.createdAt) }}</span></template></a-table-column>
              <a-table-column v-if="canManage" title="" key="op" :width="70" align="right"><template #default="{ record }"><a-button type="text" size="small" danger @click="removeGrant(record)"><DeleteOutlined /></a-button></template></a-table-column>
            </a-table>
          </div>
        </div>
      </a-tab-pane>

      <a-tab-pane v-if="canManage" key="mounts" tab="挂载情况">
        <div class="card flush">
          <a-table :data-source="mounts" row-key="id" :pagination="false" size="middle" :locale="{ emptyText: '还没有 Workspace 挂载该共享空间' }">
            <a-table-column title="Workspace" key="w"><template #default="{ record }"><a @click="router.push(`/workspaces/${record.workspaceId}`)"><b>{{ record.workspaceName }}</b></a><div class="muted small">{{ record.poolName }}</div></template></a-table-column>
            <a-table-column title="挂载名" key="m"><template #default="{ record }"><span class="mono">shared/{{ record.mountName }}</span></template></a-table-column>
            <a-table-column title="覆盖" key="o" :width="120"><template #default="{ record }"><Tags kind="role" :value="record.override" /><span v-if="!record.override" class="muted">—</span></template></a-table-column>
            <a-table-column title="状态" key="e" :width="100"><template #default="{ record }"><span class="tag dot" :class="record.enabled ? 'ok' : ''">{{ record.enabled ? '启用' : '停用' }}</span></template></a-table-column>
          </a-table>
        </div>
      </a-tab-pane>

      <a-tab-pane key="explain" tab="权限来源">
        <div class="card" style="max-width: 720px">
          <div class="card-title">我为什么拥有该权限</div>
          <div class="row" style="margin-bottom: 18px"><span class="muted">当前权限</span><Tags v-if="explain?.permission" kind="role" :value="explain.permission" /><span v-else class="tag danger">无</span><Tags kind="source" :value="explain?.source" /></div>
          <div v-if="explain?.path?.length" style="margin-bottom: 18px"><div class="muted small" style="margin-bottom: 10px">继承路径</div><a-steps direction="vertical" size="small" :current="explain.path.length" :items="explain.path.map((p: string) => ({ title: p }))" /></div>
          <div class="muted small" style="margin-bottom: 8px">判定说明</div>
          <ul style="padding-left: 18px; line-height: 2; color: var(--text-2)"><li v-for="n in explain?.notes" :key="n">{{ n }}</li></ul>
        </div>
      </a-tab-pane>

      <a-tab-pane key="audit" tab="访问审计"><AuditPanel default-action="sharedspace" :default-keyword="id" /></a-tab-pane>

      <a-tab-pane v-if="canManage" key="settings" tab="设置">
        <div class="stack">
          <div class="card">
            <div class="card-title">基本信息</div>
            <a-form layout="vertical" style="max-width: 640px">
              <a-form-item label="名称"><a-input v-model:value="info.name" /></a-form-item>
              <a-form-item label="描述"><a-textarea v-model:value="info.description" :rows="2" /></a-form-item>
              <div class="grid grid-2"><a-form-item label="默认挂载名"><a-input v-model:value="info.mountName" class="mono" /></a-form-item><a-form-item label="配额（MB，0 不限）"><a-input-number v-model:value="info.quotaMb" :min="0" style="width: 100%" /></a-form-item></div>
              <div v-if="space.quotaMb" style="margin-bottom: 20px"><a-progress :percent="pct" :stroke-color="'#f08a00'" /></div>
              <a-button type="primary" @click="saveInfo">保存</a-button>
            </a-form>
          </div>
          <div class="card danger-zone">
            <div class="card-title" style="color: var(--danger)">删除共享空间</div>
            <div class="row"><div><b>默认仅归档，数据保留</b><div class="muted small">共享空间数据默认不删除；永久删除要求没有任何 Workspace 挂载。</div></div><span class="spacer" /><a-button danger @click="del.open = true">删除…</a-button></div>
          </div>
        </div>
      </a-tab-pane>
    </a-tabs>

    <a-modal v-model:open="g.open" title="新增授权" ok-text="保存" cancel-text="取消" @ok="addGrant">
      <a-form layout="vertical">
        <a-form-item label="授权对象类型"><a-radio-group v-model:value="g.subjectType" @change="g.subjectId = undefined"><a-radio value="POOL">Pool</a-radio><a-radio value="USER">用户</a-radio></a-radio-group></a-form-item>
        <a-form-item label="对象" required><PoolTreeSelect v-if="g.subjectType === 'POOL'" v-model="g.subjectId" /><UserSelect v-else v-model="g.subjectId" /></a-form-item>
        <a-form-item><a-checkbox v-model:checked="g.deny">显式拒绝（优先级最高，覆盖其他所有授权）</a-checkbox></a-form-item>
        <a-form-item v-if="!g.deny" label="权限"><a-radio-group v-model:value="g.permission"><a-radio value="RO">RO 只读</a-radio><a-radio value="RW">RW 读写</a-radio><a-radio value="MANAGE">MANAGE 管理</a-radio></a-radio-group></a-form-item>
        <a-form-item v-if="g.subjectType === 'POOL'"><a-checkbox v-model:checked="g.inherit">向下继承给子 Pool</a-checkbox><div class="muted small">关闭后仅该 Pool 的直接成员与 Workspace 可用。</div></a-form-item>
      </a-form>
    </a-modal>
    <a-modal v-model:open="del.open" title="删除共享空间" ok-text="确认" ok-type="danger" cancel-text="取消" @ok="doDelete">
      <a-radio-group v-model:value="del.permanent" class="vgroup"><a-radio :value="false"><b>归档</b><div class="muted small">停用并隐藏，数据保留</div></a-radio><a-radio :value="true"><b style="color: var(--danger)">永久删除</b><div class="muted small">立即删除全部文件，不可恢复</div></a-radio></a-radio-group>
    </a-modal>
  </div>
  <div v-else class="page"><a-skeleton active /></div>
</template>

<style scoped>
.danger-zone { border-color: #f5c6c6; }
.vgroup { display: flex; flex-direction: column; gap: 16px; }
</style>
