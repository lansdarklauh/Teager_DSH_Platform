"""Production HTTPS allowlist: actual pip/npm downloads and denied destinations."""
import json
import argparse
from pathlib import Path
import subprocess
import tempfile
import uuid
import ipaddress
import urllib.parse
import urllib.request

REPOSITORIES = ['pypi.org', 'files.pythonhosted.org', 'registry.npmjs.org']


def public_dns_snapshot():
    """Trusted HTTPS resolver answers used only by this disposable probe container."""
    records = {}
    for host in REPOSITORIES:
        query = urllib.parse.urlencode({'name': host, 'type': 'A', 'edns_client_subnet': '0.0.0.0/0'})
        with urllib.request.urlopen('https://dns.google/resolve?' + query, timeout=15) as response:
            answer = json.load(response)
        assert answer.get('Status') == 0 and not answer.get('TC'), answer
        assert answer['Question'][0]['name'].rstrip('.').lower() == host
        addresses = [item['data'] for item in answer.get('Answer', []) if item['type'] == 1]
        assert addresses and all(ipaddress.ip_address(ip).version == 4 and ipaddress.ip_address(ip).is_global for ip in addresses), answer
        records[host] = addresses[0]
    return records

INSTALL = r'''set -eu
python3 -m pip install --disable-pip-version-check --no-cache-dir --no-deps --target /home/user/python-online --index-url https://pypi.org/simple six==1.17.0
PYTHONPATH=/home/user/python-online python3 -c 'import six; assert six.__version__ == "1.17.0"; print("PIP_ONLINE_VERIFIED")'
npm install --prefix /home/user/node-online --registry https://registry.npmjs.org --no-audit --no-fund --ignore-scripts is-number@7.0.0
node -e 'if (!require("/home/user/node-online/node_modules/is-number")(42)) process.exit(1); console.log("NPM_ONLINE_VERIFIED")'
'''
BOUNDARIES = r'''set -eu
python3 - <<'PY'
import socket, urllib.request, urllib.error
for url in ('https://example.com/', 'https://127.0.0.1:443/', 'https://169.254.169.254:443/'):
 try:
  urllib.request.urlopen(url, timeout=5)
 except (urllib.error.URLError, TimeoutError, ConnectionError):
  print('DENIED', url)
 else:
  raise AssertionError('destination outside allowlist accepted: ' + url)
for address in ('1.1.1.1', '169.254.169.254'):
 try:
  socket.create_connection((address, 443), timeout=2)
 except (OSError, TimeoutError):
  print('DIRECT_EGRESS_DENIED', address)
 else:
  raise AssertionError('untrusted process bypassed network broker')
assert 'CapEff:\t0000000000000000' in open('/proc/self/status').read()
print('UNTRUSTED_CAPABILITIES_ZERO')
PY
'''

parser = argparse.ArgumentParser()
parser.add_argument('--deny-only', action='store_true', help='Validate network denials/capabilities without downloading packages')
parser.add_argument('--expect-dns-denial', action='store_true', help='Check diagnostic when the current environment returns non-public repository DNS')
parser.add_argument('--public-dns-snapshot', action='store_true', help='Use verified Google DoH public IPv4 answers only in this probe container; does not change host/platform DNS')
options = parser.parse_args()
if options.public_dns_snapshot and options.expect_dns_denial:
    parser.error('--public-dns-snapshot cannot be combined with --expect-dns-denial')
dns_records = public_dns_snapshot() if options.public_dns_snapshot else {}
if dns_records:
    Path('.cache/usage-validation/network-public-dns.json').write_text(json.dumps({'resolver': 'https://dns.google/resolve', 'probeOnly': True, 'records': dns_records}, indent=2), encoding='utf-8')
    print('PROBE_ONLY_PUBLIC_DNS ' + json.dumps(dns_records), flush=True)
script = BOUNDARIES if options.deny_only else INSTALL + BOUNDARIES
if options.expect_dns_denial:
    script += r'''python3 - <<'PY'
import urllib.request, urllib.error
try:
 urllib.request.urlopen('https://pypi.org/simple/six/', timeout=5)
except urllib.error.URLError as error:
 assert 'Repository resolves to a non-public address' in str(error), error
 print('NON_PUBLIC_DNS_DIAGNOSTIC_VERIFIED')
else:
 raise AssertionError('expected non-public DNS rejection in this environment')
PY
'''

with tempfile.TemporaryDirectory(prefix='dsh_network_probe_') as temporary:
    root = Path(temporary)
    (root / 'control').mkdir()
    name = 'dsh-usage-network-' + uuid.uuid4().hex[:12]
    job = {'executionId': name, 'workspaceId': name, 'command': ['sh', '-c', script], 'terminal': False, 'storageBytes': 16*1024*1024, 'maxFiles': 3000, 'mounts': [], 'modelProxy': None, 'preview': None, 'networkHosts': REPOSITORIES}
    (root / 'control/job.json').write_text(json.dumps(job), encoding='utf-8')
    subprocess.run(['docker', 'volume', 'create', name], check=True, stdout=subprocess.DEVNULL)
    args = ['docker', 'run', '--rm', '--name', name, '--read-only', '--network', 'bridge', '--cap-drop', 'ALL', '--device', '/dev/fuse', '--security-opt', 'apparmor=unconfined', '--security-opt', 'no-new-privileges', '--memory', '512m', '--cpus', '1', '--pids-limit', '128', '--tmpfs', '/sandbox:rw,size=16m,mode=755']
    for capability in ('SYS_ADMIN', 'SETUID', 'SETGID', 'SYS_CHROOT', 'NET_ADMIN', 'KILL'):
        args += ['--cap-add', capability]
    for host, address in dns_records.items():
        args += ['--add-host', f'{host}:{address}']
    args += ['--mount', f'type=volume,source={name},target=/input', '--mount', f'type=volume,source={name},target=/locks', '--mount', f'type=bind,source={root/"control"},target=/control,readonly', '--entrypoint', 'sh', 'teager-dsh-sandbox:usage-only', '-c', 'mkdir -p /input/files /input/home; exec python3 /opt/sandbox/entrypoint.py']
    try:
        result = subprocess.run(args, capture_output=True, timeout=120)
        output = result.stdout.decode(errors='replace') + result.stderr.decode(errors='replace')
        log = 'network-public-dns-probe.log' if options.public_dns_snapshot else 'network-deny-probe.log' if options.deny_only else 'network-probe.log'
        Path('.cache/usage-validation/' + log).write_text(output, encoding='utf-8')
        print(output)
        markers = ['DENIED https://example.com/', 'DENIED https://127.0.0.1:443/', 'DENIED https://169.254.169.254:443/', 'DIRECT_EGRESS_DENIED 1.1.1.1', 'DIRECT_EGRESS_DENIED 169.254.169.254', 'UNTRUSTED_CAPABILITIES_ZERO']
        if not options.deny_only:
            markers += ['PIP_ONLINE_VERIFIED', 'NPM_ONLINE_VERIFIED']
        if options.expect_dns_denial:
            markers += ['NON_PUBLIC_DNS_DIAGNOSTIC_VERIFIED']
        assert result.returncode == 0 and all(marker in output for marker in markers), result.returncode
        print('PASS: network boundary and zero-capability checks' + ('' if options.deny_only else '; actual pip/npm HTTPS downloads') + (' with probe-only verified public DNS' if dns_records else ''))
    finally:
        subprocess.run(['docker', 'rm', '-f', name], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        subprocess.run(['docker', 'volume', 'rm', name], check=True, stdout=subprocess.DEVNULL)
