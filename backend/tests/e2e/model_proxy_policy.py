"""Check actual broker initialization policy without contacting an upstream."""
import http.server
import socket
import subprocess
import sys
import threading
from pathlib import Path
from unittest.mock import patch, Mock

sys.path.insert(0, str(Path(__file__).resolve().parents[3] / 'deploy/sandbox'))
import model_proxy


def main():
    cases = [('93.184.216.34', False, True), ('198.18.0.30', False, False),
             ('198.18.0.30', True, True), ('198.19.255.255', True, True),
             ('198.20.0.1', False, True), ('127.0.0.1', True, False),
             ('192.168.31.25', True, False), ('10.0.0.1', True, False),
             ('169.254.169.254', True, False)]
    for address, allow, accepted in cases:
        firewall_calls = []
        with patch.object(socket, 'getaddrinfo', return_value=[(socket.AF_INET, socket.SOCK_STREAM, 6, '', (address, 443))]), \
                patch.object(subprocess, 'run', side_effect=lambda args, **kw: firewall_calls.append(args)), \
                patch.object(http.server, 'ThreadingHTTPServer', return_value=Mock()), \
                patch.object(threading, 'Thread', return_value=Mock()):
            try:
                model_proxy.start({'baseUrl': 'https://fixture.example/v1', 'apiKey': 'dummy-key', 'model': 'fixture', 'allowFakeIp': allow})
                outcome = True
            except RuntimeError:
                outcome = False
        assert outcome == accepted, (address, allow, outcome)
        if not accepted:
            assert not firewall_calls, 'blocked destination was admitted to firewall'
    print('PASS: model broker requires explicit fake-IP permission; loopback/private/link-local remain blocked')


if __name__ == '__main__':
    main()
