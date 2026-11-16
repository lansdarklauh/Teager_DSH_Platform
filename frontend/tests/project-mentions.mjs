import assert from 'node:assert/strict'
import fs from 'node:fs'
import ts from 'typescript'
const source = fs.readFileSync(new URL('../src/utils/projectMentions.ts', import.meta.url), 'utf8')
const code = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ES2022 } }).outputText
const { projectQuery, filterProjects, removeQuery } = await import('data:text/javascript;base64,' + Buffer.from(code).toString('base64'))
assert.deepEqual(projectQuery('请检查 @新闻', 7), { start: 4, end: 7, query: '新闻' })
assert.equal(projectQuery('mail@example.com', 16), null)
assert.equal(projectQuery('https://example/@name', 21), null)
assert.deepEqual(projectQuery('@', 1), { start: 0, end: 1, query: '' })
const query = projectQuery('分析 @项目 后面的任务', 6)
assert.equal(removeQuery('分析 @项目 后面的任务', query), '分析  后面的任务')
const projects = [{ id: 'a', name: '中文项目', directory: 'projects/a', description: '新闻' }, { id: 'b', name: 'API', directory: 'projects/b' }]
assert.deepEqual(filterProjects(projects, '新闻', []).map(p => p.id), ['a'])
assert.deepEqual(filterProjects(projects, 'api', []).map(p => p.id), ['b'])
assert.deepEqual(filterProjects(projects, '', [projects[0]]).map(p => p.id), ['b'])
console.log('PASS: project @ query parsing, caret replacement, Chinese search, case-insensitive search and duplicate exclusion')
