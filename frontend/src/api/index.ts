import { http, unwrap } from './http'

type Dict = Record<string, any>
const q = (o: Dict = {}) => ({ params: Object.fromEntries(Object.entries(o).filter(([, v]) => v !== undefined && v !== null && v !== '')) })

/** 认证 */
export const authApi = {
  login: (username: string, password: string) => unwrap(http.post('/auth/login', { username, password }, { silent: true })),
  logout: () => unwrap(http.post('/auth/logout')),
  me: () => unwrap(http.get('/auth/me', { silent: true })),
  changePassword: (oldPassword: string, newPassword: string) => unwrap(http.post('/auth/change-password', { oldPassword, newPassword }))
}

/** 元信息 / 用户 */
export const metaApi = { get: () => unwrap(http.get('/meta')) }
export const userApi = {
  list: (keyword?: string) => unwrap(http.get('/users', q({ q: keyword }))),
  lookup: (keyword?: string) => unwrap(http.get('/users/lookup', q({ q: keyword }))),
  create: (b: Dict) => unwrap(http.post('/users', b)),
  update: (id: string, b: Dict) => unwrap(http.patch(`/users/${id}`, b)),
  resetPassword: (id: string, newPassword: string) => unwrap(http.post(`/users/${id}/reset-password`, { newPassword }))
}

/** Pool */
export const poolApi = {
  tree: () => unwrap(http.get('/pools')),
  get: (id: string) => unwrap(http.get(`/pools/${id}`)),
  create: (b: Dict) => unwrap(http.post('/pools', b)),
  update: (id: string, b: Dict) => unwrap(http.patch(`/pools/${id}`, b)),
  remove: (id: string, p: Dict) => unwrap(http.delete(`/pools/${id}`, q(p))),
  members: (id: string) => unwrap(http.get(`/pools/${id}/members`)),
  addMember: (id: string, userId: string, role: string) => unwrap(http.post(`/pools/${id}/members`, { userId, role })),
  setRole: (id: string, userId: string, role: string) => unwrap(http.patch(`/pools/${id}/members/${userId}`, { role })),
  removeMember: (id: string, userId: string) => unwrap(http.delete(`/pools/${id}/members/${userId}`)),
  movePreview: (id: string, newParentId?: string) => unwrap(http.get(`/pools/${id}/move-preview`, q({ newParentId }))),
  move: (id: string, newParentId?: string | null) => unwrap(http.post(`/pools/${id}/move`, { newParentId })),
  workspaces: (id: string, p: Dict = {}) => unwrap(http.get(`/pools/${id}/workspaces`, q(p))),
  sharedSpaces: (id: string) => unwrap(http.get(`/pools/${id}/shared-spaces`)),
  policy: (id: string) => unwrap(http.get(`/pools/${id}/policy`)),
  savePolicy: (id: string, b: Dict) => unwrap(http.put(`/pools/${id}/policy`, b)),
  effectiveConfig: (id: string) => unwrap(http.get(`/pools/${id}/effective-config`)),
  runtime: (id: string) => unwrap(http.get(`/pools/${id}/runtime`)),
  runtimeHealth: (id: string) => unwrap(http.get(`/pools/${id}/runtime/health`)),
  runtimeAction: (id: string, action: 'start' | 'stop' | 'restart') => unwrap(http.post(`/pools/${id}/runtime/${action}`)),
  maintenance: (id: string, enabled: boolean) => unwrap(http.post(`/pools/${id}/runtime/maintenance`, { enabled }))
}

