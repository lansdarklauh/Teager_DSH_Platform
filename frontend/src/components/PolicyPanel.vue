<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { message } from 'ant-design-vue'
import { poolApi } from '@/api'
import { useAuthStore } from '@/stores/auth'

/** Pool 策略：平台默认 → 祖先 → 本级逐级收紧（子级只能收紧，不能放宽）；右侧显示最终有效值及其来源。 */
const props = defineProps<{ poolId: string; canManage: boolean; sharedSpaces?: any[] }>()
const auth = useAuthStore()
const data = ref<any>(null)
const own = ref<any>({})
const saving = ref(false)
async function load() { data.value = await poolApi.policy(props.poolId); own.value = JSON.parse(JSON.stringify(data.value.own)) }
watch(() => props.poolId, load, { immediate: true })
const eff = computed(() => data.value?.effective)
const src = (k: string) => eff.value?.sources?.[k]
async function save() {
  saving.value = true
  try { data.value = await poolApi.savePolicy(props.poolId, own.value); own.value = JSON.parse(JSON.stringify(data.value.own)); message.success('策略已保存') } finally { saving.value = false }
}
const num = (k: string) => ({ get: () => own.value[k] ?? null, set: (v: any) => (own.value[k] = v ?? null) })
const sandboxOptions = [{ value: 'read-only', label: 'read-only 只读' }, { value: 'workspace-write', label: 'workspace-write 仅写工作目录' }, { value: 'danger-full-access', label: 'danger-full-access 完全访问' }]
const tri = (k: string) => own.value[k] === false ? 'deny' : own.value[k] === true ? 'allow' : 'inherit'
function setTri(k: string, v: string) { own.value[k] = v === 'deny' ? false : v === 'allow' ? true : null }
</script>

