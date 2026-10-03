import { createRouter, createWebHistory } from 'vue-router'
import { useAuthStore } from '@/stores/auth'

/**
 * 路由：遵循产品方案的统一入口 /login、/app、/p/{poolId}、/w/{workspaceId}、/w/{workspaceId}/s/{sessionId}。
 * meta.mod 决定页面主题色（Pool 靛蓝 / Workspace 青绿 / 共享空间 琥珀 …），meta.admin 表示仅平台管理员可见。
 */
const routes = [
  { path: '/login', name: 'login', component: () => import('@/views/LoginView.vue'), meta: { title: '登录', public: true } },
  { path: '/', redirect: '/app' },
  {
    path: '/',
    component: () => import('@/layouts/AppLayout.vue'),
    children: [
      { path: 'app', name: 'home', component: () => import('@/views/HomeView.vue'), meta: { title: '我的工作台', mod: 'pool' } },
      { path: 'pools', name: 'pools', component: () => import('@/views/pools/PoolsView.vue'), meta: { title: 'Pool 管理', mod: 'pool' } },
      { path: 'p/:id', name: 'pool', component: () => import('@/views/pools/PoolDetailView.vue'), meta: { title: 'Pool 详情', mod: 'pool' } },
      { path: 'workspaces', name: 'workspaces', component: () => import('@/views/workspaces/WorkspacesView.vue'), meta: { title: '工作空间', mod: 'ws' } },
      { path: 'workspaces/:id', name: 'workspace', component: () => import('@/views/workspaces/WorkspaceDetailView.vue'), meta: { title: 'Workspace 详情', mod: 'ws' } },
      { path: 'shared-spaces', name: 'spaces', component: () => import('@/views/spaces/SharedSpacesView.vue'), meta: { title: '共享空间', mod: 'space' } },
      { path: 'shared-spaces/:id', name: 'space', component: () => import('@/views/spaces/SharedSpaceDetailView.vue'), meta: { title: '共享空间详情', mod: 'space' } },
      { path: 'usage', name: 'usage', component: () => import('@/views/UsageView.vue'), meta: { title: '用量统计', mod: 'usage' } },
      { path: 'tokens', name: 'tokens', component: () => import('@/views/TokensView.vue'), meta: { title: 'API Token', mod: 'token' } },
      { path: 'audit', name: 'audit', component: () => import('@/views/AuditView.vue'), meta: { title: '审计日志', mod: 'audit' } },
      { path: 'profile', name: 'profile', component: () => import('@/views/ProfileView.vue'), meta: { title: '个人设置', mod: 'admin' } },
      { path: 'users', name: 'users', component: () => import('@/views/admin/UsersView.vue'), meta: { title: '用户管理', mod: 'admin', admin: true } },
      { path: 'runtimes', name: 'runtimes', component: () => import('@/views/admin/RuntimesView.vue'), meta: { title: '运行环境', mod: 'runtime', admin: true } },
      { path: 'platform-config', name: 'platform-config', component: () => import('@/views/admin/PlatformConfigView.vue'), meta: { title: '平台配置', mod: 'config', admin: true } },
      { path: 'system', name: 'system', component: () => import('@/views/admin/SystemView.vue'), meta: { title: '系统信息', mod: 'admin', admin: true } },
      { path: 'w/:id', name: 'workbench', component: () => import('@/views/workbench/WorkbenchView.vue'), meta: { title: 'DSH 工作台', mod: 'chat', compact: true } },
      { path: 'w/:id/s/:sid', name: 'workbench-session', component: () => import('@/views/workbench/WorkbenchView.vue'), meta: { title: 'DSH 工作台', mod: 'chat', compact: true } }
    ]
  },
  { path: '/:pathMatch(.*)*', redirect: '/app' }
]

const router = createRouter({ history: createWebHistory(), routes })

router.beforeEach(async (to) => {
  const auth = useAuthStore()
  if (!auth.loaded) await auth.fetchMe()
  if (!to.meta.public && !auth.user) return { name: 'login', query: { redirect: to.fullPath } }
  if (to.name === 'login' && auth.user) return { name: 'home' }
  if (to.meta.admin && !auth.isAdmin) return { name: 'home' }
  document.title = `${(to.meta.title as string) || 'DSH'} · DSH 平台`
})

export default router
