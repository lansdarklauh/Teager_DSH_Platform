<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import {
  HomeOutlined, ApartmentOutlined, AppstoreOutlined, FolderOpenOutlined, BarChartOutlined, KeyOutlined, AuditOutlined, TeamOutlined,
  CloudServerOutlined, SettingOutlined, DashboardOutlined, LogoutOutlined, UserOutlined, SearchOutlined, MenuFoldOutlined, MenuUnfoldOutlined
} from '@ant-design/icons-vue'
import { useAuthStore } from '@/stores/auth'
import { sessionApi } from '@/api'
import { fmtAgo } from '@/utils/format'

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()

/** 侧边导航：每一项使用各自模块的主题色（图标色块 + 选中底色）。 */
const groups = computed(() => !auth.isAdmin ? [{ title: '工作', items: [
  { key: 'home', path: auth.landingPath, label: '我的工作台', icon: HomeOutlined, mod: 'chat' },
  { key: 'accessible-pools', path: '/accessible-pools', label: '可访问 Pool', icon: ApartmentOutlined, mod: 'pool' },
  { key: 'spaces', path: '/shared-spaces', label: '授权共享资源', icon: FolderOpenOutlined, mod: 'space' },
  { key: 'tokens', path: '/tokens', label: 'API 接入', icon: KeyOutlined, mod: 'token' },
  { key: 'profile', path: '/profile', label: '个人设置', icon: UserOutlined, mod: 'admin' }
] }] : [
  { title: '工作', items: [
    { key: 'home', path: '/app', label: '我的工作台', icon: HomeOutlined, mod: 'pool' },
    { key: 'pools', path: '/pools', label: 'Pool 管理', icon: ApartmentOutlined, mod: 'pool' },
    { key: 'workspaces', path: '/workspaces', label: '工作空间', icon: AppstoreOutlined, mod: 'ws' },
    { key: 'spaces', path: '/shared-spaces', label: '共享空间', icon: FolderOpenOutlined, mod: 'space' }
  ] },
  { title: '治理', items: [
    { key: 'usage', path: '/usage', label: '用量统计', icon: BarChartOutlined, mod: 'usage' },
    { key: 'tokens', path: '/tokens', label: 'API Token', icon: KeyOutlined, mod: 'token' },
    { key: 'audit', path: '/audit', label: '审计日志', icon: AuditOutlined, mod: 'audit' }
  ] },
  ...(auth.isAdmin ? [{ title: '平台管理', items: [
    { key: 'users', path: '/users', label: '用户管理', icon: TeamOutlined, mod: 'admin' },
    { key: 'runtimes', path: '/runtimes', label: '运行环境', icon: CloudServerOutlined, mod: 'runtime' },
    { key: 'platform-config', path: '/platform-config', label: '平台配置', icon: SettingOutlined, mod: 'config' },
    { key: 'system', path: '/system', label: '系统信息', icon: DashboardOutlined, mod: 'admin' }
  ] }] : [])
])

/** 当前路由属于哪一项导航（详情页归属列表页）。 */
const activeKey = computed(() => {
  const n = String(route.name || '')
  if (n === 'pool') return 'pools'
  if (!auth.isAdmin && n.startsWith('workbench')) return 'home'
  if (n === 'workspace' || n === 'workbench' || n === 'workbench-session') return 'workspaces'
  if (n === 'space') return 'spaces'
  return n
})

const manualCollapsed = ref(false)
const compact = computed(() => !!route.meta.compact)
const collapsed = computed(() => manualCollapsed.value || compact.value)
watch(() => route.fullPath, () => { keyword.value = ''; results.value = [] })

const keyword = ref('')
const results = ref<any[]>([])
const searching = ref(false)
let timer: any
/** 全局会话搜索（按标题 / 消息内容，仅限有权限的 Workspace）。 */
function onSearch() {
  clearTimeout(timer)
  if (!keyword.value.trim()) { results.value = []; return }
  timer = setTimeout(async () => {
    searching.value = true
    try { results.value = await sessionApi.search(keyword.value.trim()) } finally { searching.value = false }
  }, 300)
}
function openSession(r: any) { router.push(`/w/${r.workspaceId}/s/${r.id}`) }

async function logout() { await auth.logout(); router.replace('/login') }
const modOf = computed(() => (route.meta.mod as string) || 'pool')
/** 路由变化时重建页面；工作台在同一 Workspace 内切换会话时不重建（保留正在进行的流式运行状态）。 */
const viewKey = computed(() => (String(route.name).startsWith('workbench') ? `wb-${route.params.id}` : route.fullPath))
</script>