<template>
  <div v-if="eff" class="grid" style="grid-template-columns: minmax(0, 7fr) minmax(0, 5fr); align-items: start">
    <div class="card">
      <div class="card-title">本级策略<span class="sub">留空 = 沿用上级；子级只能收紧，不能放宽上级限制</span></div>
      <a-form layout="vertical">
        <div class="grid grid-2">
          <a-form-item label="网络访问"><a-segmented :value="tri('allowNetwork')" :options="[{ label: '沿用', value: 'inherit' }, { label: '允许', value: 'allow' }, { label: '禁止', value: 'deny' }]" :disabled="!canManage" @change="(v: any) => setTri('allowNetwork', v)" /></a-form-item>
          <a-form-item label="Workspace 终端"><a-segmented :value="tri('allowTerminal')" :options="[{ label: '沿用', value: 'inherit' }, { label: '允许', value: 'allow' }, { label: '禁止', value: 'deny' }]" :disabled="!canManage" @change="(v: any) => setTri('allowTerminal', v)" /></a-form-item>
          <a-form-item label="沙箱模式" class="span2"><a-select v-model:value="own.sandboxMode" allow-clear placeholder="沿用上级" :options="sandboxOptions" :disabled="!canManage" /></a-form-item>
          <a-form-item label="Workspace 数量上限"><a-input-number v-model:value="own.maxWorkspaces" :min="1" style="width: 100%" placeholder="不设置" :disabled="!canManage" /></a-form-item>
          <a-form-item label="单 Workspace 存储配额（MB）"><a-input-number v-model:value="own.maxStorageMbPerWorkspace" :min="1" style="width: 100%" placeholder="不设置" :disabled="!canManage" /></a-form-item>
          <a-form-item label="并发运行数上限"><a-input-number v-model:value="own.maxConcurrentRuns" :min="1" style="width: 100%" placeholder="不设置" :disabled="!canManage" /></a-form-item>
          <a-form-item label="单次运行最长（分钟）"><a-input-number v-model:value="own.maxRunMinutes" :min="1" style="width: 100%" placeholder="不设置" :disabled="!canManage" /></a-form-item>
          <a-form-item label="每日消息数上限"><a-input-number v-model:value="own.maxMessagesPerDay" :min="1" style="width: 100%" placeholder="不设置" :disabled="!canManage" /></a-form-item>
          <a-form-item label="每日 Token 上限"><a-input-number v-model:value="own.maxTokensPerDay" :min="1" style="width: 100%" placeholder="不设置" :disabled="!canManage" /></a-form-item>
        </div>
        <a-form-item label="默认共享空间（新建 Workspace 时自动挂载）">
          <a-select v-model:value="own.defaultSharedSpaceIds" mode="multiple" placeholder="选择本 Pool 已获授权的共享空间" :disabled="!canManage"
            :options="(sharedSpaces || []).map((s: any) => ({ value: s.id, label: s.name }))" />
        </a-form-item>
        <template v-if="auth.isAdmin">
          <a-divider style="margin: 8px 0 20px">平台管理员授权（不继承，仅本 Pool 生效）</a-divider>
          <div class="grid grid-3">
            <a-form-item label="允许自行新增模型"><a-switch :checked="!!own.allowOwnModels" @change="(v: any) => (own.allowOwnModels = v)" /></a-form-item>
            <a-form-item label="允许自行新增 MCP"><a-switch :checked="!!own.allowOwnMcp" @change="(v: any) => (own.allowOwnMcp = v)" /></a-form-item>
            <a-form-item label="允许自行新增 Skill"><a-switch :checked="own.allowOwnSkills !== false" @change="(v: any) => (own.allowOwnSkills = v)" /></a-form-item>
          </div>
        </template>
        <a-button v-if="canManage" type="primary" :loading="saving" @click="save">保存策略</a-button>
      </a-form>
    </div>

    <div class="card">
      <div class="card-title">有效策略<span class="sub">最终生效值与来源</span></div>
      <div class="info-list">
        <div class="info-item"><span class="text-2">网络访问</span><span class="spacer" /><span class="tag dot" :class="eff.allowNetwork ? 'ok' : 'danger'">{{ eff.allowNetwork ? '允许' : '禁止' }}</span><span class="muted small">{{ src('allowNetwork') }}</span></div>
        <div class="info-item"><span class="text-2">Workspace 终端</span><span class="spacer" /><span class="tag dot" :class="eff.allowTerminal ? 'ok' : 'danger'">{{ eff.allowTerminal ? '允许' : '禁止' }}</span><span class="muted small">{{ src('allowTerminal') }}</span></div>
        <div class="info-item"><span class="text-2">沙箱模式</span><span class="spacer" /><b class="mono">{{ eff.sandboxMode }}</b><span class="muted small">{{ src('sandboxMode') }}</span></div>
        <div class="info-item"><span class="text-2">Workspace 数量</span><span class="spacer" /><b>{{ eff.maxWorkspaces || '不限' }}</b><span class="muted small">{{ src('maxWorkspaces') }}</span></div>
        <div class="info-item"><span class="text-2">单 Workspace 存储</span><span class="spacer" /><b>{{ eff.maxStorageMbPerWorkspace ? eff.maxStorageMbPerWorkspace + ' MB' : '不限' }}</b><span class="muted small">{{ src('maxStorageMbPerWorkspace') }}</span></div>
        <div class="info-item"><span class="text-2">并发运行数</span><span class="spacer" /><b>{{ eff.maxConcurrentRuns || '不限' }}</b><span class="muted small">{{ src('maxConcurrentRuns') }}</span></div>
        <div class="info-item"><span class="text-2">单次运行时长</span><span class="spacer" /><b>{{ eff.maxRunMinutes ? eff.maxRunMinutes + ' 分钟' : '不限' }}</b><span class="muted small">{{ src('maxRunMinutes') }}</span></div>
        <div class="info-item"><span class="text-2">每日消息数</span><span class="spacer" /><b>{{ eff.maxMessagesPerDay || '不限' }}</b><span class="muted small">{{ src('maxMessagesPerDay') }}</span></div>
        <div class="info-item"><span class="text-2">每日 Token</span><span class="spacer" /><b>{{ eff.maxTokensPerDay || '不限' }}</b><span class="muted small">{{ src('maxTokensPerDay') }}</span></div>
        <div class="info-item"><span class="text-2">自行新增</span><span class="spacer" /><span class="tag" :class="eff.allowOwnModels ? 'ok' : ''">模型</span><span class="tag" :class="eff.allowOwnMcp ? 'ok' : ''">MCP</span><span class="tag" :class="eff.allowOwnSkills ? 'ok' : ''">Skill</span></div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.span2 { grid-column: span 2; }
</style>
