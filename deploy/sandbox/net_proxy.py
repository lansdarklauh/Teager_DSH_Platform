"""Trusted ingress and optional HTTPS package proxy, outside the user chroot."""
import http.client
import http.server
import ipaddress
import select
import socket
import subprocess
import threading
import urllib.parse
from tls_guard import read_hello


def tunnel(left, right):
    while True:
        readable, _, _ = select.select([left, right], [], [], 60)
        if not readable:
            return
        for source in readable:
            data = source.recv(16384)
            if not data:
                return
            (right if source is left else left).sendall(data)


def ingress(config):
    class Handler(http.server.BaseHTTPRequestHandler):
        protocol_version = 'HTTP/1.1'
        def log_message(self, *_):
            pass

        def handle_request(self):
            if self.headers.get('X-Execution-Key') != config['secret']:
                self.send_error(403); return
            target_port = int(self.headers.get('X-Application-Port', '0'))
            if target_port not in config['ports']:
                self.send_error(403); return
            try:
                if self.headers.get('Upgrade', '').lower() == 'websocket':
                    # HTTPResponse buffers can consume the first server frame,
                    # or close HTTP/1.0 upgrade sockets. Keep a raw transport
                    # after reading exactly the handshake header bytes.
                    with socket.create_connection(('127.0.0.1', target_port), timeout=30) as remote:
                        excluded = {'host', 'cookie', 'authorization', 'x-execution-key', 'x-application-port', 'content-length'}
                        headers = {key: value for key, value in self.headers.items() if key.lower() not in excluded}
                        headers['Host'] = '127.0.0.1:' + str(target_port)
                        request = 'GET ' + self.path + ' HTTP/1.1\r\n' + ''.join(key + ': ' + value + '\r\n' for key, value in headers.items()) + '\r\n'
                        remote.sendall(request.encode())
                        handshake = b''
                        while not handshake.endswith(b'\r\n\r\n'):
                            part = remote.recv(1)
                            if not part or len(handshake) > 65536:
                                raise OSError('invalid application handshake')
                            handshake += part
                        lines = handshake.decode('latin-1').split('\r\n')
                        if lines[0].split(' ')[1] != '101':
                            self.send_error(502, 'application did not upgrade'); return
                        self.send_response_only(101)
                        for line in lines[1:]:
                            if ':' in line:
                                key, value = line.split(':', 1)
                                if key.lower() in ('upgrade', 'connection', 'sec-websocket-accept', 'sec-websocket-protocol', 'sec-websocket-extensions'):
                                    self.send_header(key, value.strip())
                        self.end_headers(); self.wfile.flush()
                        tunnel(self.connection, remote)
                    self.close_connection = True
                    return
                connection = http.client.HTTPConnection('127.0.0.1', target_port, timeout=30)
                excluded = {'host', 'cookie', 'authorization', 'x-execution-key', 'x-application-port', 'content-length'}
                headers = {key: value for key, value in self.headers.items() if key.lower() not in excluded}
                size = int(self.headers.get('Content-Length', '0'))
                if size > 16 * 1024 * 1024:
                    self.send_error(413); return
                body = self.rfile.read(size) if size else None
                connection.request(self.command, self.path, body, headers)
                response = connection.getresponse()
                self.send_response_only(response.status)
                for key, value in response.getheaders():
                    if key.lower() not in ('set-cookie', 'connection', 'transfer-encoding'):
                        self.send_header(key, value)
                self.send_header('Connection', 'Upgrade' if response.status == 101 else 'close')
                self.end_headers()
                if response.status == 101:
                    self.wfile.flush()
                    tunnel(self.connection, connection.sock)
                else:
                    while True:
                        data = response.read1(16384)
                        if not data:
                            break
                        self.wfile.write(data)
                self.close_connection = True
                connection.close()
            except (OSError, ValueError):
                try:
                    self.send_error(502, 'application unavailable')
                except OSError:
                    pass
        do_GET = do_POST = do_PUT = do_PATCH = do_DELETE = do_HEAD = do_OPTIONS = handle_request
    server = http.server.ThreadingHTTPServer(('0.0.0.0', 8766), Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server


def package_proxy(hosts):
    allowed = {host.lower().rstrip('.') for host in hosts}
    class Handler(http.server.BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass
        def do_CONNECT(self):
            try:
                target = urllib.parse.urlsplit('https://' + self.path)
                host = (target.hostname or '').lower().rstrip('.')
                if host not in allowed or target.port != 443 or target.username or target.password:
                    self.send_error(403); return
                addresses = {row[4][0] for row in socket.getaddrinfo(host, 443, socket.AF_INET, socket.SOCK_STREAM)}
                if not addresses or any(not ipaddress.ip_address(address).is_global for address in addresses):
                    self.send_error(403, 'Repository resolves to a non-public address; check trusted DNS and fake-IP settings'); return
                address = sorted(addresses)[0]
                subprocess.run(['iptables', '-A', 'OUTPUT', '-m', 'owner', '--uid-owner', '0', '-d', address, '-p', 'tcp', '--dport', '443', '-j', 'ACCEPT'], check=True)
                with socket.create_connection((address, 443), timeout=30) as remote:
                    self.send_response(200); self.end_headers(); self.wfile.flush()
                    remote.sendall(read_hello(self.connection, host))
                    tunnel(self.connection, remote)
                self.close_connection = True
            except (OSError, ValueError, subprocess.CalledProcessError):
                self.send_error(502)
    server = http.server.ThreadingHTTPServer(('127.0.0.1', 8767), Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server
