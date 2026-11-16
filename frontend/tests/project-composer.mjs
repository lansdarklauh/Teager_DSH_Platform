// Execute the actual Vue component setup with controlled API responses, without a browser.
import assert from 'node:assert/strict'
import fs from 'node:fs'
import { parse, compileScript } from '@vue/compiler-sfc'
import ts from 'typescript'
import { reactive, nextTick, effectScope } from 'vue'

const transpile = source => ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ES2022 } }).outputText
const dataUrl = source => 'data:text/javascript;base64,' + Buffer.from(source).toString('base64')
const helpers = dataUrl(transpile(fs.readFileSync(new URL('../src/utils/projectMentions.ts', import.meta.url), 'utf8')))
const source = fs.readFileSync(new URL('../src/components/ProjectMentionInput.vue', import.meta.url), 'utf8')
const compiled = compileScript(parse(source).descriptor, { id: 'composer-test' }).content
const vueUrl = new URL('../node_modules/vue/index.mjs', import.meta.url).href
const code = transpile(compiled)
  .replaceAll("from 'vue'", `from '${vueUrl}'`)
  .replace(/import \{ message \} from ['"]ant-design-vue['"];?/, 'const message = globalThis.__composerFixture.message;')
  .replace(/import \{ projectApi \} from ['"]@\/api['"];?/, 'const projectApi = globalThis.__composerFixture.api;')
  .replaceAll("from '@/utils/projectMentions'", `from '${helpers}'`)
const pending = [], notices = [], emitted = []
globalThis.__composerFixture = {
  api: { list: workspace => new Promise((resolve, reject) => pending.push({ workspace, resolve, reject })) },
  message: { info: text => notices.push(text) }
}
const component = (await import(dataUrl(code))).default
const props = reactive({ modelValue: '', projects: [], workspaceId: 'own', disabled: false })
const scope = effectScope()
const state = scope.run(() => component.setup(props, { expose() {}, emit(name, value) {
  emitted.push({ name, value })
  if (name === 'update:modelValue') props.modelValue = value
  if (name === 'update:projects') props.projects = value
} }))
const projects = [{ id: 'a', name: '新闻项目', directory: 'projects/a' }, { id: 'b', name: 'API 项目', directory: 'projects/b' }]
const flush = async () => { await Promise.resolve(); await nextTick() }
function type(text, caret = text.length) { props.modelValue = text; state.inspect({ target: { value: text, selectionStart: caret } }) }
function key(key, extra = {}) { const event = { key, shiftKey: false, isComposing: false, keyCode: 0, prevented: false, preventDefault() { this.prevented = true }, ...extra }; state.keydown(event); return event }
try {
  assert.equal(pending[0].workspace, 'own')
  pending.shift().resolve(projects); await flush()
  type('检查 @')
  assert.equal(state.candidates.value.length, 2)
  assert.equal(key('ArrowDown').prevented, true)
  assert.equal(state.active.value, 1)
  key('Enter'); await flush()
  assert.equal(props.projects[0].id, 'b')
  assert.equal(props.modelValue, '检查 ')
  assert.equal(emitted.filter(e => e.name === 'send').length, 0, 'choosing a project must not send')
  key('Enter')
  assert.equal(emitted.filter(e => e.name === 'send').length, 1)
  key('Enter', { shiftKey: true }); key('Enter', { isComposing: true }); key('Enter', { keyCode: 229 })
  assert.equal(emitted.filter(e => e.name === 'send').length, 1, 'newline and IME must not send')
  type('检查 @新闻')
  assert.equal(state.candidates.value[0].id, 'a')
  key('Escape'); assert.equal(state.query.value, null)
  type('@不存在'); key('Enter')
  assert.equal(emitted.filter(e => e.name === 'send').length, 1, 'an empty picker must not send')
  const count = emitted.length
  state.add(projects[1]); assert.equal(emitted.length, count, 'duplicate reference ignored')
  props.projects = Array.from({ length: 8 }, (_, i) => ({ id: String(i), name: String(i), directory: `projects/${i}` }))
  state.add(projects[0]); assert.equal(notices.length, 1); assert.equal(props.projects.length, 8)
  props.disabled = true; state.add(projects[0]); key('Enter')
  assert.equal(props.projects.length, 8)
  assert.equal(emitted.filter(e => e.name === 'send').length, 1)
  props.workspaceId = 'older'; await nextTick()
  assert.equal(state.rows.value.length, 0); assert.equal(state.query.value, null)
  props.workspaceId = 'latest'; await nextTick()
  const older = pending.shift(), latest = pending.shift()
  latest.resolve([{ ...projects[0], id: 'latest' }]); await flush()
  older.resolve(projects); await flush()
  assert.equal(state.rows.value[0].id, 'latest', 'stale workspace response ignored')
  const refresh = state.refresh(); pending.shift().reject(new Error('offline')); await refresh
  assert.equal(state.rows.value.length, 0); assert.ok(state.error.value)
  const retry = state.refresh(); pending.shift().resolve(projects); await retry
  assert.equal(state.error.value, ''); assert.equal(state.rows.value.length, 2)
  console.log('PASS: actual composer keyboard selection/send, newline/IME, Escape, empty search, duplicate/limit/disabled guards, workspace races and retry')
} finally { scope.stop(); delete globalThis.__composerFixture }
