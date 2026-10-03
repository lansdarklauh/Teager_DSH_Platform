<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { message } from 'ant-design-vue'
import { ArrowRightOutlined, DeleteOutlined } from '@ant-design/icons-vue'
import { poolApi } from '@/api'
import { useAuthStore } from '@/stores/auth'
import { descendantIds } from '@/utils/tree'
import PoolTreeSelect from './PoolTreeSelect.vue'

/** Pool 设置：基本信息、移动（含影响预览）、删除（子 Pool / Workspace 的处理方式，不级联删除数据）。 */
const props = defineProps<{ pool: any; canManage: boolean }>()
const emit = defineEmits<{ changed: [] }>()
const router = useRouter()
const auth = useAuthStore()
const info = ref({ name: props.pool.name, description: props.pool.description || '', runtimeProvider: props.pool.runtimeProvider, inheritConfig: props.pool.inheritConfig })
const saving = ref(false)
async function saveInfo() {
  saving.value = true
  try { await poolApi.update(props.pool.id, { name: info.value.name, description: info.value.description, inheritConfig: info.value.inheritConfig, runtimeProvider: auth.isAdmin ? info.value.runtimeProvider : undefined }); message.success('已保存'); emit('changed') } finally { saving.value = false }
}

// 移动
const nodes = ref<any[]>([])
onMounted(async () => { nodes.value = (await poolApi.tree()) as any[] })
const exclude = computed(() => descendantIds(nodes.value, props.pool.id))
const newParent = ref<string | undefined>(undefined)
const preview = ref<any>(null)
const moving = ref(false)
async function doPreview() { preview.value = await poolApi.movePreview(props.pool.id, newParent.value) }
async function doMove() {
  moving.value = true
  try { await poolApi.move(props.pool.id, newParent.value || null); message.success('已移动'); preview.value = null; newParent.value = undefined; emit('changed') } finally { moving.value = false }
}

// 删除
const del = ref({ open: false, childMode: 'ToParent', workspaceMode: 'Archive', transferTo: undefined as string | undefined, loading: false })
const childCount = computed(() => nodes.value.filter((n) => n.parentPoolId === props.pool.id).length)
const wsCount = computed(() => (props.pool.stats?.workspaces ?? 0))
async function doDelete() {
  del.value.loading = true
  try {
    await poolApi.remove(props.pool.id, { childMode: childCount.value ? del.value.childMode : undefined, workspaceMode: wsCount.value ? del.value.workspaceMode : undefined, transferTo: del.value.workspaceMode === 'Transfer' ? del.value.transferTo : undefined })
    message.success('Pool 已删除（归档）'); del.value.open = false; router.replace('/pools')
  } finally { del.value.loading = false }
}
</script>