/** Workspace */
export const wsApi = {
  list: (p: Dict = {}) => unwrap(http.get('/workspaces', q(p))),
  get: (id: string) => unwrap(http.get(`/workspaces/${id}`)),
  create: (b: Dict) => unwrap(http.post('/workspaces', b)),
  update: (id: string, b: Dict) => unwrap(http.patch(`/workspaces/${id}`, b)),
  remove: (id: string, mode: string) => unwrap(http.delete(`/workspaces/${id}`, q({ mode }))),
  restore: (id: string) => unwrap(http.post(`/workspaces/${id}/restore`)),
  clone: (id: string, name?: string) => unwrap(http.post(`/workspaces/${id}/clone`, { name })),
  members: (id: string) => unwrap(http.get(`/workspaces/${id}/members`)),
  setMember: (id: string, b: Dict) => unwrap(http.post(`/workspaces/${id}/members`, b)),
  removeMember: (id: string, userId: string) => unwrap(http.delete(`/workspaces/${id}/members/${userId}`)),
  mounts: (id: string) => unwrap(http.get(`/workspaces/${id}/mounts`)),
  addMount: (id: string, b: Dict) => unwrap(http.post(`/workspaces/${id}/mounts`, b)),
  updateMount: (id: string, mountId: string, b: Dict) => unwrap(http.patch(`/workspaces/${id}/mounts/${mountId}`, b)),
  removeMount: (id: string, mountId: string) => unwrap(http.delete(`/workspaces/${id}/mounts/${mountId}`)),
  explain: (id: string, sharedSpaceId?: string) => unwrap(http.get(`/workspaces/${id}/permissions/explain`, q({ sharedSpaceId }))),
  effectiveConfig: (id: string) => unwrap(http.get(`/workspaces/${id}/effective-config`)),
  snapshots: (id: string) => unwrap(http.get(`/workspaces/${id}/snapshots`)),
  createSnapshot: (id: string, b: Dict) => unwrap(http.post(`/workspaces/${id}/snapshots`, b)),
  restoreSnapshot: (id: string, sid: string) => unwrap(http.post(`/workspaces/${id}/snapshots/${sid}/restore`)),
  deleteSnapshot: (id: string, sid: string) => unwrap(http.delete(`/workspaces/${id}/snapshots/${sid}`)),
  snapshotUrl: (id: string, sid: string) => `/api/v1/workspaces/${id}/snapshots/${sid}/download`
}

/** 文件（Workspace 私有 / 挂载共享空间 / 直接访问共享空间共用同一组方法，由 base 决定前缀） */
export interface FileTarget { base: string; area?: string }
export const fileApi = {
  list: (t: FileTarget, path = '') => unwrap(http.get(`${t.base}/files`, q({ area: t.area, path }))),
  read: (t: FileTarget, path: string) => unwrap(http.get(`${t.base}/files/content`, q({ area: t.area, path }))),
  write: (t: FileTarget, path: string, content: string) => unwrap(http.put(`${t.base}/files/content`, { path, content }, q({ area: t.area }))),
  mkdir: (t: FileTarget, path: string) => unwrap(http.post(`${t.base}/files/mkdir`, { path }, q({ area: t.area }))),
  remove: (t: FileTarget, path: string) => unwrap(http.delete(`${t.base}/files`, q({ area: t.area, path }))),
  move: (t: FileTarget, from: string, to: string) => unwrap(http.post(`${t.base}/files/move`, { from, to }, q({ area: t.area }))),
  upload: (t: FileTarget, path: string, files: File[]) => {
    const fd = new FormData(); files.forEach((f) => fd.append('files', f))
    return unwrap(http.post(`${t.base}/files/upload`, fd, { ...q({ area: t.area, path }), timeout: 600000 }))
  },
  versions: (t: FileTarget, path: string) => unwrap(http.get(`${t.base}/files/versions`, q({ area: t.area, path }))),
  versionContent: (t: FileTarget, path: string, version: string) => unwrap(http.get(`${t.base}/files/versions/content`, q({ area: t.area, path, version }))),
  restoreVersion: (t: FileTarget, path: string, version: string) => unwrap(http.post(`${t.base}/files/versions/restore`, { path, version }, q({ area: t.area }))),
  downloadUrl: (t: FileTarget, path: string) => `/api/v1${t.base}/files/download?${new URLSearchParams({ ...(t.area ? { area: t.area } : {}), path }).toString()}`
}

