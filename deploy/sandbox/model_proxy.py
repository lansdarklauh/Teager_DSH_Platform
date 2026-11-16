"""Per-execution model broker. Upstream credentials never enter the chroot.

Only one administrator-selected model and completion paths are accepted.
The supervisor's output firewall pins public destination addresses; user
processes cannot directly reach provider APIs or any private network.
"""
import http.client
import http.server
import ipaddress
import json
import socket
import ssl
import subprocess
import threading
import urllib.parse


def start(config):
    target = urllib.parse.urlsplit(config['baseUrl'])
    if target.scheme != 'https' or target.username or target.password or target.port not in (None, 443):
        raise RuntimeError('upstream must be a public HTTPS endpoint')
    addresses = list({value[4][0] for value in socket.getaddrinfo(target.hostname, 443, socket.AF_INET, socket.SOCK_STREAM)})
    def allowed_address(value):
        address = ipaddress.ip_address(value)
        return address.is_global or (config.get('allowFakeIp') is True and address in ipaddress.ip_network('198.18.0.0/15'))
    if not addresses or any(not allowed_address(value) for value in addresses):
        raise RuntimeError('private upstream forbidden')
    for address in addresses:
        subprocess.run(['iptables', '-A', 'OUTPUT', '-m', 'owner', '--uid-owner', '0', '-d', address, '-p', 'tcp', '--dport', '443', '-j', 'ACCEPT'], check=True)
    budget = config.get('maxBudget', 0)
    if not isinstance(budget, int) or isinstance(budget, bool) or budget < 0:
        raise RuntimeError('invalid model budget')
    limited = budget > 0
    max_output = config.get('maxOutputTokens', 0)
    calls = 0
    lock = threading.Lock()

    class PinnedHTTPS(http.client.HTTPSConnection):
        def connect(self):
            connection = socket.create_connection((addresses[0], 443), timeout=self.timeout)
            self.sock = ssl.create_default_context().wrap_socket(connection, server_hostname=target.hostname)

    class Handler(http.server.BaseHTTPRequestHandler):
        protocol_version = 'HTTP/1.1'
        def log_message(self, *_):
            pass

        def error_json(self, status, code, message, **details):
            payload = json.dumps({'error': {'code': code, 'message': message, 'type': 'platform_limit', **details}}, ensure_ascii=False).encode()
            self.send_response(status)
            self.send_header('Content-Type', 'application/json; charset=utf-8')
            self.send_header('Content-Length', str(len(payload)))
            self.send_header('Connection', 'close')
            self.end_headers()
            self.wfile.write(payload)
            self.close_connection = True

        def do_POST(self):
            nonlocal budget, calls
            path = urllib.parse.urlsplit(self.path).path
            if path not in ('/chat/completions', '/v1/chat/completions', '/responses', '/v1/responses'):
                self.error_json(403, 'model_endpoint_forbidden', '只能访问当前任务的模型生成接口'); return
            try:
                size = int(self.headers.get('Content-Length', '0'))
                if not 0 < size <= 16 * 1024 * 1024:
                    self.error_json(413, 'model_request_too_large', '模型请求超过平台传输大小上限（16 MiB），请减少上下文或附件'); return
                body = json.loads(self.rfile.read(size))
                if not isinstance(body, dict):
                    self.error_json(400, 'invalid_model_request', '模型请求必须是 JSON 对象'); return
                if body.get('model') != config['model']:
                    self.error_json(403, 'execution_model_forbidden', '请求模型不属于当前任务'); return
                requested_output = body.get('max_completion_tokens', body.get('max_tokens', body.get('max_output_tokens')))
                if requested_output is not None and (not isinstance(requested_output, int) or isinstance(requested_output, bool) or requested_output < 1):
                    self.error_json(400, 'invalid_output_limit', '模型输出上限必须为正整数'); return
                output = requested_output or max_output or 32768
                if max_output > 0:
                    output = min(output, max_output)
                # Reserve conservatively in bytes + output tokens. A request
                # cannot evade the budget through concurrent streaming calls.
                with lock:
                    reservation = size + output
                    if calls >= 100:
                        self.error_json(429, 'execution_model_call_limit', '本次任务已达到平台模型调用次数上限（100 次），请拆分任务'); return
                    if limited and budget < reservation:
                        self.error_json(429, 'execution_model_budget_exceeded', '本次任务的模型预算不足，请管理员在模型配置中提高单次任务预算，或设为 0 使用原生设置', remainingBudget=budget, requiredBudget=reservation); return
                    if limited:
                        budget -= reservation
                    calls += 1
                for field in ('max_completion_tokens', 'max_output_tokens', 'max_tokens'):
                    body.pop(field, None)
                if requested_output is not None or max_output > 0 or limited:
                    body['max_output_tokens' if path.endswith('/responses') else 'max_tokens'] = output
                if not path.endswith('/responses'):
                    body['n'] = 1
                    body.pop('best_of', None)
                suffix = '/responses' if path.endswith('/responses') else '/chat/completions'
                prefix = target.path.rstrip('/')
                if prefix.endswith('/v1'):
                    upstream = prefix + suffix
                else:
                    upstream = prefix + '/v1' + suffix
                connection = PinnedHTTPS(target.hostname, timeout=120)
                connection.request('POST', upstream, json.dumps(body).encode(), {'Content-Type': 'application/json', 'Authorization': 'Bearer ' + config['apiKey']})
                response = connection.getresponse()
                self.send_response(response.status)
                self.send_header('Content-Type', response.getheader('Content-Type', 'application/json'))
                self.send_header('Connection', 'close'); self.end_headers()
                while True:
                    chunk = response.read1(8192)
                    if not chunk:
                        break
                    self.wfile.write(chunk); self.wfile.flush()
                connection.close(); self.close_connection = True
            except (json.JSONDecodeError, ValueError):
                self.error_json(400, 'invalid_model_request', '模型请求格式无效')
            except Exception:
                # Never copy upstream exception text or headers containing keys.
                if not self.wfile.closed:
                    try:
                        self.error_json(502, 'model_upstream_unavailable', '模型上游暂不可用')
                    except OSError:
                        pass
    server = http.server.ThreadingHTTPServer(('127.0.0.1', 8765), Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server


def firewall():
    # No user process has NET_ADMIN. This is installed before any user program.
    subprocess.run(['iptables', '-P', 'OUTPUT', 'DROP'], check=True)
    subprocess.run(['iptables', '-A', 'OUTPUT', '-o', 'lo', '-j', 'ACCEPT'], check=True)
    subprocess.run(['iptables', '-A', 'OUTPUT', '-m', 'conntrack', '--ctstate', 'ESTABLISHED,RELATED', '-j', 'ACCEPT'], check=True)
    for protocol in ('tcp', 'udp'):
        subprocess.run(['iptables', '-A', 'OUTPUT', '-m', 'owner', '--uid-owner', '0', '-p', protocol, '--dport', '53', '-j', 'ACCEPT'], check=True)
    subprocess.run(['ip6tables', '-P', 'OUTPUT', 'DROP'], check=True)
