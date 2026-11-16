"""Verify shipped defaults and real configured model in a disposable deployment.

Copies only enabled platform model rows from the local development database.
Credentials stay inside SQL/the trusted sandbox broker and are never printed.
Own processes, database, and containers are cleaned up in finally.
"""
import json
import os
from pathlib import Path
import subprocess
import time
import urllib.error
import urllib.request
import uuid

from usage_only import ROOT, WORK, Client, port
from browser_fixture import close_process, start_preview
from sandbox_checks import WebSocket


def sql(query):
    result = subprocess.run(['docker', 'exec', 'dsh-test-mssql', '/opt/mssql-tools18/bin/sqlcmd', '-S', 'localhost', '-U', 'sa', '-P', 'Dsh_Test_Pass123', '-C', '-b', '-Q', query], capture_output=True)
    if result.returncode:
        raise RuntimeError('database fixture operation failed')


def main():
    WORK.mkdir(parents=True, exist_ok=True)
    database = 'dsh_runtime_' + uuid.uuid4().hex[:12]
    directory = WORK / database
    directory.mkdir()
    base = f'http://127.0.0.1:{port()}'
    env = os.environ.copy()
    env.update({'ASPNETCORE_URLS': base, 'ASPNETCORE_ENVIRONMENT': 'Validation',
                'ConnectionStrings__Default': f'Server=127.0.0.1,14330;Database={database};User ID=sa;Password=Dsh_Test_Pass123;TrustServerCertificate=True',
                'Dsh__Storage__DataDir': str(directory / 'data'), 'Dsh__Storage__LogDir': str(directory / 'logs'), 'Dsh__Storage__CacheDir': str(directory / 'cache'),
                'Dsh__Seed__AdminPassword': 'Validation_Admin123', 'Dsh__Seed__DemoData': 'false', 'Dsh__Platform__CookieSecure': 'false'})
    if os.environ.get('DSH_RUNTIME_ALLOW_MINIMAX_FAKE_IP') == '1':
        env['Dsh__Sandbox__ModelFakeIpHosts__0'] = 'api.minimaxi.com'
    if os.environ.get('DSH_SANDBOX_TEST_IMAGE'):
        env['Dsh__Sandbox__Image'] = os.environ['DSH_SANDBOX_TEST_IMAGE']
    api = ui = None
    ui_log = None
    log = open(directory / 'api.log', 'w', encoding='utf-8')
    checks = []
    containers = []

    def start(bootstrap=False):
        launch = env.copy()
        if bootstrap:
            launch['Dsh__Runtime__DefaultProvider'] = 'Mock'
        process = subprocess.Popen(['dotnet', str(WORK / 'build/bin/Teager.Dsh.Api/debug/Teager.Dsh.Api.dll')], cwd=ROOT / 'backend/src/Teager.Dsh.Api', env=launch, stdout=log, stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NO_WINDOW)
        try:
            for _ in range(120):
                if process.poll() is not None:
                    raise RuntimeError('temporary API failed to start')
                try:
                    urllib.request.urlopen(base + '/health/ready', timeout=1)
                    return process
                except (urllib.error.URLError, TimeoutError):
                    time.sleep(.5)
            raise TimeoutError('temporary API readiness timeout')
        except BaseException:
            close_process(process)
            raise

    try:
        api = start(bootstrap=True)
        admin, alice = Client(base), Client(base)
        admin.call('POST', 'auth/login', {'username': 'admin', 'password': 'Validation_Admin123'})
        admin.call('POST', 'users', {'username': 'alice', 'password': 'Validation_User123', 'displayName': '默认环境验收'})
        original = alice.call('POST', 'auth/login', {'username': 'alice', 'password': 'Validation_User123'})
        aw = original['defaultWorkspaceId']
        session = alice.call('POST', 'sessions', {'workspaceId': aw})
        alice.call('POST', f'sessions/{session["id"]}/messages?wait=true', {'content': 'keep mock history'})
        alice.call('PUT', f'workspaces/{aw}/files/content', {'path': 'keep.txt', 'content': 'keep personal files'}, expected=204)
        close_process(api)
        # Server-side copy: never export model parameters or key values to output/files.
        sql(f"INSERT INTO [{database}].dbo.ConfigItems SELECT * FROM [DshPlatform].dbo.ConfigItems WHERE Kind='Model' AND Scope='Platform' AND Enabled=1;")
        sql(f"INSERT INTO [{database}].dbo.Secrets SELECT s.* FROM [DshPlatform].dbo.Secrets s WHERE s.Scope='Platform' AND s.Name IN (SELECT JSON_VALUE(c.ParamsJson,'$.apiKeyEnv') FROM [DshPlatform].dbo.ConfigItems c WHERE c.Kind='Model' AND c.Scope='Platform' AND c.Enabled=1);")
        api = start()
        alice.call('POST', 'auth/login', {'username': 'alice', 'password': 'Validation_User123'})
        view = alice.call('GET', f'workspaces/{aw}')
        assert view['runtimeProvider'] == 'DeepSeekHarness', view['runtimeProvider']
        assert view['sandboxEnabled'] and view['terminalAllowed']
        assert len(alice.call('GET', f'sessions/{session["id"]}/messages')) == 2
        assert alice.call('GET', f'workspaces/{aw}/files/content?path=keep.txt')['content'] == 'keep personal files'
        admin.call('POST', 'auth/login', {'username': 'admin', 'password': 'Validation_Admin123'})
        admin.call('POST', 'users', {'username': 'bob', 'password': 'Validation_User123', 'displayName': '新用户'})
        bob = Client(base)
        bw = bob.call('POST', 'auth/login', {'username': 'bob', 'password': 'Validation_User123'})['defaultWorkspaceId']
        assert bw != aw and bob.call('GET', f'workspaces/{bw}')['runtimeProvider'] == 'DeepSeekHarness'
        checks.append('shipped defaults enable terminal/sandbox/real engine for existing and new personal users; files/history preserved')
        assert alice.call('GET', f'workspaces/{aw}/environment')['ready']
        cookie = '; '.join(c.name + '=' + c.value for c in alice.cookies)
        terminal = WebSocket(base, f'/api/v1/workspaces/{aw}/terminal', {'Cookie': cookie, 'Origin': base})
        try:
            terminal.send("printf 'DEFAULT_TERMINAL_OK'; id; printf 'file from terminal' > terminal-proof.txt\n")
            output = ''
            for _ in range(20):
                output += terminal.read()
                if 'uid=1000' in output:
                    break
            assert 'DEFAULT_TERMINAL_OK' in output and 'uid=1000' in output
        finally:
            terminal.close()
        assert alice.call('GET', f'workspaces/{aw}/files/content?path=terminal-proof.txt')['content'] == 'file from terminal'
        checks.append('default terminal executes as non-root and persists personal files without per-user activation')
        real_session = alice.call('POST', 'sessions', {'workspaceId': aw, 'title': '真实模型验收'})
        if os.environ.get('DSH_RUNTIME_VERIFY_MODEL_LIMITS') == '1':
            model = admin.call('GET', 'configs?scope=Platform&kind=Model')[0]
            model['params'].update({'taskBudget':0, 'contextWindow':65536})
            admin.call('PUT', 'configs', {'scope':'Platform','kind':'Model','key':model['key'],'displayName':model['displayName'],'enabled':True,'params':model['params']})
            saved = admin.call('GET', 'configs?scope=Platform&kind=Model')[0]['params']
            assert saved['taskBudget'] == 0 and saved['contextWindow'] == 65536
            checks.append('administrator saves and reloads model task budget/context limits through real API')
        replies = []
        if os.environ.get('DSH_RUNTIME_MODEL_LIMITS_FIXTURE') == '1':
            assert os.environ.get('DSH_RUNTIME_VERIFY_MODEL_LIMITS') == '1'
            # Only the disposable database is changed. Budget=1 must reject
            # before any upstream request, so this dummy key is never sent.
            admin.call('PUT', 'secrets', {'scope':'Platform','name':model['params']['apiKeyEnv'],'value':'dummy-budget-fixture-key'}, expected=204)
            checks.append('fixture mode: upstream reply skipped; real DSH positive contract verified separately')
        else:
            run = alice.call('POST', f'sessions/{real_session["id"]}/messages', {'content': '请只回复：真实模型连接成功', 'requestId': str(uuid.uuid4())}, expected=202)
            deadline = time.monotonic() + 120
            while time.monotonic() < deadline:
                runs = alice.call('GET', f'sessions/{real_session["id"]}/runs')
                if runs[0]['status'] != 'Running':
                    break
                time.sleep(1)
            if os.environ.get('DSH_RUNTIME_EXPECT_KEY_DECRYPTION_BLOCK') == '1':
                assert runs[0]['status'] == 'Failed', 'unusable model key must promptly fail the run'
                errors = [m['content'] for m in alice.call('GET', f'sessions/{real_session["id"]}/messages') if m['role']=='system']
                assert any('模型密钥无法解密' in message and '重新保存' in message for message in errors)
                checks.append('unusable encrypted model credential promptly returns explicit recovery instructions through actual session API')
                print(json.dumps({'checks':checks},ensure_ascii=False),flush=True)
                return
            if os.environ.get('DSH_RUNTIME_EXPECT_DNS_BLOCK') == '1':
                assert runs[0]['status'] == 'Failed', 'DNS rejection must finish the run without hanging'
                errors = [m['content'] for m in alice.call('GET', f'sessions/{real_session["id"]}/messages') if m['role'] == 'system']
                assert any('DNS/VPN' in message for message in errors)
                checks.append('default DNS rejection promptly finishes the run and shows actionable DNS/VPN error')
                print(json.dumps({'checks': checks}, ensure_ascii=False), flush=True)
                return
            if runs[0]['status'] != 'Completed':
                # Deliberately omit provider exception text which could contain sensitive data.
                raise RuntimeError('real configured model did not complete; status=' + runs[0]['status'] + '; inspect trusted API/container diagnostics')
            replies = [m['content'] for m in alice.call('GET', f'sessions/{real_session["id"]}/messages') if m['role'] == 'assistant']
            assert replies and '模拟回复' not in ''.join(replies)
            checks.append('existing configured upstream model responds through real isolated DSH agent')
        if os.environ.get('DSH_RUNTIME_VERIFY_MODEL_LIMITS') == '1':
            model['params']['taskBudget'] = 1
            admin.call('PUT', 'configs', {'scope':'Platform','kind':'Model','key':model['key'],'displayName':model['displayName'],'enabled':True,'params':model['params']})
            bounded = alice.call('POST', 'sessions', {'workspaceId':aw, 'title':'任务预算验收'})
            alice.call('POST', f'sessions/{bounded["id"]}/messages', {'content':'请只回复预算测试', 'requestId':str(uuid.uuid4())}, expected=202)
            for _ in range(120):
                bounded_runs = alice.call('GET', f'sessions/{bounded["id"]}/runs')
                if bounded_runs[0]['status'] != 'Running': break
                time.sleep(1)
            assert bounded_runs[0]['status'] == 'Failed'
            errors = [m['content'] for m in alice.call('GET', f'sessions/{bounded["id"]}/messages') if m['role']=='system']
            assert any('模型预算不足' in error for error in errors), 'budget error was not actionable'
            assert all('<html' not in error.lower() for error in errors), 'HTML model error leaked to UI'
            checks.append('finite model task budget is enforced with Chinese error; saved config applies to next task without API restart')
            model['params'].update({'taskBudget':0,'contextWindow':0})
            admin.call('PUT', 'configs', {'scope':'Platform','kind':'Model','key':model['key'],'displayName':model['displayName'],'enabled':True,'params':model['params']})
        print(json.dumps({'checks': checks, 'reply': replies[-1] if replies else None}, ensure_ascii=False), flush=True)
        if os.environ.get('DSH_RUNTIME_KEEP_UI') == '1':
            ui, ui_log, url = start_preview(ROOT, WORK, directory, base, port())
            (WORK / 'runtime-live-state.json').write_text(json.dumps({'url': url, 'api': base, 'workspaceId': aw, 'database': database}), encoding='utf-8')
            print('UI=' + url, flush=True)
            stop = WORK / 'stop-runtime-ui'
            stop.unlink(missing_ok=True)
            while not stop.exists():
                time.sleep(1)
    finally:
        if api is not None and api.poll() is None:
            try:
                containers.extend(row['containerName'] for row in alice.call('GET', f'workspaces/{aw}/executions'))
            except Exception:
                pass
        close_process(ui)
        close_process(api)
        if ui_log:
            ui_log.close()
        log.close()
        containers.extend('dsh-usage-' + execution.name for execution in (directory / 'data' / 'execution-control').glob('*'))
        for name in set(containers):
            subprocess.run(['docker', 'rm', '-f', name], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        sql(f'IF DB_ID(N\'{database}\') IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END')
        report = 'model-key-recovery-checks.json' if os.environ.get('DSH_RUNTIME_EXPECT_KEY_DECRYPTION_BLOCK') == '1' else ('runtime-dns-denial-checks.json' if os.environ.get('DSH_RUNTIME_EXPECT_DNS_BLOCK') == '1' else ('model-limits-api-checks.json' if os.environ.get('DSH_RUNTIME_VERIFY_MODEL_LIMITS') == '1' else 'runtime-defaults-checks.json'))
        (WORK / report).write_text(json.dumps({'checks': checks, 'testProcessesClosed': (api is None or api.poll() is not None) and (ui is None or ui.poll() is not None)}, ensure_ascii=False, indent=2), encoding='utf-8')


if __name__ == '__main__':
    main()
