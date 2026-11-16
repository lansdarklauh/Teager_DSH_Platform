"""Disposable SQL database/API/UI validation, without Playwright.

Run from repository root after building API into .cache/usage-validation/build.
--keep-ui waits for browser inspection; Ctrl+C closes only this test's processes.
"""
import argparse
import http.cookiejar
import json
import os
from pathlib import Path
import socket
import subprocess
import time
import urllib.error
import urllib.request
import uuid
import sys

sys.stdout.reconfigure(encoding='utf-8')
sys.stderr.reconfigure(encoding='utf-8')

ROOT = Path(__file__).resolve().parents[3]
WORK = ROOT / '.cache' / 'usage-validation'


def port():
    with socket.socket() as connection:
        connection.bind(('127.0.0.1', 0))
        return connection.getsockname()[1]


class Client:
    def __init__(self, base):
        self.base = base
        self.cookies = http.cookiejar.CookieJar()
        self.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(self.cookies))

    def call(self, method, path, data=None, expected=200, token=None, headers=None):
        request_headers = {'X-Requested-With': 'dsh-web', **(headers or {})}
        if token:
            request_headers['Authorization'] = 'Bearer ' + token
        if data is not None:
            request_headers['Content-Type'] = 'application/json'
        request = urllib.request.Request(self.base + '/api/v1/' + path, data=None if data is None else json.dumps(data, ensure_ascii=False).encode(), headers=request_headers, method=method)
        try:
            response = self.opener.open(request, timeout=60)
        except urllib.error.HTTPError as error:
            response = error
        body = response.read()
        assert response.status == expected, (method, path, response.status, body[:400].decode(errors='replace'))
        return json.loads(body) if body else None


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--keep-ui', action='store_true')
    parser.add_argument('--sandbox', action='store_true')
    parser.add_argument('--ui-only', action='store_true', help='Seed a fresh workbench for rendered UI checks without running the API/container suite')
    args = parser.parse_args()
    WORK.mkdir(parents=True, exist_ok=True)
    database = 'dsh_usage_' + uuid.uuid4().hex[:12]
    data_dir = WORK / database
    data_dir.mkdir()
    api_port = port()
    env = os.environ.copy()
    env.update({'ASPNETCORE_URLS': f'http://127.0.0.1:{api_port}', 'ASPNETCORE_ENVIRONMENT': 'Validation',
                'ConnectionStrings__Default': f'Server=127.0.0.1,14330;Database={database};User ID=sa;Password=Dsh_Test_Pass123;TrustServerCertificate=True',
                'Dsh__Storage__DataDir': str(data_dir / 'data'), 'Dsh__Storage__LogDir': str(data_dir / 'logs'), 'Dsh__Storage__CacheDir': str(data_dir / 'cache'),
                'Dsh__Platform__AutoMigrate': 'true', 'Dsh__Seed__DemoData': 'false', 'Dsh__Seed__AdminPassword': 'Validation_Admin123',
                'Dsh__Runtime__DefaultProvider': 'Mock', 'Dsh__Runtime__TerminalEnabled': 'true', 'Dsh__Sandbox__Enabled': str(args.sandbox).lower(),
                'Dsh__Sandbox__MaxStorageMb': '2', 'Dsh__Sandbox__PreviewBaseUrl': f'http://{{execution}}.preview.localhost:{api_port}/', 'Dsh__Platform__CookieSecure': 'false'})
    log = open(data_dir / 'api.log', 'w', encoding='utf-8')
    api = subprocess.Popen(['dotnet', str(WORK / 'build/bin/Teager.Dsh.Api/debug/Teager.Dsh.Api.dll')], cwd=ROOT / 'backend/src/Teager.Dsh.Api', env=env, stdout=log, stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
    frontend = None
    frontend_log = None
    checks = []
    containers = []
    try:
        base = f'http://127.0.0.1:{api_port}'
        for _ in range(120):
            if api.poll() is not None:
                raise RuntimeError('temporary API failed; see ' + str(data_dir / 'api.log'))
            try:
                urllib.request.urlopen(base + '/health/ready', timeout=1)
                break
            except (urllib.error.URLError, TimeoutError):
                time.sleep(.5)
        admin, alice, bob = Client(base), Client(base), Client(base)
        admin.call('POST', 'auth/login', {'username': 'admin', 'password': 'Validation_Admin123'})
        for name in ('alice', 'bob'):
            admin.call('POST', 'users', {'username': name, 'password': 'Validation_User123', 'displayName': name})
        a = alice.call('POST', 'auth/login', {'username': 'alice', 'password': 'Validation_User123'})
        b = bob.call('POST', 'auth/login', {'username': 'bob', 'password': 'Validation_User123'})
        aw, bw = a['defaultWorkspaceId'], b['defaultWorkspaceId']
        if args.ui_only:
            project = alice.call('POST', f'workspaces/{aw}/projects', {'name': '中文项目', 'description': '页面验收用的个人项目'})
            alice.call('PUT', f'workspaces/{aw}/files/content', {'path': project['directory'] + '/说明.txt', 'content': 'UTF-8 页面文件验收'}, expected=204)
            checks.append('fresh personal workbench seeded for real browser acceptance')
        else:
            assert aw and bw and aw != bw and a['landingPath'] == '/w/' + aw
            checks.append('login assigns distinct personal workbenches')
            view = alice.call('GET', 'workspaces/' + aw)
            assert view['view']['myRole'] == 'Member' and not view['view']['canManage']
            for target in ('workspaces/' + bw, 'workspaces/' + bw + '/files', 'workspaces/' + bw + '/projects'):
                alice.call('GET', target, expected=404)
            checks.append('foreign workspaces/files/projects return 404')
            for target, body in [('pools', {'name': 'forbidden'}), ('shared-spaces', {'name': 'forbidden'}), ('workspaces', {'poolId': view['view']['poolId'], 'name': 'forbidden'})]:
                alice.call('POST', target, body, expected=403)
            alice.call('POST', 'workspaces/' + aw + '/clone', {'name': 'forbidden'}, expected=403)
            alice.call('GET', 'users/lookup', expected=403)
            checks.append('resource creation/clone/user lookup forbidden')
            project = alice.call('POST', 'workspaces/' + aw + '/projects', {'name': '中文项目', 'description': '验证自己的项目'})
            filepath = project['directory'] + '/说明.txt'
            alice.call('PUT', 'workspaces/' + aw + '/files/content', {'path': filepath, 'content': 'UTF-8 文件验证'}, expected=204)
            assert alice.call('GET', 'workspaces/' + aw + '/files/content?path=' + urllib.parse.quote(filepath))['content'] == 'UTF-8 文件验证'
            alice.call('DELETE', 'workspaces/' + aw + '/files?path=.', expected=400)
            checks.append('projects and UTF-8 files work; root deletion denied')
            session = alice.call('POST', 'sessions', {'workspaceId': aw})
            response = alice.call('POST', 'sessions/' + session['id'] + '/messages?wait=true', {'content': '验证对话模块'})
            assert '模拟' in json.dumps(response, ensure_ascii=False)
            bob.call('GET', 'sessions/' + session['id'], expected=404)
            checks.append('conversation completes; foreign session inaccessible')
            alice.call('POST', 'tokens', {'name': 'bad', 'scopes': ['workspace:manage']}, expected=403)
            token = alice.call('POST', 'tokens', {'name': 'sdk', 'scopes': ['workspace:read', 'session:read', 'session:create', 'session:execute', 'files:read', 'files:write']})
            token_client = Client(base)
            token_client.call('GET', 'workspaces/' + aw, token=token['token'])
            token_client.call('GET', 'workspaces/' + bw, token=token['token'], expected=404)
            token_client.call('GET', 'tokens', token=token['token'], expected=403)
            token_client.call('POST', 'workspaces/' + aw + '/commands', {'command': 'id'}, token=token['token'], expected=403)
            checks.append('SDK token stays personal; no management or direct command scope')
            from sandbox_checks import check_permissions, check_sandbox
            check_permissions(admin, alice, bob, a, b, aw, bw, checks)
            from project_checks import check_projects
            check_projects(alice, bob, aw, bw, checks)
            if args.sandbox:
                command = alice.call('POST', 'workspaces/' + aw + '/commands', {'command': "id; printf '隔离执行成功' > sandbox.txt; node --version; ls /control /input 2>/dev/null; exit 0"})
                containers.append(command['containerName'])
                for _ in range(60):
                    output = alice.call('GET', 'workspaces/' + aw + '/executions/' + command['id'] + '/logs')
                    if output['status'] != 'Running':
                        break
                    time.sleep(.5)
                assert output['exitCode'] == 0, output
                assert 'uid=1000' in output['output'] and '/control' not in output['output']
                assert alice.call('GET', 'workspaces/' + aw + '/files/content?path=sandbox.txt')['content'] == '隔离执行成功'
                checks.append('real non-root container command shares personal files and hides control/backing directories')
                check_sandbox(admin, alice, bob, a, b, aw, bw, checks, containers)
        # Built UI with the same full proxy used by local development. A browser
        # must log in normally; writes, streamed responses and PTY are proxied.
        ui_port = port()
        from browser_fixture import start_preview, close_process
        frontend, frontend_log, ui_url = start_preview(ROOT, WORK, data_dir, base, ui_port)
        # Check the actual frontend proxy login and Cookie round trip before UI inspection.
        web = Client(ui_url)
        web.call('GET', 'auth/me', expected=401)
        logged_in = web.call('POST', 'auth/login', {'username': 'alice', 'password': 'Validation_User123'})
        assert logged_in['defaultWorkspaceId'] == aw
        assert web.call('GET', 'auth/me')['defaultWorkspaceId'] == aw
        assert web.call('GET', f'workspaces/{aw}/projects')
        with urllib.request.urlopen(ui_url + '/login') as page:
            assert page.headers.get('Set-Cookie') is None and b'<div id="app">' in page.read()
        checks.append('built UI proxy supports real login and Cookie round trip without injecting an authenticated browser session')
        state = {'url': ui_url, 'api': base, 'workspaceId': aw, 'database': database, 'checks': checks}
        (WORK / 'live-state.json').write_text(json.dumps(state, ensure_ascii=False, indent=2), encoding='utf-8')
        print(json.dumps(state, ensure_ascii=False), flush=True)
        if args.keep_ui:
            stop = WORK / 'stop-ui'
            stop.unlink(missing_ok=True)
            while not stop.exists():
                time.sleep(1)
    finally:
        if frontend:
            close_process(frontend)
        if frontend_log:
            frontend_log.close()
        api.terminate()
        try:
            api.wait(timeout=15)
        except subprocess.TimeoutExpired:
            api.kill(); api.wait()
        log.close()
        for name in containers:
            subprocess.run(['docker', 'rm', '-f', name], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        # Random database name is generated here, never taken from user input.
        subprocess.run(['docker', 'exec', 'dsh-test-mssql', '/opt/mssql-tools18/bin/sqlcmd', '-S', 'localhost', '-U', 'sa', '-P', 'Dsh_Test_Pass123', '-C', '-Q', f'ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]'], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        report = 'ui-only-checks.json' if args.ui_only else 'api-checks.json' if args.sandbox else 'ui-api-checks.json'
        (WORK / report).write_text(json.dumps({'checks': checks, 'testProcessesClosed': api.poll() is not None and (frontend is None or frontend.poll() is not None)}, ensure_ascii=False, indent=2), encoding='utf-8')


if __name__ == '__main__':
    main()
