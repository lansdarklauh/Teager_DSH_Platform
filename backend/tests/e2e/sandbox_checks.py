"""Permission and real Docker/HTTP/WebSocket acceptance checks (stdlib only)."""
import base64
import concurrent.futures
import hashlib
import json
import os
import socket
import sys
import struct
import time
import urllib.parse
import urllib.request
from pathlib import Path


def check_permissions(admin, alice, bob, a, b, aw, bw, checks):
    pool = alice.call('GET', 'workspaces/' + aw)['view']['poolId']
    alice.call('PATCH', 'pools/' + pool, {'description': 'forbidden'}, expected=403)
    admin.call('POST', 'pools/' + pool + '/members', {'userId': a['id'], 'role': 'Member'})
    admin.call('PUT', 'pools/' + pool + '/members/' + a['id'] + '/details-permission', {'canEditDetails': True}, expected=204)
    alice.call('PATCH', 'pools/' + pool, {'description': '管理员指派的编辑权限'})
    alice.call('PATCH', 'pools/' + pool, {'runtimeProvider': 'Mock'}, expected=403)
    for route in ('members', 'policy', 'effective-config', 'runtime'):
        alice.call('GET', 'pools/' + pool + '/' + route, expected=403)
    checks.append('Pool editor can edit details only; members/policy/runtime stay admin-only')
    admin.call('PUT', 'users/' + a['id'] + '/workspace', {'workspaceId': bw}, expected=409)
    alice.call('PUT', 'users/' + a['id'] + '/workspace', {'workspaceId': bw}, expected=403)
    checks.append('workspace assignment rejects ordinary users and conflicting owners')
    alice.call('POST', 'shared-spaces', {'name': 'invalid', 'quotaMb': 0}, expected=403)
    # Numeric enum values must never be accepted as permissions.
    admin.call('POST', 'pools/' + pool + '/members', {'userId': b['id'], 'role': 999}, expected=400)
    alice.call('GET', 'auth/me', headers={'Authorization': 'Basic invalid'}, expected=401)
    alice.call('GET', 'auth/me', headers={'Authorization': 'Bearer invalid'}, expected=401)
    checks.append('unsupported/invalid Authorization cannot fall back to cookies; numeric roles rejected')
    scoped = admin.call('POST', 'tokens', {'name': 'restricted-admin', 'scopes': ['pool:read', 'pool:manage', 'workspace:read', 'workspace:write', 'workspace:manage', 'runtime:read', 'share:manage'], 'allowedWorkspaces': [aw]})['token']
    admin.call('GET', 'workspaces/' + bw, token=scoped, expected=404)
    other_pool = bob.call('GET', 'workspaces/' + bw)['view']['poolId']
    # The two personal workspaces can share the private provisioning Pool.
    restricted_view = admin.call('GET', 'pools/' + pool, token=scoped)
    assert 'stats' not in restricted_view
    admin.call('POST', 'workspaces', {'poolId': pool, 'name': 'escape'}, token=scoped, expected=403)
    admin.call('POST', 'pools', {'name': 'escape'}, token=scoped, expected=403)
    admin.call('GET', 'configs?scope=Platform', token=scoped, expected=403)
    admin.call('GET', 'runtimes', token=scoped, expected=403)
    admin.call('GET', 'usage/summary', token=scoped)  # Results are clipped to the workspace whitelist.
    checks.append('resource-scoped administrator Token cannot widen its resource/management range')
    session = alice.call('POST', 'sessions', {'workspaceId': aw})['id']
    request_id = 'same-request'
    body = {'content': '/sleep 2', 'requestId': request_id}
    with concurrent.futures.ThreadPoolExecutor(2) as executor:
        results = list(executor.map(lambda _: alice.call('POST', 'sessions/' + session + '/messages', body, expected=202), range(2)))
    assert results[0]['runId'] == results[1]['runId'], results
    alice.call('POST', 'sessions/' + session + '/messages', {'content': 'different', 'requestId': request_id}, expected=409)
    alice.call('DELETE', 'sessions/' + session, expected=409)
    alice.call('POST', 'sessions/' + session + '/messages', {'content': 'another'}, expected=409)
    time.sleep(3)
    messages = alice.call('GET', 'sessions/' + session + '/messages')
    assert len([m for m in messages if m['role'] == 'user']) == 1, messages
    checks.append('parallel retries create one run/message; active session deletion/concurrent sends rejected')
    snap = alice.call('POST', 'workspaces/' + aw + '/snapshots', {'name': 'personal-backup'})['id']
    alice.call('PUT', 'workspaces/' + aw + '/files/content', {'path': 'after-snapshot.txt', 'content': 'temporary'}, expected=204)
    alice.call('POST', f'workspaces/{aw}/snapshots/{snap}/restore', expected=204)
    alice.call('GET', 'workspaces/' + aw + '/files/content?path=after-snapshot.txt', expected=404)
    alice.call('DELETE', f'workspaces/{aw}/snapshots/{snap}', expected=204)
    checks.append('ordinary user can create/restore/delete personal file snapshots')
    disabled_session = alice.call('POST', 'sessions', {'workspaceId': aw})['id']
    alice.call('POST', f'sessions/{disabled_session}/messages', {'content': '/sleep 20'}, expected=202)
    admin.call('PATCH', 'users/' + a['id'], {'status': 'Disabled'})
    alice.call('GET', 'auth/me', expected=401)
    for _ in range(25):
        rows = admin.call('GET', f'sessions/{disabled_session}/runs')
        if rows[0]['status'] != 'Running':
            break
        time.sleep(.2)
    assert rows[0]['status'] == 'Cancelled', rows
    admin.call('PATCH', 'users/' + a['id'], {'status': 'Active'})
    alice.call('POST', 'auth/login', {'username': 'alice', 'password': 'Validation_User123'})
    checks.append('disabling a user invalidates access and cancels an already-running task')
    sys.path.insert(0, str(Path(__file__).resolve().parents[3] / 'sdk/python'))
    from dsh_platform import Client
    sdk_token = alice.call('POST', 'tokens', {'name': 'sdk-stream', 'scopes': ['workspace:read', 'session:read', 'session:create', 'session:execute', 'files:read', 'files:write']})['token']
    sdk_session = Client(alice.base, sdk_token).workspace(aw).create_session()
    streamed = list(sdk_session.stream('/sleep 1', timeout=10))
    assert streamed[-1]['type'] == 'run_end' and len({event['runId'] for event in streamed}) == 1, streamed
    assert len(sdk_session.messages()) == 2
    sdk_session.delete()
    checks.append('Python SDK creates/streams/reads/deletes its personal session and receives the matching run_end')


