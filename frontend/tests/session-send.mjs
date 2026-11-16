// Exercise the actual API module without a browser or Playwright.
import assert from 'node:assert/strict'
import fs from 'node:fs'
import vm from 'node:vm'
import { webcrypto } from 'node:crypto'
import ts from 'typescript'

const source = fs.readFileSync(new URL('../src/api/index.ts', import.meta.url), 'utf8')
const compiled = ts.transpileModule(source, { compilerOptions: {
  target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS
} }).outputText
const idPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/
for (const [environment, crypto] of [
  ['HTTPS/localhost', webcrypto],
  ['LAN HTTP: randomUUID unavailable', { getRandomValues: values => webcrypto.getRandomValues(values) }]
]) {
  const requests = []
  const context = { exports: {}, crypto, Uint8Array, URLSearchParams, require(path) {
    assert.equal(path, './http')
    return { http: { post(url, body) {
      requests.push({ url, body })
      return Promise.resolve({ data: { runId: 'run-test' } })
    } }, unwrap: promise => promise.then(response => response.data) }
  } }
  vm.runInNewContext(compiled, context)
  for (let i = 0; i < 200; i++) {
    const result = await context.exports.sessionApi.send('session-test', '你好', 'model-test', ['own-project'])
    assert.equal(result.runId, 'run-test')
  }
  assert.equal(requests.length, 200, 'the send action must reach the HTTP transport')
  assert.equal(new Set(requests.map(r => r.body.requestId)).size, 200)
  for (const request of requests) {
    assert.equal(request.url, '/sessions/session-test/messages')
    assert.equal(request.body.content, '你好')
    assert.equal(request.body.model, 'model-test')
    assert.deepEqual(Array.from(request.body.projectIds), ['own-project'])
    assert.match(request.body.requestId, idPattern)
  }
  console.log(`PASS: actual session send reaches POST with distinct UUIDs and preserved content/model/projects (${environment})`)
}