/** 共享空间 */
export const spaceApi = {
  list: (p: Dict = {}) => unwrap(http.get('/shared-spaces', q(p))),
  get: (id: string) => unwrap(http.get(`/shared-spaces/${id}`)),
  create: (b: Dict) => unwrap(http.post('/shared-spaces', b)),
  update: (id: string, b: Dict) => unwrap(http.patch(`/shared-spaces/${id}`, b)),
  remove: (id: string, permanent = false) => unwrap(http.delete(`/shared-spaces/${id}`, q({ permanent: permanent || undefined }))),
  grants: (id: string) => unwrap(http.get(`/shared-spaces/${id}/grants`)),
  addGrant: (id: string, b: Dict) => unwrap(http.post(`/shared-spaces/${id}/grants`, b)),
  removeGrant: (id: string, gid: string) => unwrap(http.delete(`/shared-spaces/${id}/grants/${gid}`)),
  mounts: (id: string) => unwrap(http.get(`/shared-spaces/${id}/mounts`)),
  explain: (id: string) => unwrap(http.get(`/shared-spaces/${id}/permissions/explain`))
}

/** 会话 */
export const sessionApi = {
  list: (workspaceId: string, p: Dict = {}) => unwrap(http.get('/sessions', q({ workspaceId, ...p }))),
  search: (keyword: string) => unwrap(http.get('/sessions/search', q({ q: keyword }))),
  create: (b: Dict) => unwrap(http.post('/sessions', b)),
  get: (id: string) => unwrap(http.get(`/sessions/${id}`)),
  update: (id: string, b: Dict) => unwrap(http.patch(`/sessions/${id}`, b)),
  remove: (id: string) => unwrap(http.delete(`/sessions/${id}`)),
  duplicate: (id: string) => unwrap(http.post(`/sessions/${id}/duplicate`)),
  messages: (id: string) => unwrap(http.get(`/sessions/${id}/messages`)),
  send: (id: string, content: string, model?: string) => unwrap(http.post(`/sessions/${id}/messages`, { content, model })),
  cancel: (id: string) => unwrap(http.post(`/sessions/${id}/cancel`)),
  runs: (id: string) => unwrap(http.get(`/sessions/${id}/runs`)),
  history: (id: string, runId?: string, after?: number) => unwrap(http.get(`/sessions/${id}/events/history`, q({ runId, after }))),
  exportUrl: (id: string, format: 'md' | 'json') => `/api/v1/sessions/${id}/export?format=${format}`,
  eventsUrl: (id: string, after: number) => `/api/v1/sessions/${id}/events?after=${after}`
}

/** 配置 / 密钥 */
export const configApi = {
  list: (scope: string, scopeId?: string, kind?: string) => unwrap(http.get('/configs', q({ scope, scopeId, kind }))),
  upsert: (b: Dict) => unwrap(http.put('/configs', b)),
  remove: (id: string) => unwrap(http.delete(`/configs/${id}`))
}
export const secretApi = {
  list: (scope: string, scopeId?: string) => unwrap(http.get('/secrets', q({ scope, scopeId }))),
  upsert: (b: Dict) => unwrap(http.put('/secrets', b)),
  remove: (id: string) => unwrap(http.delete(`/secrets/${id}`))
}

/** Token / 审计 / 用量 / 管理 */
export const tokenApi = {
  list: (all = false) => unwrap(http.get('/tokens', q({ all: all || undefined }))),
  create: (b: Dict) => unwrap(http.post('/tokens', b)),
  revoke: (id: string) => unwrap(http.delete(`/tokens/${id}`))
}
export const auditApi = { query: (p: Dict) => unwrap(http.get('/audit', q(p))) }
export const usageApi = { summary: (p: Dict) => unwrap(http.get('/usage/summary', q(p))) }
export const adminApi = {
  runtimes: () => unwrap(http.get('/runtimes')),
  nodes: () => unwrap(http.get('/runtime-nodes')),
  createNode: (b: Dict) => unwrap(http.post('/runtime-nodes', b)),
  updateNode: (id: string, b: Dict) => unwrap(http.patch(`/runtime-nodes/${id}`, b)),
  systemInfo: () => unwrap(http.get('/system/info'))
}
