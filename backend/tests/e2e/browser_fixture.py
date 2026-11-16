"""Serve the built UI with Vite's full HTTP/SSE/WebSocket API proxy.

Uses real login, not an injected browser session cookie. Owns only its child process.
"""
import os
import subprocess
import time
import urllib.error
import urllib.request


def close_process(process):
    if process is None or process.poll() is not None:
        return
    process.terminate()
    try:
        process.wait(timeout=15)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait()


def start_preview(root, work, data_dir, api_url, ui_port):
    env = os.environ.copy()
    env.update({'DEV_BACKEND_URL': api_url, 'DEV_FRONTEND_PORT': str(ui_port)})
    log = open(data_dir / 'preview.log', 'w', encoding='utf-8')
    process = None
    try:
        process = subprocess.Popen([
            'node', str(root / 'frontend/node_modules/vite/bin/vite.js'), 'preview',
            '--host', '127.0.0.1', '--port', str(ui_port), '--strictPort',
            '--outDir', str(work / 'frontend')
        ], cwd=root / 'frontend', env=env, stdout=log, stderr=subprocess.STDOUT,
            creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
        url = f'http://127.0.0.1:{ui_port}'
        for _ in range(120):
            if process.poll() is not None:
                raise RuntimeError('temporary UI failed; see ' + str(data_dir / 'preview.log'))
            try:
                with urllib.request.urlopen(url + '/health/ready', timeout=1) as response:
                    if response.status == 200:
                        return process, log, url
            except (urllib.error.URLError, TimeoutError):
                time.sleep(.5)
        raise TimeoutError('temporary UI/API proxy did not become ready')
    except BaseException:
        close_process(process)
        log.close()
        raise
