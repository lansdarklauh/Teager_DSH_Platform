<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { message, Modal } from 'ant-design-vue'
import { PlusOutlined, EditOutlined, DeleteOutlined, StopOutlined } from '@ant-design/icons-vue'
import { configApi, poolApi, wsApi } from '@/api'

/**
 * 模型 / Skill / MCP 配置面板：上方展示“继承合并后的有效配置”（含来源与被谁停用），下方维护“本层定义 / 覆盖”。
 * 规则：子级可停用上级项（收紧），不能重新启用被上级停用的项；新增全新的模型 / MCP 需要管理员授权或 Pool 策略允许。
 */
const props = defineProps<{ scope: 'Platform' | 'Pool' | 'Workspace'; scopeId?: string; kind: 'Model' | 'Skill' | 'Mcp' | 'Plugin'; canEdit: boolean }>()
const kindText = { Model: '模型', Skill: 'Skill', Mcp: 'MCP 服务', Plugin: '插件' }[props.kind]
const scopeText = { Platform: '平台', Pool: '本 Pool', Workspace: '本 Workspace' }[props.scope]

const effective = ref<any[]>([])
const own = ref<any[]>([])
const loading = ref(false)
async function load() {
  loading.value = true
  try {
    own.value = (await configApi.list(props.scope, props.scopeId, props.kind)) as any[]
    if (props.scope === 'Pool') effective.value = ((await poolApi.effectiveConfig(props.scopeId!)) as any[]).filter((e) => e.kind === props.kind)
    else if (props.scope === 'Workspace') effective.value = ((await wsApi.effectiveConfig(props.scopeId!)) as any[]).filter((e) => e.kind === props.kind)
    else effective.value = []
  } finally { loading.value = false }
}
watch(() => [props.scope, props.scopeId, props.kind], load, { immediate: true })

// 编辑弹窗
const form = ref<any>({})
const open = ref(false)
const saving = ref(false)
const isEdit = ref(false)
function blank() {
  return { key: '', displayName: '', enabled: true, content: '', provider: 'deepseek-official', model: '', baseUrl: '', apiKeyEnv: '', reasoningEffort: '', isDefault: false,
    transport: 'stdio', command: '', args: '', env: '', url: '', headers: '', package: '', pluginConfig: '' }
}
function add() { form.value = blank(); isEdit.value = false; open.value = true }
function edit(r: any) {
  const p = r.params || {}
  form.value = { ...blank(), key: r.key, displayName: r.displayName || '', enabled: r.enabled, content: r.content || '', ...p,
    args: (p.args || []).join('\n'), env: Object.entries(p.env || {}).map(([k, v]) => `${k}=${v}`).join('\n'), headers: Object.entries(p.headers || {}).map(([k, v]) => `${k}=${v}`).join('\n'), pluginConfig: p.config ? JSON.stringify(p.config, null, 2) : '' }
  isEdit.value = true; open.value = true
}
const lines = (s: string) => s.split('\n').map((x) => x.trim()).filter(Boolean)
const kvObj = (s: string) => Object.fromEntries(lines(s).map((l) => [l.slice(0, l.indexOf('=')), l.slice(l.indexOf('=') + 1)]))
function buildParams() {
  const f = form.value
  if (props.kind === 'Model') return { provider: f.provider || undefined, model: f.model || undefined, baseUrl: f.baseUrl || undefined, apiKeyEnv: f.apiKeyEnv || undefined, reasoningEffort: f.reasoningEffort || undefined, isDefault: !!f.isDefault }
  if (props.kind === 'Mcp') return f.transport === 'stdio' ? { transport: 'stdio', command: f.command, args: lines(f.args), env: kvObj(f.env) } : { transport: 'streamable-http', url: f.url, headers: kvObj(f.headers) }
  if (props.kind === 'Plugin') {
    let cfg: any
    try { cfg = f.pluginConfig?.trim() ? JSON.parse(f.pluginConfig) : undefined } catch { throw new Error('插件配置必须是合法的 JSON 对象') }
    return { package: f.package, config: cfg }
  }
  return undefined
}
async function save() {
  saving.value = true
  try {
    buildParams() // 先校验插件配置 JSON
    await configApi.upsert({ kind: props.kind, scope: props.scope, scopeId: props.scopeId, key: form.value.key, displayName: form.value.displayName || undefined, enabled: form.value.enabled, params: buildParams(), content: props.kind === 'Skill' ? form.value.content : undefined })
    message.success('已保存'); open.value = false; load()
  } catch (e: any) { if (!e.response) message.error(e.message) } finally { saving.value = false }
}
function remove(r: any) {
  Modal.confirm({ title: `删除本层的「${r.key}」？`, content: '删除后将回退为继承自上级的配置。', okType: 'danger', okText: '删除', cancelText: '取消', onOk: async () => { await configApi.remove(r.id); load() } })
}
/** 在本层停用某个继承项（收紧）；参数沿用上级（服务端对继承项只接受“停用”这类收紧操作）。 */
async function toggleInherited(e: any) {
  await configApi.upsert({ kind: props.kind, scope: props.scope, scopeId: props.scopeId, key: e.key, enabled: false, displayName: e.displayName || undefined, params: Object.keys(e.params || {}).length ? e.params : undefined })
  message.success('已在本层停用'); load()
}
const isOwn = (key: string) => own.value.some((o) => o.key === key)
const parse = (j?: string | null) => { try { return j ? JSON.parse(j) : {} } catch { return {} } }
const effRows = computed(() => effective.value.map((e) => ({ ...e, params: parse(e.paramsJson) })))
</script>

