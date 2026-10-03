/** 审计操作的中文名称与所属类别（用于筛选与配色）。 */
export const auditText: Record<string, string> = {
  'auth.login': '登录', 'auth.logout': '退出登录', 'auth.password.change': '修改密码',
  'user.create': '创建用户', 'user.update': '更新用户', 'user.password.reset': '重置密码',
  'pool.create': '创建 Pool', 'pool.update': '更新 Pool', 'pool.delete': '删除 Pool', 'pool.move': '移动 Pool', 'pool.member.add': '添加 Pool 成员', 'pool.member.role': '调整成员角色',
  'pool.member.remove': '移除 Pool 成员', 'pool.policy.update': '更新 Pool 策略',
  'workspace.create': '创建 Workspace', 'workspace.update': '更新 Workspace', 'workspace.delete': '删除 Workspace', 'workspace.restore': '恢复 Workspace', 'workspace.clone': '克隆 Workspace',
  'workspace.member.set': '设置 Workspace 成员', 'workspace.member.remove': '移除 Workspace 成员', 'workspace.mount.add': '挂载共享空间', 'workspace.mount.update': '更新挂载', 'workspace.mount.remove': '取消挂载',
  'workspace.snapshot.create': '创建快照', 'workspace.snapshot.restore': '恢复快照', 'workspace.file.write': '写入文件', 'workspace.file.delete': '删除文件', 'workspace.file.rename': '重命名文件',
  'workspace.file.move': '移动文件', 'workspace.file.mkdir': '创建目录',
  'sharedspace.create': '创建共享空间', 'sharedspace.update': '更新共享空间', 'sharedspace.delete': '删除共享空间', 'sharedspace.grant.add': '授权共享空间', 'sharedspace.grant.remove': '撤销授权',
  'sharedspace.read': '读取共享文件', 'sharedspace.write': '写入共享文件', 'sharedspace.mkdir': '创建共享目录', 'sharedspace.rename': '重命名共享文件', 'sharedspace.move': '移动共享文件', 'sharedspace.ro_violation': '只读共享空间被修改',
  'session.create': '创建会话', 'session.update': '更新会话', 'session.delete': '删除会话', 'session.duplicate': '复制会话', 'session.export': '导出会话', 'session.cancel': '取消运行', 'session.run': 'Agent 运行',
  'runtime.start': '启动 Runtime', 'runtime.stop': '停止 Runtime', 'runtime.restart': '重启 Runtime', 'runtime.maintenance-on': '进入维护', 'runtime.maintenance-off': '退出维护',
  'runtime.node.create': '登记运行节点', 'runtime.node.update': '更新运行节点',
  'config.upsert': '保存配置', 'config.delete': '删除配置', 'secret.upsert': '设置密钥', 'secret.delete': '删除密钥',
  'token.create': '创建 Token', 'token.revoke': '吊销 Token', 'terminal.open': '打开终端', 'terminal.close': '关闭终端'
}
export const auditCategories = [
  { value: 'auth', label: '登录认证' }, { value: 'pool', label: 'Pool' }, { value: 'workspace', label: 'Workspace' }, { value: 'sharedspace', label: '共享空间' },
  { value: 'session', label: '会话 / Agent' }, { value: 'runtime', label: 'Runtime' }, { value: 'config', label: '配置' }, { value: 'secret', label: '密钥' }, { value: 'token', label: 'Token' },
  { value: 'user', label: '用户' }, { value: 'terminal', label: '终端' }
]
/** 操作的中文名（未收录则原样显示）。 */
export const actionName = (a: string) => auditText[a] || a
/** 操作类别对应的标签色。 */
export const actionColor = (a: string) => {
  const c = a.split('.')[0]
  return ({ auth: 'chat', pool: 'pool', workspace: 'ws', sharedspace: 'space', session: 'chat', runtime: 'runtime', config: 'token', secret: 'audit', token: 'token', user: '', terminal: 'audit' } as Record<string, string>)[c] || ''
}
