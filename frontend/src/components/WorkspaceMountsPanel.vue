<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { message, Modal } from 'ant-design-vue'
import { LinkOutlined, DeleteOutlined, QuestionCircleOutlined, PlusOutlined } from '@ant-design/icons-vue'
import { wsApi } from '@/api'
import Tags from './Tags.vue'

/**
 * Workspace 共享空间挂载：权限 ≠ 挂载。Pool 已授权的共享空间需要在此显式挂载后才会出现在 Agent 的 shared/ 目录；
 * 挂载可停用，也可把权限降低（RW → RO），但不能扩大。每条挂载都可查看“权限来源与继承路径”。
 */
const props = defineProps<{ workspaceId: string; canManage: boolean }>()
const emit = defineEmits<{ changed: [] }>()
const mounts = ref<any[]>([])
const available = ref<any[]>([])
const loading = ref(false)
async function load() { loading.value = true; try { const r: any = await wsApi.mounts(props.workspaceId); mounts.value = r.mounts; available.value = r.available; emit('changed') } finally { loading.value = false } }
watch(() => props.workspaceId, load)
onMounted(load)

const add = ref<any>({ open: false, space: null, mountName: '', override: undefined })
function askMount(a: any) { add.value = { open: true, space: a, mountName: a.defaultMountName, override: undefined } }
async function doMount() {
  await wsApi.addMount(props.workspaceId, { sharedSpaceId: add.value.space.sharedSpaceId, mountName: add.value.mountName, permissionOverride: add.value.override })
  message.success('已挂载'); add.value.open = false; load()
}
async function toggle(m: any, v: boolean) { await wsApi.updateMount(props.workspaceId, m.id, { enabled: v }); load() }
async function setOverride(m: any, v: string | undefined) { await wsApi.updateMount(props.workspaceId, m.id, v ? { permissionOverride: v } : { clearOverride: true }); message.success('已更新'); load() }
function remove(m: any) { Modal.confirm({ title: `取消挂载「${m.mountName}」？`, content: '取消后 Agent 将不再看到该目录，共享空间数据不受影响。', okType: 'danger', okText: '取消挂载', cancelText: '保留', onOk: async () => { await wsApi.removeMount(props.workspaceId, m.id); load() } }) }
const overrideOptions = (m: any) => [{ value: '', label: '不覆盖（' + (m.grantedPermission || '—') + '）' }, ...(m.grantedPermission === 'RW' || m.grantedPermission === 'MANAGE' ? [{ value: 'RW', label: 'RW 读写' }] : []), { value: 'RO', label: 'RO 只读（降低）' }]

const drawer = ref<any>({ open: false, data: null, loading: false })
async function explain(m: any) { drawer.value = { open: true, data: null, loading: true }; try { drawer.value.data = await wsApi.explain(props.workspaceId, m.sharedSpaceId) } finally { drawer.value.loading = false } }
</script>