<template>
  <div class="stack">
    <div v-if="scope !== 'Platform'" class="card flush">
      <div class="card-head"><h3>有效{{ kindText }}<span class="muted small" style="font-weight: 400"> 　继承合并后的最终结果</span></h3></div>
      <a-table :data-source="effRows" :loading="loading" :pagination="false" row-key="key" size="middle" :locale="{ emptyText: `暂无有效${kindText}` }">
        <a-table-column title="名称" key="key">
          <template #default="{ record }"><b>{{ record.key }}</b><div v-if="record.displayName" class="muted small ellipsis" style="max-width: 320px">{{ record.displayName }}</div></template>
        </a-table-column>
        <a-table-column v-if="kind === 'Model'" title="Provider / 地址" key="p"><template #default="{ record }"><span class="mono">{{ record.params.provider || '—' }}</span><div v-if="record.params.baseUrl" class="muted small mono">{{ record.params.baseUrl }}</div></template></a-table-column>
        <a-table-column v-if="kind === 'Plugin'" title="插件包" key="pk"><template #default="{ record }"><span class="mono">{{ record.params.package }}</span></template></a-table-column>
        <a-table-column v-if="kind === 'Mcp'" title="连接方式" key="m"><template #default="{ record }"><span class="tag token">{{ record.params.transport }}</span> <span class="mono muted small">{{ record.params.command || record.params.url }}</span></template></a-table-column>
        <a-table-column title="状态" key="s" :width="190">
          <template #default="{ record }">
            <span v-if="record.enabled" class="tag ok dot">启用</span>
            <a-tooltip v-else :title="`被「${record.disabledAt}」停用`"><span class="tag danger dot">已停用 · {{ record.disabledAt }}</span></a-tooltip>
            <span v-if="record.isDefault" class="tag chat" style="margin-left: 6px">默认</span>
          </template>
        </a-table-column>
        <a-table-column title="定义于" key="d" :width="150"><template #default="{ record }"><a-tooltip :title="record.chain.join(' → ')"><span class="tag pool">{{ record.definedAt }}</span></a-tooltip></template></a-table-column>
        <a-table-column v-if="canEdit" title="操作" key="op" :width="150" align="right">
          <template #default="{ record }">
            <a-button v-if="!isOwn(record.key) && record.enabled" type="text" size="small" danger @click="toggleInherited(record)"><StopOutlined /> 在本层停用</a-button>
            <span v-else-if="isOwn(record.key)" class="muted small">本层已覆盖</span>
          </template>
        </a-table-column>
      </a-table>
    </div>

    <div class="card flush">
      <div class="card-head">
        <h3>{{ scopeText }}定义 / 覆盖的{{ kindText }}</h3>
        <a-button v-if="canEdit" type="primary" @click="add"><PlusOutlined /> 新增{{ kindText }}</a-button>
      </div>
      <a-table :data-source="own" :loading="loading" :pagination="false" row-key="id" size="middle" :locale="{ emptyText: `${scopeText}还没有定义${kindText}` }">
        <a-table-column title="名称" key="key"><template #default="{ record }"><b>{{ record.key }}</b><div v-if="record.displayName" class="muted small">{{ record.displayName }}</div></template></a-table-column>
        <a-table-column title="状态" key="e" :width="100"><template #default="{ record }"><span class="tag dot" :class="record.enabled ? 'ok' : 'danger'">{{ record.enabled ? '启用' : '停用' }}</span></template></a-table-column>
        <a-table-column title="更新" key="u" :width="180"><template #default="{ record }"><span class="muted small">{{ record.updatedBy }} · {{ record.updatedAt?.slice(0, 16).replace('T', ' ') }}</span></template></a-table-column>
        <a-table-column v-if="canEdit" title="操作" key="op" :width="120" align="right">
          <template #default="{ record }"><a-space :size="4"><a-button type="text" size="small" @click="edit(record)"><EditOutlined /></a-button><a-button type="text" size="small" danger @click="remove(record)"><DeleteOutlined /></a-button></a-space></template>
        </a-table-column>
      </a-table>
    </div>

    <a-modal v-model:open="open" :title="`${isEdit ? '编辑' : '新增'}${kindText}（${scopeText}）`" :confirm-loading="saving" ok-text="保存" cancel-text="取消" width="640px" @ok="save">
      <a-form layout="vertical">
        <a-form-item :label="kind === 'Skill' ? 'Skill 名称（小写字母/数字/连字符）' : kind === 'Mcp' ? '服务名（字母/数字/下划线，≤32）' : kind === 'Plugin' ? '插件标识（字母/数字/点/下划线/连字符）' : '模型键（如 deepseek-flash）'" required>
          <a-input v-model:value="form.key" :disabled="isEdit" />
        </a-form-item>
        <a-form-item :label="kind === 'Skill' ? '描述（写清楚“什么时候使用”，Agent 据此决定是否调用）' : '显示名'"><a-input v-model:value="form.displayName" /></a-form-item>
        <a-form-item label="启用"><a-switch v-model:checked="form.enabled" /></a-form-item>

        <template v-if="kind === 'Model'">
          <div class="grid grid-2">
            <a-form-item label="Provider"><a-input v-model:value="form.provider" placeholder="deepseek-official 或自定义" /></a-form-item>
            <a-form-item label="模型 ID（默认同模型键）"><a-input v-model:value="form.model" /></a-form-item>
            <a-form-item label="OpenAI 兼容 Base URL（自定义模型时填写）"><a-input v-model:value="form.baseUrl" placeholder="https://host/v1" /></a-form-item>
            <a-form-item label="API Key 环境变量名（对应密钥名称）"><a-input v-model:value="form.apiKeyEnv" placeholder="DEEPSEEK_API_KEY" /></a-form-item>
            <a-form-item label="推理强度"><a-select v-model:value="form.reasoningEffort" allow-clear :options="[{ value: 'low' }, { value: 'medium' }, { value: 'high' }]" /></a-form-item>
            <a-form-item label="设为默认模型"><a-switch v-model:checked="form.isDefault" /></a-form-item>
          </div>
        </template>
        <template v-else-if="kind === 'Mcp'">
          <a-form-item label="传输方式"><a-radio-group v-model:value="form.transport"><a-radio value="stdio">stdio（本地命令）</a-radio><a-radio value="streamable-http">Streamable HTTP（远程）</a-radio></a-radio-group></a-form-item>
          <template v-if="form.transport === 'stdio'">
            <a-form-item label="命令"><a-input v-model:value="form.command" placeholder="npx" /></a-form-item>
            <a-form-item label="参数（每行一个）"><a-textarea v-model:value="form.args" :rows="3" placeholder="-y&#10;@modelcontextprotocol/server-github" /></a-form-item>
            <a-form-item label="环境变量（每行 KEY=VALUE，值写 ${密钥名} 可引用密钥，不会落盘明文）"><a-textarea v-model:value="form.env" :rows="3" placeholder="GITHUB_TOKEN=${GITHUB_TOKEN}" /></a-form-item>
          </template>
          <template v-else>
            <a-form-item label="URL"><a-input v-model:value="form.url" placeholder="https://host/mcp" /></a-form-item>
            <a-form-item label="请求头（每行 KEY=VALUE，可用 ${密钥名}）"><a-textarea v-model:value="form.headers" :rows="3" /></a-form-item>
          </template>
        </template>
        <template v-else-if="kind === 'Plugin'">
          <a-form-item label="插件包名（npm 包 / 模块标识）" required><a-input v-model:value="form.package" placeholder="@deepseek-ai/dsh-xxx" class="mono" /></a-form-item>
          <a-form-item label="插件配置（JSON 对象，可空）"><a-textarea v-model:value="form.pluginConfig" :rows="6" class="mono" placeholder='{ "key": "value" }' /></a-form-item>
          <div class="note warn"><span>插件属于第三方可执行代码：新增需平台管理员授权。平台只写入声明式配置，<b>插件包必须已安装</b>在运行镜像 / Pool 的 DSH profile 中（<span class="mono">dsh plugin --profile headless add &lt;包名&gt;</span>）。</span></div>
        </template>
        <template v-else>
          <a-form-item label="Skill 正文（Markdown，将写入 SKILL.md）"><a-textarea v-model:value="form.content" :rows="10" class="mono" /></a-form-item>
        </template>
      </a-form>
    </a-modal>
  </div>
</template>
