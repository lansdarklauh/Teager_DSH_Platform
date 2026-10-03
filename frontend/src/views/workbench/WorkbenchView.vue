<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, reactive, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { message, Modal } from 'ant-design-vue'
import { marked } from 'marked'
import DOMPurify from 'dompurify'
import {
  PlusOutlined, SendOutlined, StopOutlined, MessageOutlined, FolderOpenOutlined, CodeOutlined, ProfileOutlined, ToolOutlined, ThunderboltOutlined,
  AlertOutlined, RobotOutlined, UserOutlined, EditOutlined, DeleteOutlined, CopyOutlined, DownloadOutlined, InboxOutlined, MoreOutlined, LeftOutlined, RightOutlined, BulbOutlined
} from '@ant-design/icons-vue'
import { wsApi, sessionApi, poolApi } from '@/api'
import { fmtAgo, fmtShort } from '@/utils/format'
import Tags from '@/components/Tags.vue'
import FileManager from '@/components/FileManager.vue'
import TerminalPanel from '@/components/TerminalPanel.vue'
import EventTimeline from '@/components/EventTimeline.vue'

const route = useRoute()
const router = useRouter()
const wsId = computed(() => route.params.id as string)
const sid = computed(() => (route.params.sid as string) || '')

// ── 数据 ──
const wsData = ref<any>(null)
const mounts = ref<any[]>([])
const config = ref<any[]>([])
const sessions = ref<any[]>([])
const keyword = ref('')
const session = ref<any>(null)
const messages = ref<any[]>([])
const input = ref('')
const modelKey = ref<string>('')
const rightOpen = ref(true)
const rightTab = ref('files')
const fm = ref<any>(null)
const listEl = ref<HTMLElement>()

/** 当前运行的实时状态；run_end 后保留事件供“工具 / 进度”面板查看最近一次运行。 */
const live = reactive({ running: false, runId: '', text: '', thinking: '', events: [] as any[], error: '', alerts: [] as any[] })
let lastSeq = 0
let ctrl: AbortController | null = null

const v = computed(() => wsData.value?.view)
const role = computed(() => v.value?.myRole)
const canSend = computed(() => (role.value === 'Owner' || role.value === 'Admin' || role.value === 'Member') && v.value?.status !== 'Archived')
const models = computed(() => config.value.filter((c) => c.kind === 'Model' && c.enabled))
const skills = computed(() => config.value.filter((c) => c.kind === 'Skill' && c.enabled))
const mcps = computed(() => config.value.filter((c) => c.kind === 'Mcp' && c.enabled))
const fileAreas = computed(() => [{ value: 'files', label: '私有文件' }, ...mounts.value.filter((m) => m.active).map((m) => ({ value: `shared/${m.mountName}`, label: `共享：${m.mountName}`, hint: `${m.sharedSpaceName} · ${m.effectivePermission}` }))])
const terminalReason = computed(() => (!wsData.value?.terminalAllowed || !wsData.value?.policy?.allowTerminal ? '终端已被平台或 Pool 策略禁用' : role.value === 'Viewer' ? '只读角色不能使用终端' : ''))
const toolCount = computed(() => live.events.filter((e) => e.type === 'tool_call').length)

async function loadWorkspace() {
  wsData.value = await wsApi.get(wsId.value)
  mounts.value = ((await wsApi.mounts(wsId.value)) as any).mounts
  config.value = (await wsApi.effectiveConfig(wsId.value)) as any[]
}
async function loadSessions() { sessions.value = (await sessionApi.list(wsId.value, { q: keyword.value })) as any[] }
let skipLoad = false
async function loadSession() {
  if (skipLoad) { skipLoad = false; return }
  stopStream(); Object.assign(live, { running: false, runId: '', text: '', thinking: '', events: [], error: '', alerts: [] })
  if (!sid.value) { session.value = null; messages.value = []; return }
  const r: any = await sessionApi.get(sid.value)
  session.value = r.session; lastSeq = r.lastEventSeq; modelKey.value = r.session.modelKey || ''
  messages.value = (await sessionApi.messages(sid.value)) as any[]
  scrollDown()
  if (r.session.running) attach(lastSeq)
}
watch(wsId, async () => { await loadWorkspace(); await loadSessions() }, { immediate: true })
watch(sid, loadSession, { immediate: true })
let kwTimer: any
watch(keyword, () => { clearTimeout(kwTimer); kwTimer = setTimeout(loadSessions, 300) })

