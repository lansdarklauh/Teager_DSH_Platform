"""Real HTTP/SSE/WebSocket checks through the disposable built-frontend proxy.

This validates transport and authentication, not rendered browser interactions.
"""
import json
import subprocess
import urllib.request
import uuid
from pathlib import Path
from usage_only import Client
from sandbox_checks import WebSocket


def check_proxy(state):
    web = Client(state['url'])
    web.call('GET', 'auth/me', expected=401)
    login = web.call('POST', 'auth/login', {'username': 'alice', 'password': 'Validation_User123'})
    workspace = state['workspaceId']
    assert login['defaultWorkspaceId'] == workspace
    project = web.call('GET', f'workspaces/{workspace}/projects')[0]
    session = web.call('POST', 'sessions', {'workspaceId': workspace})['id']
    containers = []
    report = []
    try:
        result = web.call('POST', f'sessions/{session}/messages', {
            'content': '通过前端代理检查项目引用', 'projectIds': [project['id']],
            'requestId': str(uuid.uuid4())
        }, expected=202)
        events = []
        request = urllib.request.Request(state['url'] + f'/api/v1/sessions/{session}/events?after=0', headers={'X-Requested-With': 'dsh-web'})
        with web.opener.open(request, timeout=15) as stream:
            assert stream.headers.get_content_type() == 'text/event-stream'
            for line in stream:
                if not line.startswith(b'data: '):
                    continue
                event = json.loads(line[6:])
                events.append(event)
                if event.get('type') == 'run_end' and event.get('runId') == result['runId']:
                    assert event['status'] == 'Completed', event
                    break
        assert next(e for e in events if e['type'] == 'run_start')['projects'][0]['id'] == project['id']
        assert any(e['type'] == 'text' for e in events)
        assert web.call('GET', f'sessions/{session}/messages')[0]['projects'][0]['id'] == project['id']
        report.append('frontend proxy POST messages, project metadata and live SSE through run_end')
        existing = {e['id'] for e in web.call('GET', f'workspaces/{workspace}/executions')}
        cookie = '; '.join(c.name + '=' + c.value for c in web.cookies)
        terminal = WebSocket(state['url'], f'/api/v1/workspaces/{workspace}/terminal', {'Cookie': cookie, 'Origin': state['url']})
        try:
            terminal.send(json.dumps({'type': 'resize', 'cols': 100, 'rows': 30}))
            terminal.send("printf 'FRONTEND_PTY_VERIFIED\\n'; id\n")
            output = ''
            for _ in range(20):
                output += terminal.read()
                if 'FRONTEND_PTY_VERIFIED' in output and 'uid=1000' in output:
                    break
            assert 'FRONTEND_PTY_VERIFIED' in output and 'uid=1000' in output, output
            report.append('frontend WebSocket proxy preserves session and Origin, PTY input/resize and non-root execution')
        finally:
            terminal.close()
            for execution in web.call('GET', f'workspaces/{workspace}/executions'):
                if execution['id'] not in existing and execution['kind'] == 'terminal':
                    containers.append(execution['containerName'])
                    web.call('DELETE', f'workspaces/{workspace}/executions/{execution["id"]}', expected=204)
    finally:
        web.call('DELETE', f'sessions/{session}', expected=204)
        for name in containers:
            subprocess.run(['docker', 'rm', '-f', name], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    return report


if __name__ == '__main__':
    work = Path('.cache/usage-validation')
    state = json.loads((work / 'live-state.json').read_text(encoding='utf-8'))
    checks = check_proxy(state)
    (work / 'frontend-proxy-checks.json').write_text(json.dumps({'checks': checks}, ensure_ascii=False, indent=2), encoding='utf-8')
    print('PASS: ' + '; '.join(checks))