<template>
  <div class="stack" data-mod="space">
    <div class="card flush">
      <div class="card-head"><h3>已挂载的共享空间<span class="muted small" style="font-weight: 400">　Agent 在 <span class="mono">shared/&lt;挂载名&gt;/</span> 下看到它们</span></h3></div>
      <a-table :data-source="mounts" :loading="loading" row-key="id" :pagination="false" size="middle" :locale="{ emptyText: '尚未挂载任何共享空间' }">
        <a-table-column title="共享空间" key="n"><template #default="{ record }"><div class="row"><span class="chip sm"><LinkOutlined /></span><div><b>{{ record.sharedSpaceName }}</b><div class="mono muted small">shared/{{ record.mountName }}</div></div></div></template></a-table-column>
        <a-table-column title="Pool 授权" key="g" :width="110"><template #default="{ record }"><Tags kind="role" :value="record.grantedPermission" /><span v-if="!record.grantedPermission" class="tag danger">已失效</span></template></a-table-column>
        <a-table-column title="挂载覆盖（只降不升）" key="o" :width="190">
          <template #default="{ record }">
            <a-select v-if="canManage && record.grantedPermission" :value="record.permissionOverride || ''" size="small" style="width: 170px" :options="overrideOptions(record)" @change="(v: any) => setOverride(record, v || undefined)" />
            <Tags v-else kind="role" :value="record.permissionOverride" />
          </template>
        </a-table-column>
        <a-table-column title="我的有效权限" key="e" :width="200">
          <template #default="{ record }"><div class="row-wrap" style="gap: 6px"><Tags v-if="record.effectivePermission" kind="role" :value="record.effectivePermission" /><span v-else class="tag danger">无权限</span><a-tooltip title="查看权限来源与继承路径"><a-button type="text" size="small" @click="explain(record)"><QuestionCircleOutlined /></a-button></a-tooltip></div></template>
        </a-table-column>
        <a-table-column title="启用" key="en" :width="80"><template #default="{ record }"><a-switch size="small" :checked="record.enabled" :disabled="!canManage" @change="(v: any) => toggle(record, v)" /></template></a-table-column>
        <a-table-column v-if="canManage" title="" key="op" :width="60" align="right"><template #default="{ record }"><a-button type="text" size="small" danger @click="remove(record)"><DeleteOutlined /></a-button></template></a-table-column>
      </a-table>
    </div>

    <div class="card flush">
      <div class="card-head"><h3>可挂载的共享空间<span class="muted small" style="font-weight: 400">　所属 Pool 已获授权但尚未挂载</span></h3></div>
      <a-table :data-source="available" row-key="sharedSpaceId" :pagination="false" size="middle" :locale="{ emptyText: '没有更多可挂载的共享空间' }">
        <a-table-column title="共享空间" key="n"><template #default="{ record }"><b>{{ record.name }}</b></template></a-table-column>
        <a-table-column title="授权权限" key="p" :width="120"><template #default="{ record }"><Tags kind="role" :value="record.permission" /></template></a-table-column>
        <a-table-column title="授权来源" key="s"><template #default="{ record }"><a-tooltip :title="record.path.join(' → ')"><span class="tag" :class="record.source === 'inherited' ? 'space' : 'pool'">{{ record.source === 'inherited' ? `继承自 ${record.viaPool}` : '直接授权' }}</span></a-tooltip></template></a-table-column>
        <a-table-column v-if="canManage" title="" key="op" :width="110" align="right"><template #default="{ record }"><a-button type="primary" size="small" @click="askMount(record)"><PlusOutlined /> 挂载</a-button></template></a-table-column>
      </a-table>
    </div>

    <a-modal v-model:open="add.open" title="挂载共享空间" ok-text="挂载" cancel-text="取消" @ok="doMount">
      <a-form layout="vertical">
        <a-form-item label="共享空间"><b>{{ add.space?.name }}</b>　<Tags kind="role" :value="add.space?.permission" /></a-form-item>
        <a-form-item label="挂载名（shared/ 下的目录名）"><a-input v-model:value="add.mountName" class="mono" /></a-form-item>
        <a-form-item label="权限覆盖（可选，只能降低）"><a-select v-model:value="add.override" allow-clear placeholder="不覆盖" :options="add.space?.permission === 'RW' || add.space?.permission === 'MANAGE' ? [{ value: 'RW', label: 'RW 读写' }, { value: 'RO', label: 'RO 只读' }] : [{ value: 'RO', label: 'RO 只读' }]" /></a-form-item>
      </a-form>
    </a-modal>

    <a-drawer v-model:open="drawer.open" title="权限来源可解释" width="460" data-mod="space">
      <a-spin :spinning="drawer.loading">
        <template v-if="drawer.data">
          <div class="card" style="margin-bottom: 18px"><div class="muted small">共享空间</div><b>{{ drawer.data.sharedSpace?.name }}</b>
            <div class="mt-16 muted small">当前权限</div><div class="row"><Tags v-if="drawer.data.sharedSpace?.permission" kind="role" :value="drawer.data.sharedSpace.permission" /><span v-else class="tag danger">无权限</span><Tags kind="source" :value="drawer.data.sharedSpace?.source" /></div></div>
          <div class="muted small" style="margin-bottom: 10px">继承路径</div>
          <a-steps v-if="drawer.data.sharedSpace?.path?.length" direction="vertical" size="small" :current="drawer.data.sharedSpace.path.length" :items="drawer.data.sharedSpace.path.map((p: string) => ({ title: p }))" />
          <a-empty v-else :image="false" description="无继承路径" />
          <div class="muted small" style="margin: 18px 0 10px">判定说明</div>
          <ul class="explain"><li v-for="n in drawer.data.sharedSpace?.notes" :key="n">{{ n }}</li></ul>
        </template>
      </a-spin>
    </a-drawer>
  </div>
</template>

<style scoped>
.explain { padding-left: 18px; line-height: 2; color: var(--text-2); }
</style>
