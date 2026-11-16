"""Disposable quota-filesystem probe using the production supervisor."""
import json
import subprocess
import tempfile
import uuid
from pathlib import Path

with tempfile.TemporaryDirectory(prefix='dsh_quota_probe_') as directory:
    root = Path(directory)
    for name in ('files', 'home', 'locks', 'control'):
        (root / name).mkdir()
    script = "import os\nprint('cwd',os.getcwd(),flush=True)\nf=open('orphan.bin','wb'); print('opened',os.listdir('.'),flush=True)\nos.unlink('orphan.bin'); print('unlinked',flush=True)\nf.write(b'x'*(3*1024*1024)); f.flush()\n"
    job = {'executionId': 'probe', 'workspaceId': 'probe', 'command': ['python3', '-c', script], 'terminal': False, 'storageBytes': 2*1024*1024, 'maxFiles': 100, 'mounts': [], 'modelProxy': None, 'preview': None, 'networkHosts': []}
    (root / 'control/job.json').write_text(json.dumps(job), encoding='utf-8')
    args = ['docker', 'run', '--rm', '--read-only', '--cap-drop', 'ALL', '--device', '/dev/fuse', '--security-opt', 'apparmor=unconfined', '--security-opt', 'no-new-privileges', '--tmpfs', '/sandbox:rw,size=16m,mode=755']
    for capability in ('SYS_ADMIN', 'SETUID', 'SETGID', 'SYS_CHROOT', 'NET_ADMIN'):
        args += ['--cap-add', capability]
    volume = 'dsh-usage-probe-' + uuid.uuid4().hex[:12]
    args += ['--name', volume]
    subprocess.run(['docker', 'volume', 'create', volume], check=True, stdout=subprocess.DEVNULL)
    try:
        args += ['--mount', f'type=volume,source={volume},target=/input', '--mount', f'type=volume,source={volume},target=/locks', '--mount', f'type=bind,source={root/"control"},target=/control,readonly']
        args += ['--entrypoint', 'sh', 'teager-dsh-sandbox:usage-only', '-c', 'mkdir -p /input/files /input/home; exec python3 /opt/sandbox/entrypoint.py']
        result = subprocess.run(args, capture_output=True, timeout=30)
        output = result.stdout.decode(errors='replace') + result.stderr.decode(errors='replace')
        print(output)
        assert result.returncode != 0 and 'unlinked' in output and 'No space left on device' in output, output
        print('PASS: Linux unlinked open descriptor cannot exceed storage budget')
    finally:
        subprocess.run(['docker', 'rm', '-f', volume], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        subprocess.run(['docker', 'volume', 'rm', volume], check=True, stdout=subprocess.DEVNULL)