<template>
  <div class="stack" data-mod="pool">
    <div class="card">
      <div class="card-title">基本信息</div>
      <a-form layout="vertical" style="max-width: 640px">
        <a-form-item label="名称"><a-input v-model:value="info.name" :disabled="!canManage" /></a-form-item>
        <a-form-item label="描述"><a-textarea v-model:value="info.description" :rows="2" :disabled="!canManage" /></a-form-item>
        <a-form-item v-if="auth.isAdmin" label="Runtime Provider"><a-select v-model:value="info.runtimeProvider" :options="(auth.meta?.runtimeProviders || []).map((p: string) => ({ value: p, label: p }))" /></a-form-item>
        <a-form-item><a-checkbox v-model:checked="info.inheritConfig" :disabled="!canManage">继承父级配置（模型 / Skill / MCP）</a-checkbox><div class="muted small">关闭后本 Pool 不再继承上级与平台的模型 / Skill / MCP；策略与安全限制始终逐级收紧，不受此开关影响。</div></a-form-item>
        <a-button v-if="canManage" type="primary" :loading="saving" @click="saveInfo">保存</a-button>
      </a-form>
    </div>

    <div v-if="canManage" class="card">
      <div class="card-title">移动 Pool<span class="sub">改变父级会重算继承成员、继承共享空间与继承配置</span></div>
      <div class="row-wrap" style="max-width: 640px">
        <div style="flex: 1; min-width: 240px"><PoolTreeSelect v-model="newParent" manage-only allow-clear :exclude="exclude" :placeholder="auth.isAdmin ? '选择新父 Pool（不选 = 移到顶级）' : '选择新父 Pool'" /></div>
        <a-button @click="doPreview">预览影响</a-button>
      </div>
      <div v-if="preview" class="preview">
        <div class="grid grid-2">
          <div><div class="muted small">将失去的父级</div><div class="row-wrap mt-8"><span v-for="x in preview.lostAncestors" :key="x" class="tag danger">{{ x }}</span><span v-if="!preview.lostAncestors.length" class="muted">无</span></div></div>
          <div><div class="muted small">将新增的父级</div><div class="row-wrap mt-8"><span v-for="x in preview.gainedAncestors" :key="x" class="tag ok">{{ x }}</span><span v-if="!preview.gainedAncestors.length" class="muted">无</span></div></div>
          <div><div class="muted small">将失去的继承共享空间</div><div class="row-wrap mt-8"><span v-for="x in preview.lostSharedSpaces" :key="x" class="tag danger">{{ x }}</span><span v-if="!preview.lostSharedSpaces.length" class="muted">无</span></div></div>
          <div><div class="muted small">将新增的继承共享空间</div><div class="row-wrap mt-8"><span v-for="x in preview.gainedSharedSpaces" :key="x" class="tag ok">{{ x }}</span><span v-if="!preview.gainedSharedSpaces.length" class="muted">无</span></div></div>
        </div>
        <div class="row-wrap mt-16"><span class="tag">影响 {{ preview.affectedPools }} 个 Pool</span><span class="tag">{{ preview.affectedUsers }} 位用户</span><span class="tag">{{ preview.affectedWorkspaces }} 个 Workspace</span><span class="tag">移动后层级 {{ preview.newDepth }}</span><span class="spacer" />
          <a-popconfirm title="确认移动？相关权限将立即重新计算。" ok-text="确认移动" cancel-text="取消" @confirm="doMove"><a-button type="primary" :loading="moving">确认移动 <ArrowRightOutlined /></a-button></a-popconfirm></div>
      </div>
    </div>

    <div v-if="canManage" class="card danger-zone">
      <div class="card-title" style="color: var(--danger)">危险操作</div>
      <div class="row"><div><b>删除 Pool</b><div class="muted small">删除不会直接级联删除数据：可选择子 Pool 上移 / 归档，Workspace 归档 / 转移；共享空间默认保留（转为组织级）。</div></div><span class="spacer" /><a-button danger @click="del.open = true"><DeleteOutlined /> 删除 Pool</a-button></div>
    </div>

    <a-modal v-model:open="del.open" :title="`删除 Pool「${pool.name}」`" ok-text="确认删除" ok-type="danger" cancel-text="取消" :confirm-loading="del.loading" @ok="doDelete">
      <a-form layout="vertical">
        <a-form-item v-if="childCount" :label="`该 Pool 含 ${childCount} 个子 Pool，如何处理？`">
          <a-radio-group v-model:value="del.childMode" class="vgroup"><a-radio value="ToParent">将子 Pool 移到父级</a-radio><a-radio v-if="auth.isAdmin" value="ToRoot">将子 Pool 移到顶级</a-radio><a-radio value="Archive">同时归档子 Pool</a-radio></a-radio-group>
        </a-form-item>
        <a-form-item v-if="wsCount" :label="`该 Pool 含 ${wsCount} 个 Workspace，如何处理？`">
          <a-radio-group v-model:value="del.workspaceMode" class="vgroup"><a-radio value="Archive">归档（默认保留 30 天）</a-radio><a-radio value="Transfer">转移到其他 Pool</a-radio></a-radio-group>
          <PoolTreeSelect v-if="del.workspaceMode === 'Transfer'" v-model="del.transferTo" manage-only :exclude="exclude" placeholder="选择目标 Pool" style="margin-top: 10px" />
        </a-form-item>
        <div class="note warn"><span>Pool 的成员关系与共享授权会被清理；共享空间数据、归档的 Workspace 数据均不会被物理删除。</span></div>
      </a-form>
    </a-modal>
  </div>
</template>

<style scoped>
.preview { margin-top: 20px; padding: 20px; border-radius: 12px; background: var(--surface-2); border: 1px dashed var(--border-strong); }
.danger-zone { border-color: #f5c6c6; }
.vgroup { display: flex; flex-direction: column; gap: 10px; }
</style>