// ── SSE（fetch 流式，携带 Cookie） ──
function stopStream() { ctrl?.abort(); ctrl = null }
async function attach(after: number) {
  stopStream()
  const c = new AbortController(); ctrl = c
  live.running = true
  try {
    const res = await fetch(sessionApi.eventsUrl(sid.value, after), { credentials: 'include', headers: { 'X-Requested-With': 'dsh-web' }, signal: c.signal })
    if (!res.ok || !res.body) throw new Error(`事件流连接失败（${res.status}）`)
    const reader = res.body.getReader(); const dec = new TextDecoder(); let buf = ''
    for (;;) {
      const { done, value } = await reader.read()
      if (done) break
      buf += dec.decode(value, { stream: true })
      let i: number
      while ((i = buf.indexOf('\n\n')) >= 0) {
        const frame = buf.slice(0, i); buf = buf.slice(i + 2)
        const data = frame.split('\n').filter((l) => l.startsWith('data: ')).map((l) => l.slice(6)).join('\n')
        if (data) handle(JSON.parse(data))
      }
    }
  } catch (e: any) { if (e.name !== 'AbortError') live.error = e.message } finally {
    if (ctrl === c) { ctrl = null; if (live.running) { live.running = false; refreshAfterRun() } }
  }
}
function handle(e: any) {
  lastSeq = Math.max(lastSeq, e.seq || 0)
  live.events.push(e)
  switch (e.type) {
    case 'run_start': live.running = true; live.runId = e.runId; live.text = ''; live.thinking = ''; live.error = ''; break
    case 'text': live.text += e.text || ''; scrollDown(); break
    case 'thinking': if (e.text) live.thinking += e.text; break
    case 'error': live.error = e.message; break
    case 'alert': live.alerts.push(e); message.warning(e.message); break
    case 'final': if (e.text && !live.text) live.text = e.text; break
    case 'run_end': live.running = false; if (e.status === 'Failed' && e.error) live.error = e.error; refreshAfterRun(); break
  }
}
async function refreshAfterRun() {
  if (!sid.value) return
  messages.value = (await sessionApi.messages(sid.value)) as any[]
  live.text = ''; live.thinking = ''
  loadSessions(); fm.value?.reload?.(); scrollDown()
  const r: any = await sessionApi.get(sid.value); session.value = r.session
}
function scrollDown() { nextTick(() => listEl.value && (listEl.value.scrollTop = listEl.value.scrollHeight)) }

// ── 会话操作 ──
async function newSession() { const s: any = await sessionApi.create({ workspaceId: wsId.value, model: modelKey.value || undefined }); await loadSessions(); router.push(`/w/${wsId.value}/s/${s.id}`) }
function openSession(id: string) { if (id !== sid.value) router.push(`/w/${wsId.value}/s/${id}`) }
async function send() {
  const text = input.value.trim()
  if (!text || live.running || !canSend.value) return
  let id = sid.value
  if (!id) { const s: any = await sessionApi.create({ workspaceId: wsId.value, model: modelKey.value || undefined }); id = s.id; session.value = s; lastSeq = 0; skipLoad = true; await router.push(`/w/${wsId.value}/s/${id}`); loadSessions() }
  input.value = ''
  messages.value.push({ id: 'tmp' + Date.now(), role: 'user', content: text, createdAt: new Date().toISOString() })
  Object.assign(live, { running: true, text: '', thinking: '', events: [], error: '', alerts: [] })
  scrollDown()
  try {
    await sessionApi.send(id, text, modelKey.value || undefined)
    attach(lastSeq)
  } catch (e: any) { live.running = false; live.error = e.friendly || '发送失败'; messages.value = (await sessionApi.messages(id)) as any[] }
}
async function cancel() { await sessionApi.cancel(sid.value); message.info('已发送取消请求') }
function onKey(e: KeyboardEvent) { if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) { e.preventDefault(); send() } }
async function changeModel(v: string) { modelKey.value = v || ''; if (sid.value) await sessionApi.update(sid.value, { model: modelKey.value }) }