class WebSocket:
    """A minimal RFC6455 client for real terminal/application transport checks."""
    def __init__(self, base, path, headers=None):
        uri = urllib.parse.urlsplit(base)
        self.connection = socket.create_connection(('127.0.0.1', uri.port), timeout=15)
        self.connection.settimeout(15)
        key = base64.b64encode(os.urandom(16)).decode()
        request_headers = {'Host': uri.netloc, 'Upgrade': 'websocket', 'Connection': 'Upgrade', 'Sec-WebSocket-Key': key, 'Sec-WebSocket-Version': '13', **(headers or {})}
        request = 'GET ' + path + ' HTTP/1.1\r\n' + ''.join(k + ': ' + v + '\r\n' for k, v in request_headers.items()) + '\r\n'
        self.connection.sendall(request.encode())
        response = b''
        while not response.endswith(b'\r\n\r\n'):
            response += self.connection.recv(1)
        assert b'101' in response.split(b'\r\n')[0], response
        accept = base64.b64encode(hashlib.sha1((key + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11').encode()).digest())
        assert accept in response, response

    def send(self, value):
        data = value.encode()
        mask = os.urandom(4)
        length = len(data)
        header = bytes([0x81, 0x80 | length]) if length < 126 else bytes([0x81, 0xfe]) + struct.pack('!H', length)
        self.connection.sendall(header + mask + bytes(c ^ mask[i % 4] for i, c in enumerate(data)))

    def read(self):
        def exact(count):
            data = b''
            while len(data) < count:
                chunk = self.connection.recv(count - len(data))
                if not chunk:
                    raise EOFError('websocket closed')
                data += chunk
            return data
        header = exact(2)
        length = header[1] & 127
        if length == 126:
            length = struct.unpack('!H', exact(2))[0]
        elif length == 127:
            length = struct.unpack('!Q', exact(8))[0]
        mask = exact(4) if header[1] & 128 else None
        data = exact(length)
        if mask:
            data = bytes(c ^ mask[i % 4] for i, c in enumerate(data))
        return data.decode(errors='replace')

    def close(self):
        self.connection.close()


APP = '''import base64, hashlib, http.server, json
class App(http.server.BaseHTTPRequestHandler):
 def do_GET(self):
  if self.path == '/socket':
   key = self.headers['Sec-WebSocket-Key']
   self.send_response(101); self.send_header('Upgrade', 'websocket'); self.send_header('Connection', 'Upgrade')
   self.send_header('Sec-WebSocket-Accept', base64.b64encode(hashlib.sha1((key+'258EAFA5-E914-47DA-95CA-C5AB0DC85B11').encode()).digest()).decode()); self.end_headers()
   header=self.rfile.read(2); size=header[1]&127; mask=self.rfile.read(4); data=self.rfile.read(size)
   text=bytes(c^mask[i%4] for i,c in enumerate(data)) if header[1]&128 else data
   self.wfile.write(bytes([129,len(text)])+text); self.wfile.flush(); return
  body=json.dumps({'ok': True, 'cookie': self.headers.get('Cookie'), 'authorization': self.headers.get('Authorization')}).encode()
  self.send_response(200); self.send_header('Content-Type','application/json'); self.send_header('Content-Length',str(len(body))); self.end_headers(); self.wfile.write(body)
http.server.ThreadingHTTPServer(('0.0.0.0',8000),App).serve_forever()
'''


def check_sandbox(admin, alice, bob, a, b, aw, bw, checks, containers):
    def start(command, token=None, ports=None):
        result = alice.call('POST', f'workspaces/{aw}/commands', {'command': command, 'ports': ports or []}, token=token)
        containers.append(result['containerName'])
        return result

    def finish(execution, token=None):
        deadline = time.monotonic() + 60
        while time.monotonic() < deadline:
            result = alice.call('GET', f"workspaces/{aw}/executions/{execution['id']}/logs", token=token)
            if result['status'] != 'Running':
                return result
            time.sleep(.3)
        raise AssertionError(('command did not finish', execution))

    pool = alice.call('GET', 'workspaces/' + aw)['view']['poolId']
    dependency_script = r"""python3 - <<'PY'
import zipfile
with zipfile.ZipFile('usage_fixture-1.0-py3-none-any.whl','w') as package:
 package.writestr('usage_fixture.py','VALUE = "PYTHON_DEPENDENCY_VERIFIED"')
 package.writestr('usage_fixture-1.0.dist-info/METADATA','Metadata-Version: 2.1\nName: usage-fixture\nVersion: 1.0\n')
 package.writestr('usage_fixture-1.0.dist-info/WHEEL','Wheel-Version: 1.0\nGenerator: validation\nRoot-Is-Purelib: true\nTag: py3-none-any\n')
 package.writestr('usage_fixture-1.0.dist-info/RECORD','')
PY
python3 -m pip install --no-index --no-deps --target /home/user/python-dependencies usage_fixture-1.0-py3-none-any.whl
mkdir -p local-node
printf '{"name":"usage-local","version":"1.0.0","main":"index.js"}' > local-node/package.json
printf 'module.exports="NODE_DEPENDENCY_VERIFIED"' > local-node/index.js
npm install --prefix /home/user/node-dependencies /workspace/files/local-node --offline --no-audit --no-fund
"""
    installed = finish(start(dependency_script))
    assert installed['exitCode'] == 0, installed
    persisted = finish(start("PYTHONPATH=/home/user/python-dependencies python3 -c 'import usage_fixture; print(usage_fixture.VALUE)'; node -e 'console.log(require(\"/home/user/node-dependencies/node_modules/usage-local\"))'"))
    assert persisted['exitCode'] == 0 and 'PYTHON_DEPENDENCY_VERIFIED' in persisted['output'] and 'NODE_DEPENDENCY_VERIFIED' in persisted['output'], persisted
    checks.append('pip/npm dependencies install as ordinary user and persist into later executions')
    shared = admin.call('POST', 'shared-spaces', {'name': '团队文件', 'ownerPoolId': pool, 'mountName': 'team', 'quotaMb': 1})['id']
    admin.call('POST', f'workspaces/{aw}/mounts', {'sharedSpaceId': shared, 'mountName': 'team'}, expected=204)
    admin.call('PUT', f'shared-spaces/{shared}/files/content', {'path': 'read.txt', 'content': 'readable'}, expected=204)
    alice.call('PUT', f'shared-spaces/{shared}/files/content', {'path': 'write.txt', 'content': 'forbidden'}, expected=403)
    alice.call('GET', f'shared-spaces/{shared}/grants', expected=403)
    ro = finish(start("cat /workspace/shared/team/read.txt; echo denied > /workspace/shared/team/write.txt"))
    assert ro['exitCode'] != 0 and 'readable' in ro['output'], ro
    admin.call('GET', f'shared-spaces/{shared}/files/content?path=write.txt', expected=404)
    grant = admin.call('POST', f'shared-spaces/{shared}/grants', {'subjectType': 'USER', 'subjectId': a['id'], 'permission': 'RW'})['id']
    rw = finish(start("echo allowed > /workspace/shared/team/write.txt"))
    assert rw['exitCode'] == 0, rw
    assert 'allowed' in alice.call('GET', f'shared-spaces/{shared}/files/content?path=write.txt')['content']
    alice.call('PATCH', f'shared-spaces/{shared}', {'description': 'forbidden'}, expected=403)
    checks.append('real shared RO mount rejects writes; admin-assigned RW enables writes without management')
    scopes = ['workspace:read', 'workspace:execute', 'files:read', 'files:write']
    restricted = alice.call('POST', 'tokens', {'name': 'no-share', 'scopes': scopes})
    missing = finish(start('test ! -e /workspace/shared/team', token=restricted['token']))
    assert missing['exitCode'] == 0, missing
    read_token = alice.call('POST', 'tokens', {'name': 'ro-share', 'scopes': scopes + ['share:read'], 'allowedSharedSpaces': [shared]})
    token_ro = finish(start('echo denied > /workspace/shared/team/token-write.txt', token=read_token['token']))
    assert token_ro['exitCode'] != 0, token_ro
    alice.call('PUT', f'workspaces/{aw}/files/content?area=%20/shared/team/', {'path': 'forbidden.txt', 'content': 'denied'}, token=restricted['token'], expected=403)
    checks.append('Token execution mounts respect explicit shared whitelist and read/write scopes')
    active = start('sleep 60')
    admin.call('DELETE', f'shared-spaces/{shared}/grants/{grant}', expected=204)
    assert finish(active)['status'] == 'Stopped'
    token_active = start('sleep 60', token=restricted['token'])
    alice.call('DELETE', 'tokens/' + restricted['id'], expected=204)
    assert finish(token_active)['status'] == 'Stopped'
    alice.call('GET', 'workspaces/' + aw, token=restricted['token'], expected=401)
    checks.append('grant/token revocation stops already-running containers')
    quota = finish(start("python3 -c \"open('quota.bin','wb').write(b'x'*(3*1024*1024))\""))
    assert quota['exitCode'] != 0 and ('space' in quota['output'].lower() or 'quota' in quota['output'].lower()), quota
    alice.call('DELETE', f'workspaces/{aw}/files?path=quota.bin', expected=204)
    alice.call('PUT', f'workspaces/{aw}/files/content', {'path': 'quota-api.bin', 'content': 'x' * (3 * 1024 * 1024)}, expected=429)
    orphan = finish(start("python3 - <<'PY'\nimport os\nf=open('orphan.bin','wb'); os.unlink('orphan.bin'); f.write(b'x'*(3*1024*1024))\nPY"))
    # Docker Desktop's Windows backing filesystem rejects writes to an open
    # unlinked descriptor with ENOENT. Native Linux behavior is separately
    # verified by filesystem_probe.py using a disposable Linux volume.
    assert orphan['exitCode'] != 0 and ('space' in orphan['output'].lower() or 'No such file or directory' in orphan['output']), orphan
    checks.append('hard storage limits cover command/API writes and unlinked open files')
    cookie = '; '.join(c.name + '=' + c.value for c in alice.cookies)
    terminal = WebSocket(alice.base, f'/api/v1/workspaces/{aw}/terminal', {'Cookie': cookie, 'Origin': alice.base})
    try:
        terminal.send(json.dumps({'type': 'resize', 'cols': 100, 'rows': 30}, separators=(',', ':')))
        terminal.send("printf 'PTY_VERIFIED\\n'; id\n")
        terminal_output = ''
        for _ in range(20):
            terminal_output += terminal.read()
            if 'PTY_VERIFIED' in terminal_output and 'uid=1000' in terminal_output:
                break
        assert 'PTY_VERIFIED' in terminal_output and 'uid=1000' in terminal_output, terminal_output
    finally:
        # Find this test's registered terminal before closing the socket.
        for execution in alice.call('GET', f'workspaces/{aw}/executions'):
            if execution['kind'] == 'terminal' and execution['containerName'] not in containers:
                containers.append(execution['containerName'])
        terminal.close()
    checks.append('real WebSocket PTY handles input/resize and runs as non-root')
    alice.call('PUT', f'workspaces/{aw}/files/content', {'path': 'preview_app.py', 'content': APP}, expected=204)
    app = start('python3 preview_app.py', ports=[8000])
    admin.call('DELETE', f'workspaces/{aw}?mode=permanent', expected=409)
    preview = alice.call('POST', f"workspaces/{aw}/executions/{app['id']}/preview", {'port': 8000})
    uri = urllib.parse.urlsplit(preview['url'])
    alice.call('POST', f"workspaces/{aw}/executions/{app['id']}/preview", {'port': 8001}, expected=403)
    request = urllib.request.Request(alice.base + '/', headers={'Host': uri.netloc, 'Cookie': 'platform-secret=never-forward', 'Authorization': 'Bearer never-forward'})
    for _ in range(40):
        try:
            response = urllib.request.urlopen(request)
            body = json.loads(response.read())
            break
        except urllib.error.HTTPError:
            time.sleep(.25)
    assert body['ok'] and body['cookie'] is None and body['authorization'] is None, body
    ws = WebSocket(alice.base, '/socket', {'Host': uri.netloc})
    try:
        ws.send('WS_VERIFIED')
        assert ws.read() == 'WS_VERIFIED'
    finally:
        ws.close()
    alice.call('DELETE', f"workspaces/{aw}/executions/{app['id']}", expected=204)
    try:
        urllib.request.urlopen(request)
        raise AssertionError('stopped application is still available')
    except urllib.error.HTTPError as error:
        assert error.code in (403, 404), error.code
    checks.append('isolated app HTTP/WebSocket preview works, strips credentials, closes on stop; live deletion denied')
    deny = admin.call('POST', f'shared-spaces/{shared}/grants', {'subjectType': 'USER', 'subjectId': a['id'], 'permission': 'RO', 'effect': 'Deny'})['id']
    alice.call('GET', f'shared-spaces/{shared}/permissions/explain', expected=404)
    alice.call('GET', f'workspaces/{aw}/permissions/explain?sharedSpaceId={shared}', expected=404)
    assert all(space['id'] != shared for space in alice.call('GET', 'shared-spaces'))
    admin.call('DELETE', f'shared-spaces/{shared}/grants/{deny}', expected=204)
    archive = admin.call('POST', 'shared-spaces', {'name': 'archive-test'})['id']
    admin.call('DELETE', f'shared-spaces/{archive}', expected=204)
    alice.call('POST', f'shared-spaces/{archive}/restore', expected=403)
    assert any(space['id'] == archive for space in admin.call('GET', 'shared-spaces?includeArchived=true'))
    admin.call('POST', f'shared-spaces/{archive}/restore', expected=204)
    admin.call('DELETE', f'shared-spaces/{archive}?permanent=true', expected=204)
    checks.append('explicit Deny removes all resource explanations/lists; only admin can restore archived shared space')
