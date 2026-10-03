import dayjs from 'dayjs'

/** 本地时间格式化（后端时间均为本地时间）。 */
export const fmtTime = (v?: string | null) => (v ? dayjs(v).format('YYYY-MM-DD HH:mm:ss') : '—')
/** 短时间：当天显示时分，否则显示月日。 */
export const fmtShort = (v?: string | null) => {
  if (!v) return '—'
  const d = dayjs(v)
  return d.isSame(dayjs(), 'day') ? d.format('HH:mm') : d.format('MM-DD HH:mm')
}
/** 相对时间。 */
export const fmtAgo = (v?: string | null) => {
  if (!v) return '从未'
  const s = dayjs().diff(dayjs(v), 'second')
  if (s < 60) return '刚刚'
  if (s < 3600) return `${Math.floor(s / 60)} 分钟前`
  if (s < 86400) return `${Math.floor(s / 3600)} 小时前`
  if (s < 86400 * 30) return `${Math.floor(s / 86400)} 天前`
  return dayjs(v).format('YYYY-MM-DD')
}
/** 字节数格式化。 */
export const fmtBytes = (n?: number | null) => {
  if (!n) return '0 B'
  const u = ['B', 'KB', 'MB', 'GB', 'TB']
  let i = 0; let v = n
  while (v >= 1024 && i < u.length - 1) { v /= 1024; i++ }
  return `${v >= 100 || i === 0 ? Math.round(v) : v.toFixed(1)} ${u[i]}`
}
/** 数字千分位。 */
export const fmtNum = (n?: number | null) => (n ?? 0).toLocaleString('zh-CN')
/** 紧凑数字：12.3k / 4.5M。 */
export const fmtCompact = (n?: number | null) => {
  const v = n ?? 0
  if (v >= 1e6) return (v / 1e6).toFixed(1) + 'M'
  if (v >= 1e3) return (v / 1e3).toFixed(1) + 'k'
  return String(v)
}
/** 复制到剪贴板。 */
export async function copyText(text: string) {
  try { await navigator.clipboard.writeText(text) } catch {
    const ta = document.createElement('textarea'); ta.value = text; document.body.appendChild(ta); ta.select(); document.execCommand('copy'); ta.remove()
  }
}

/** 角色 / 权限 / 状态的中文文案与配色。 */
export const roleText: Record<string, string> = { Owner: '所有者', Admin: '管理员', Member: '成员', Viewer: '只读', RO: '只读 RO', RW: '读写 RW', MANAGE: '管理 MANAGE' }
export const roleColor: Record<string, string> = { Owner: 'pool', Admin: 'pool', Member: 'ws', Viewer: '', RO: 'warn', RW: 'ok', MANAGE: 'audit' }
export const runtimeText: Record<string, string> = { STOPPED: '已停止', STARTING: '启动中', RUNNING: '运行中', IDLE: '空闲', STOPPING: '停止中', ERROR: '错误', MAINTENANCE: '维护中' }
export const runtimeColor: Record<string, string> = { STOPPED: '', STARTING: 'warn', RUNNING: 'ok', IDLE: 'ok', STOPPING: 'warn', ERROR: 'danger', MAINTENANCE: 'warn' }
/** 权限来源的中文说明。 */
export const sourceText: Record<string, string> = {
  'platform-admin': '平台管理员', direct: '直属成员', 'pool-direct': '所属 Pool 直接授权', 'pool-inherited': '祖先 Pool 向下继承', 'workspace-member': 'Workspace 显式成员',
  'explicit-deny': '显式拒绝', 'user-grant': '用户级授权', 'owner-pool-admin': '所属 Pool 管理员', creator: '创建人', 'ancestor-admin': '祖先 Pool 管理员（委托管理）',
  'descendant-member': '子 Pool 成员（可见祖先）', none: '无权限', 'not-granted': '未授权', 'token-scope': 'Token 范围限制'
}
export const sourceColor = (s?: string) => (s === 'explicit-deny' ? 'danger' : s === 'pool-inherited' ? 'space' : s === 'none' || s === 'not-granted' ? '' : 'pool')