const ren = ref({ open: false, id: '', title: '' })
async function doRename() { await sessionApi.update(ren.value.id, { title: ren.value.title }); ren.value.open = false; loadSessions(); if (ren.value.id === sid.value) session.value = { ...session.value, title: ren.value.title } }
function askDelete(s: any) {
  Modal.confirm({ title: `删除会话「${s.title}」？`, content: '消息与事件日志将一并删除。', okType: 'danger', okText: '删除', cancelText: '取消',
    onOk: async () => { await sessionApi.remove(s.id); await loadSessions(); if (s.id === sid.value) router.replace(`/w/${wsId.value}`) } })
}
async function dup(s: any) { const n: any = await sessionApi.duplicate(s.id); await loadSessions(); router.push(`/w/${wsId.value}/s/${n.id}`) }
async function archive(s: any) { await sessionApi.update(s.id, { archived: true }); await loadSessions(); if (s.id === sid.value) router.replace(`/w/${wsId.value}`) }

// 查看历史运行的执行过程
const runDrawer = ref<any>({ open: false, events: [], loading: false, title: '' })
async function openRun(m: any) { runDrawer.value = { open: true, events: [], loading: true, title: '执行过程' }; try { runDrawer.value.events = (await sessionApi.history(sid.value, m.runId)) as any[] } finally { runDrawer.value.loading = false } }

const md = (t: string) => DOMPurify.sanitize(marked.parse(t || '', { async: false }) as string)
const suggestions = ['列出当前 Workspace 里有哪些文件', '阅读 shared/ 下的资料并给出摘要', '在 files/ 下创建 README.md，写一份项目说明', '检查目录结构，指出可以改进的地方']
function useSuggestion(s: string) { input.value = s }
onBeforeUnmount(stopStream)
</script>

