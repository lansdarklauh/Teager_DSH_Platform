// Offline route contract: execute the actual TypeScript guard without a browser.
import assert from 'node:assert/strict'
import fs from 'node:fs'
import ts from 'typescript'
const source = fs.readFileSync(new URL('../src/router/index.ts', import.meta.url), 'utf8')
const tree = ts.createSourceFile('router.ts', source, ts.ScriptTarget.Latest, true)
let arrow
function visit(node) {
  if (ts.isCallExpression(node) && node.expression.getText(tree) === 'router.beforeEach') arrow = node.arguments[0].getText(tree)
  ts.forEachChild(node, visit)
}
visit(tree)
assert.ok(arrow)
const code = ts.transpileModule(`const guard = ${arrow}`, { compilerOptions: { target: ts.ScriptTarget.ES2022 } }).outputText
const auth = { loaded: true, user: { role: 'User', defaultWorkspaceId: 'personal-fixture' }, isAdmin: false, landingPath: '/w/personal-fixture' }
const guard = new Function('useAuthStore', 'document', code + '\nreturn guard')(() => auth, {})
const route = (name, extra = {}) => ({ name, meta: {}, params: {}, fullPath: '/' + name, ...extra })
for (const entry of [route('home'), route('login'), route('users', { meta: { admin: true } }), route('workbench', { params: { id: 'foreign' } })]) {
  assert.equal(await guard(entry), '/w/personal-fixture')
}
assert.equal(await guard(route('workbench', { params: { id: 'personal-fixture' } })), undefined)
assert.equal(await guard(route('pool')), '/accessible-pools')
assert.equal(await guard(route('tokens')), undefined)
auth.user = null
assert.deepEqual(await guard(route('workbench')), { name: 'login', query: { redirect: '/workbench' } })
console.log('PASS: ordinary entry/admin/foreign routes lead to personal workbench; own workbench/API guide allowed; expired login returns to login')