<template>
  <div class="shell" :data-mod="modOf" :class="{ collapsed }">
    <aside class="side">
      <div class="brand" @click="router.push('/app')">
        <span class="logo">D</span>
        <span v-if="!collapsed" class="brand-text"><b>DSH</b> 平台<small>多工作空间 Agent 平台</small></span>
      </div>
      <nav class="nav">
        <div v-for="g in groups" :key="g.title" class="nav-group">
          <div v-if="!collapsed" class="nav-title">{{ g.title }}</div>
          <div v-else class="nav-sep" />
          <a-tooltip v-for="it in g.items" :key="it.key" :title="collapsed ? it.label : ''" placement="right">
            <router-link :to="it.path" class="nav-item" :class="{ active: activeKey === it.key }" :data-mod="it.mod">
              <span class="chip sm"><component :is="it.icon" /></span>
              <span v-if="!collapsed" class="nav-label">{{ it.label }}</span>
            </router-link>
          </a-tooltip>
        </div>
      </nav>
      <div class="side-foot">
        <a-dropdown placement="topLeft" :trigger="['click']">
          <div class="me">
            <span class="avatar">{{ (auth.displayName || '?').slice(0, 1).toUpperCase() }}</span>
            <span v-if="!collapsed" class="me-text"><b class="ellipsis">{{ auth.displayName }}</b><small>{{ auth.isAdmin ? '平台管理员' : '普通用户' }}</small></span>
          </div>
          <template #overlay>
            <a-menu>
              <a-menu-item key="profile" @click="router.push('/profile')"><UserOutlined /> 个人设置</a-menu-item>
              <a-menu-item key="logout" @click="logout"><LogoutOutlined /> 退出登录</a-menu-item>
            </a-menu>
          </template>
        </a-dropdown>
      </div>
    </aside>

    <div class="main">
      <header class="top">
        <a-button type="text" class="fold" @click="manualCollapsed = !manualCollapsed">
          <component :is="collapsed ? MenuUnfoldOutlined : MenuFoldOutlined" />
        </a-button>
        <div class="crumb"><span class="dot" />{{ route.meta.title }}</div>
        <div class="spacer" />
        <a-popover :open="!!keyword && (results.length > 0 || searching)" placement="bottomRight" trigger="click" overlay-class-name="search-pop">
          <template #content>
            <a-spin :spinning="searching">
              <div class="search-list">
                <div v-if="!results.length && !searching" class="muted small" style="padding: 8px 4px">没有匹配的会话</div>
                <div v-for="r in results" :key="r.id" class="search-item" @click="openSession(r)">
                  <div class="ellipsis"><b>{{ r.title }}</b></div>
                  <div class="muted small ellipsis">{{ r.workspaceName }} · {{ r.messageCount }} 条消息 · {{ fmtAgo(r.updatedAt) }}</div>
                </div>
              </div>
            </a-spin>
          </template>
          <a-input v-model:value="keyword" class="search" placeholder="搜索会话标题或内容…" allow-clear @input="onSearch">
            <template #prefix><SearchOutlined class="muted" /></template>
          </a-input>
        </a-popover>
      </header>
      <main class="content">
        <router-view :key="viewKey" />
      </main>
    </div>
  </div>
</template>

<style scoped>
.shell { display: flex; height: 100vh; overflow: hidden; background: var(--bg); }
.side { width: 252px; background: var(--surface); border-right: 1px solid var(--border); display: flex; flex-direction: column; flex: none; transition: width .2s; }
.collapsed .side { width: 72px; }
.brand { display: flex; align-items: center; gap: 12px; padding: 22px 20px 18px; cursor: pointer; }
.collapsed .brand { justify-content: center; padding-left: 0; padding-right: 0; }
.logo { width: 38px; height: 38px; border-radius: 12px; background: var(--pool); color: #fff; display: flex; align-items: center; justify-content: center; font-weight: 800; font-size: 20px; flex: none; position: relative; }
.logo::after { content: ''; position: absolute; right: -3px; bottom: -3px; width: 12px; height: 12px; border-radius: 50%; background: var(--space); border: 2px solid var(--surface); }
.brand-text { display: flex; flex-direction: column; line-height: 1.2; font-size: 16px; }
.brand-text b { font-size: 17px; letter-spacing: .5px; }
.brand-text small { color: var(--text-3); font-size: 11px; margin-top: 3px; }
.nav { flex: 1; overflow-y: auto; padding: 4px 14px 12px; }
.nav-group { margin-bottom: 14px; }
.nav-title { font-size: 12px; color: var(--text-3); padding: 8px 10px 6px; letter-spacing: 1px; }
.nav-sep { height: 1px; background: var(--border); margin: 10px 8px; }
.nav-item { display: flex; align-items: center; gap: 12px; padding: 8px 10px; margin-bottom: 4px; border-radius: 12px; color: var(--text-2); transition: background .15s; }
.collapsed .nav-item { justify-content: center; padding: 8px 0; }
.nav-item:hover { background: var(--admin-soft); color: var(--text); }
.nav-item.active { background: var(--mod-soft); color: var(--mod-ink); font-weight: 600; }
.nav-item.active .chip { background: var(--mod); color: #fff; }
.nav-label { font-size: 14px; }
.side-foot { padding: 14px; border-top: 1px solid var(--border); }
.me { display: flex; align-items: center; gap: 12px; padding: 8px; border-radius: 12px; cursor: pointer; }
.collapsed .me { justify-content: center; padding: 8px 0; }
.me:hover { background: var(--admin-soft); }
.avatar { width: 36px; height: 36px; border-radius: 50%; background: var(--ws-soft); color: var(--ws-ink); display: flex; align-items: center; justify-content: center; font-weight: 700; flex: none; }
.me-text { display: flex; flex-direction: column; line-height: 1.3; min-width: 0; }
.me-text small { color: var(--text-3); font-size: 12px; }
.main { flex: 1; min-width: 0; display: flex; flex-direction: column; }
.top { height: 62px; flex: none; display: flex; align-items: center; gap: 14px; padding: 0 28px 0 16px; background: var(--surface); border-bottom: 1px solid var(--border); }
.fold { color: var(--text-2); font-size: 16px; }
.crumb { display: flex; align-items: center; gap: 10px; font-size: 16px; font-weight: 600; }
.crumb .dot { width: 8px; height: 22px; border-radius: 4px; background: var(--mod); }
.search { width: 300px; background: var(--surface-2); }
.content { flex: 1; overflow: auto; }
.search-list { width: 340px; max-height: 360px; overflow: auto; }
.search-item { padding: 10px 12px; border-radius: 10px; cursor: pointer; }
.search-item:hover { background: var(--chat-soft); }
</style>