<template>
  <div v-if="v" class="wb" :class="{ 'no-right': !rightOpen }" data-mod="chat">
    <!-- 左：会话列表 -->
    <aside class="left">
      <div class="lh">
        <div class="ws-name" @click="router.push(`/workspaces/${wsId}`)"><span class="chip sm" data-mod="ws"><FolderOpenOutlined /></span><div class="ellipsis"><b class="ellipsis">{{ v.name }}</b><div class="muted small ellipsis">{{ v.poolName }}</div></div></div>
        <a-button v-if="canSend" type="primary" block style="margin-top: 14px" @click="newSession"><PlusOutlined /> 新建会话</a-button>
        <a-input v-model:value="keyword" allow-clear placeholder="搜索会话…" style="margin-top: 12px" />
      </div>
      <div class="sl">
        <div v-for="s in sessions" :key="s.id" class="si" :class="{ on: s.id === sid }" @click="openSession(s.id)">
          <span class="chip sm"><MessageOutlined /></span>
          <div class="ellipsis" style="flex: 1; min-width: 0"><div class="ellipsis" style="font-weight: 500">{{ s.title }}</div><div class="muted small">{{ s.messageCount }} 条 · {{ fmtAgo(s.updatedAt) }}</div></div>
          <span v-if="s.running" class="run-dot" title="运行中" />
          <a-dropdown v-if="canSend" :trigger="['click']" @click.stop>
            <a-button type="text" size="small" class="more" @click.stop><MoreOutlined /></a-button>
            <template #overlay><a-menu>
              <a-menu-item @click="ren = { open: true, id: s.id, title: s.title }"><EditOutlined /> 重命名</a-menu-item>
              <a-menu-item @click="dup(s)"><CopyOutlined /> 复制</a-menu-item>
              <a-menu-item @click="archive(s)"><InboxOutlined /> 归档</a-menu-item>
              <a-menu-item danger @click="askDelete(s)"><DeleteOutlined /> 删除</a-menu-item>
            </a-menu></template>
          </a-dropdown>
        </div>
        <a-empty v-if="!sessions.length" :image="false" description="还没有会话" style="margin-top: 40px" />
      </div>
    </aside>

    <!-- 中：对话 -->
    <section class="center">
      <header class="ch">
        <div class="ellipsis" style="min-width: 0"><b class="ellipsis" style="font-size: 16px">{{ session?.title || '新对话' }}</b>
          <div class="row-wrap" style="gap: 8px; margin-top: 2px"><Tags kind="role" :value="role" :path="v.path" /><Tags v-if="wsData.runtime" kind="runtime" :value="wsData.runtime.status" /><span class="muted small">{{ wsData.runtimeProvider }}</span></div></div>
        <span class="spacer" />
        <a-select :value="modelKey" style="width: 190px" placeholder="默认模型" :disabled="!canSend || live.running" @change="changeModel">
          <a-select-option value="">默认模型</a-select-option>
          <a-select-option v-for="m in models" :key="m.key" :value="m.key">{{ m.displayName || m.key }}<span v-if="m.isDefault" class="muted"> · 默认</span></a-select-option>
        </a-select>
        <a-dropdown v-if="session"><a-button><DownloadOutlined /> 导出</a-button><template #overlay><a-menu><a-menu-item><a :href="sessionApi.exportUrl(session.id, 'md')">Markdown</a></a-menu-item><a-menu-item><a :href="sessionApi.exportUrl(session.id, 'json')">JSON</a></a-menu-item></a-menu></template></a-dropdown>
        <a-button type="text" @click="rightOpen = !rightOpen"><component :is="rightOpen ? RightOutlined : LeftOutlined" /></a-button>
      </header>

      <div ref="listEl" class="msgs">
        <div v-if="!messages.length && !live.running" class="welcome">
          <span class="chip lg solid"><RobotOutlined /></span>
          <h2>开始一段 Agent 对话</h2>
          <p class="muted">Agent 的工作目录是本 Workspace：私有文件在 <span class="mono">files/</span>，挂载的共享空间在 <span class="mono">shared/</span>。</p>
          <div v-if="canSend" class="sugg"><div v-for="s in suggestions" :key="s" class="card hover" @click="useSuggestion(s)">{{ s }}</div></div>
          <a-alert v-else type="info" show-icon message="你在该 Workspace 是只读角色，可以查看会话与文件，不能发送消息。" />
        </div>

        <div v-for="m in messages" :key="m.id" class="m" :class="m.role">
          <span class="av"><UserOutlined v-if="m.role === 'user'" /><RobotOutlined v-else /></span>
          <div class="bubble">
            <div v-if="m.role === 'user'" style="white-space: pre-wrap">{{ m.content }}</div>
            <div v-else-if="m.role === 'assistant'" class="md" v-html="md(m.content)" />
            <div v-else class="sys">{{ m.content }}</div>
            <div class="mf"><span class="muted small">{{ fmtShort(m.createdAt) }}</span><a v-if="m.runId && m.role !== 'user'" class="small" @click="openRun(m)"><ToolOutlined /> 查看执行过程</a></div>
          </div>
        </div>

        <div v-if="live.running || live.text || live.error" class="m assistant live">
          <span class="av"><RobotOutlined /></span>
          <div class="bubble">
            <div v-if="live.running && !live.text" class="thinking"><a-spin size="small" /> 正在思考…<span v-if="toolCount" class="tag ws" style="margin-left: 8px">已调用 {{ toolCount }} 个工具</span></div>
            <details v-if="live.thinking" class="think"><summary><BulbOutlined /> 思考过程</summary><div>{{ live.thinking }}</div></details>
            <div v-if="live.text" class="md" v-html="md(live.text)" />
            <div v-if="live.running && live.text" class="typing"><span /><span /><span /></div>
            <a-alert v-if="live.error" type="error" show-icon :message="live.error" style="margin-top: 10px" />
          </div>
        </div>
      </div>

      <footer class="cf">
        <div class="box" :class="{ off: !canSend }">
          <a-textarea v-model:value="input" :auto-size="{ minRows: 1, maxRows: 8 }" :bordered="false" :disabled="!canSend" placeholder="向 Agent 描述任务…（Enter 发送，Shift+Enter 换行）" @keydown="onKey" />
          <div class="row" style="padding: 0 10px 8px">
            <span class="muted small">{{ canSend ? '所有操作限定在当前 Workspace 内，并受沙箱与共享空间权限约束' : '当前角色不能发送消息' }}</span><span class="spacer" />
            <a-button v-if="live.running" danger @click="cancel"><StopOutlined /> 停止</a-button>
            <a-button v-else type="primary" :disabled="!canSend || !input.trim()" @click="send"><SendOutlined /> 发送</a-button>
          </div>
        </div>
      </footer>
    </section>

    <!-- 右：工具面板 -->
    <aside v-show="rightOpen" class="right">
      <a-tabs v-model:active-key="rightTab" size="small" class="rt" :destroy-inactive-tab-pane="false">
        <a-tab-pane key="files"><template #tab><FolderOpenOutlined /> 文件</template><div class="pane"><FileManager ref="fm" :base="`/workspaces/${wsId}`" :areas="fileAreas" :can-write="canSend" /></div></a-tab-pane>
        <a-tab-pane key="terminal"><template #tab><CodeOutlined /> 终端</template><div class="pane"><TerminalPanel :workspace-id="wsId" :allowed="!terminalReason" :reason="terminalReason" /></div></a-tab-pane>
        <a-tab-pane key="plan"><template #tab><ProfileOutlined /> 进度</template><div class="pane"><div class="muted small" style="margin-bottom: 12px">最近一次运行的执行进度与 Token 用量</div><EventTimeline :events="live.events" mode="plan" /></div></a-tab-pane>
        <a-tab-pane key="tools"><template #tab><ToolOutlined /> 工具<span v-if="toolCount" class="tag ws" style="margin-left: 4px">{{ toolCount }}</span></template><div class="pane"><EventTimeline :events="live.events" mode="tools" /></div></a-tab-pane>
        <a-tab-pane key="skills"><template #tab><ThunderboltOutlined /> 技能</template>
          <div class="pane">
            <div class="muted small" style="margin-bottom: 8px">本 Workspace 生效的配置（继承合并结果）</div>
            <div class="sec"><b>模型</b><div class="row-wrap mt-8"><span v-for="m in models" :key="m.key" class="tag chat">{{ m.displayName || m.key }}<small v-if="m.isDefault">默认</small></span><span v-if="!models.length" class="muted small">使用 Runtime 默认模型</span></div></div>
            <div class="sec"><b>Skills</b><div class="row-wrap mt-8"><a-tooltip v-for="s in skills" :key="s.key" :title="s.displayName"><span class="tag ws">{{ s.key }}</span></a-tooltip><span v-if="!skills.length" class="muted small">暂无</span></div></div>
            <div class="sec"><b>MCP</b><div class="row-wrap mt-8"><span v-for="s in mcps" :key="s.key" class="tag token">{{ s.key }}</span><span v-if="!mcps.length" class="muted small">暂无</span></div></div>
            <div class="sec"><b>共享空间挂载</b><div class="row-wrap mt-8"><span v-for="m in mounts" :key="m.id" class="tag space">{{ m.mountName }} · {{ m.effectivePermission || '无权限' }}</span><span v-if="!mounts.length" class="muted small">暂无</span></div></div>
          </div>
        </a-tab-pane>
        <a-tab-pane key="alerts"><template #tab><AlertOutlined /> 告警<span v-if="live.alerts.length" class="tag danger" style="margin-left: 4px">{{ live.alerts.length }}</span></template>
          <div class="pane">
            <div class="note warn" style="margin-bottom: 14px"><span>安全类告警（如 Agent 修改了只读共享空间）会显示在这里，并写入审计日志。当前版本的工具调用由沙箱策略自动放行 / 限制，不需要人工审批。</span></div>
            <div v-for="(a, i) in live.alerts" :key="i" class="note danger" style="margin-bottom: 10px"><span>{{ a.message }}</span></div>
            <a-empty v-if="!live.alerts.length" :image="false" description="暂无告警" />
          </div>
        </a-tab-pane>
      </a-tabs>
    </aside>

    <a-modal v-model:open="ren.open" title="重命名会话" ok-text="确定" cancel-text="取消" @ok="doRename"><a-input v-model:value="ren.title" @press-enter="doRename" /></a-modal>
    <a-drawer v-model:open="runDrawer.open" :title="runDrawer.title" width="560"><a-spin :spinning="runDrawer.loading"><EventTimeline :events="runDrawer.events" mode="all" /></a-spin></a-drawer>
  </div>
  <div v-else class="page"><a-skeleton active /></div>
