export type ProjectReference = { id: string; name: string; directory: string; description?: string | null }
export type MentionQuery = { start: number; end: number; query: string }

export function projectQuery(text: string, caret: number): MentionQuery | null {
  const prefix = text.slice(0, caret)
  const match = /(?:^|\s)@([^\s@]*)$/u.exec(prefix)
  return match ? { start: prefix.length - match[1].length - 1, end: caret, query: match[1] } : null
}

export function filterProjects(rows: ProjectReference[], query: string, selected: ProjectReference[]) {
  const needle = query.toLocaleLowerCase()
  const selectedIds = new Set(selected.map(p => p.id))
  return rows.filter(p => !selectedIds.has(p.id) && `${p.name} ${p.directory} ${p.description || ''}`.toLocaleLowerCase().includes(needle)).slice(0, 8)
}

export function removeQuery(text: string, query: MentionQuery) { return text.slice(0, query.start) + text.slice(query.end) }
