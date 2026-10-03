/** 把后端返回的扁平 Pool 节点（含 parentPoolId）组装为树。 */
export interface PoolTreeNode { key: string; value: string; title: string; raw: any; children: PoolTreeNode[] }

export function buildPoolTree(nodes: any[]): PoolTreeNode[] {
  const map = new Map<string, PoolTreeNode>()
  nodes.forEach((n) => map.set(n.id, { key: n.id, value: n.id, title: n.name, raw: n, children: [] }))
  const roots: PoolTreeNode[] = []
  nodes.forEach((n) => {
    const node = map.get(n.id)!
    const parent = n.parentPoolId ? map.get(n.parentPoolId) : null
    if (parent) parent.children.push(node); else roots.push(node)
  })
  return roots
}

/** 展开树中所有节点的 key（默认全部展开）。 */
export function allKeys(tree: PoolTreeNode[]): string[] {
  return tree.flatMap((n) => [n.key, ...allKeys(n.children)])
}

/** 在树中收集某节点及其全部后代的 ID（用于“移动到”时排除自身与后代）。 */
export function descendantIds(nodes: any[], id: string): string[] {
  const out = [id]
  nodes.filter((n) => n.parentPoolId === id).forEach((c) => out.push(...descendantIds(nodes, c.id)))
  return out
}