</template>

<style scoped>
.wb { display: grid; grid-template-columns: 290px minmax(0, 1fr) 440px; height: calc(100vh - 62px); background: var(--bg); }
.wb.no-right { grid-template-columns: 290px minmax(0, 1fr); }
.left { background: var(--surface); border-right: 1px solid var(--border); display: flex; flex-direction: column; min-height: 0; }
.lh { padding: 18px 16px 14px; border-bottom: 1px solid var(--border); }
.ws-name { display: flex; align-items: center; gap: 12px; cursor: pointer; }
.sl { flex: 1; overflow: auto; padding: 10px; }
.si { display: flex; align-items: center; gap: 10px; padding: 10px; border-radius: 12px; cursor: pointer; margin-bottom: 4px; }
.si:hover { background: var(--admin-soft); }
.si.on { background: var(--chat-soft); }
.si.on .chip { background: var(--chat); color: #fff; }
.si .more { opacity: 0; } .si:hover .more { opacity: 1; }
.run-dot { width: 8px; height: 8px; border-radius: 50%; background: var(--ok); box-shadow: 0 0 0 4px var(--ok-soft); flex: none; }
.center { display: flex; flex-direction: column; min-width: 0; min-height: 0; }
.ch { display: flex; align-items: center; gap: 12px; padding: 12px 24px; background: var(--surface); border-bottom: 1px solid var(--border); }
.msgs { flex: 1; overflow: auto; padding: 28px 8%; display: flex; flex-direction: column; gap: 22px; }
.m { display: flex; gap: 14px; max-width: 900px; width: 100%; }
.m.user { align-self: flex-end; flex-direction: row-reverse; }
.av { width: 34px; height: 34px; border-radius: 11px; display: flex; align-items: center; justify-content: center; flex: none; background: var(--chat-soft); color: var(--chat); font-size: 16px; }
.m.user .av { background: var(--ws-soft); color: var(--ws); }
.bubble { background: var(--surface); border: 1px solid var(--border); border-radius: 16px; padding: 14px 18px; min-width: 0; max-width: calc(100% - 50px); }
.m.user .bubble { background: var(--chat); color: #fff; border-color: var(--chat); }
.m.user .bubble .muted { color: rgba(255,255,255,.75); }
.m.system .bubble { background: var(--danger-soft); border-color: #f5c6c6; }
.sys { color: #991b1b; white-space: pre-wrap; }
.mf { display: flex; gap: 14px; margin-top: 8px; }
.thinking { color: var(--text-3); display: flex; align-items: center; gap: 10px; }
.think { margin-bottom: 10px; color: var(--text-3); font-size: 13px; } .think summary { cursor: pointer; } .think div { margin-top: 6px; white-space: pre-wrap; max-height: 160px; overflow: auto; }
.typing { display: flex; gap: 4px; margin-top: 8px; } .typing span { width: 6px; height: 6px; border-radius: 50%; background: var(--chat); opacity: .35; animation: blink 1.2s infinite; } .typing span:nth-child(2) { animation-delay: .2s; } .typing span:nth-child(3) { animation-delay: .4s; }
@keyframes blink { 50% { opacity: 1; } }
.welcome { margin: auto; text-align: center; max-width: 620px; display: flex; flex-direction: column; align-items: center; gap: 10px; }
.welcome h2 { margin: 10px 0 0; font-size: 22px; }
.sugg { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; margin-top: 18px; width: 100%; text-align: left; } .sugg .card { padding: 14px 16px; font-size: 13px; color: var(--text-2); }
.cf { padding: 0 8% 22px; }
.box { max-width: 900px; margin: 0 auto; background: var(--surface); border: 1px solid var(--border-strong); border-radius: 18px; padding-top: 6px; transition: border-color .15s, box-shadow .15s; }
.box:focus-within { border-color: var(--chat); box-shadow: 0 0 0 3px var(--chat-soft); }
.box.off { background: var(--surface-2); }
.right { background: var(--surface); border-left: 1px solid var(--border); min-height: 0; display: flex; flex-direction: column; }
.rt { flex: 1; min-height: 0; display: flex; flex-direction: column; }
.rt :deep(.ant-tabs-nav) { padding: 8px 14px 0; margin: 0 !important; }
.rt :deep(.ant-tabs-content-holder) { flex: 1; overflow: auto; }
.pane { padding: 18px 16px 24px; }
.sec { padding: 14px 0; border-bottom: 1px solid var(--border); } .sec:last-child { border-bottom: 0; }
@media (max-width: 1280px) { .wb { grid-template-columns: 250px minmax(0, 1fr) 380px; } .msgs, .cf { padding-left: 4%; padding-right: 4%; } }
</style>
