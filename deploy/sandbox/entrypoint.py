"""Trusted supervisor. All user code runs after chroot and capability removal."""
import ctypes
import fcntl
import json
import multiprocessing
import os
import pty
import select
import signal
import struct
import subprocess
import sys
import termios
import time
from pathlib import Path
from quota_fs import serve


def mount(source, target, readonly=True):
    Path(target).mkdir(parents=True, exist_ok=True)
    subprocess.run(['mount', '--bind', source, target], check=True)
    if readonly:
        subprocess.run(['mount', '-o', 'remount,bind,ro,nosuid,nodev', target], check=True)


def quota(source, target, roots, lock, limit, inodes, readonly=False):
    Path(target).mkdir(parents=True, exist_ok=True)
    process = multiprocessing.Process(target=serve, args=(source, target, roots, lock, limit, inodes, readonly))
    # Initialization failures must not wait indefinitely for FUSE children.
    process.daemon = True
    process.start()
    for _ in range(100):
        if os.path.ismount(target):
            return process
        if not process.is_alive():
            raise RuntimeError('quota filesystem unavailable')
        time.sleep(.05)
    raise RuntimeError('quota filesystem mount timed out')


def main():
    config = json.loads(Path('/control/job.json').read_text())
    os.environ['SUPERVISOR_EXECUTION'] = config['executionId']
    # The only writable user directories are quota-backed files/home and
    # bounded tmpfs. Backing paths, job config, locks and supervisor remain
    # outside the chroot; /proc hides all other UIDs.
    root = '/sandbox'
    for directory in ('usr', 'bin', 'lib', 'lib64'):
        if os.path.exists('/' + directory):
            mount('/' + directory, root + '/' + directory)
    Path(root + '/etc').mkdir(exist_ok=True)
    Path(root + '/etc/passwd').write_text('user:x:1000:1000::/home/user:/bin/bash\n')
    Path(root + '/etc/group').write_text('user:x:1000:\n')
    for name in ('resolv.conf', 'hosts', 'nsswitch.conf'):
        Path(root + '/etc/' + name).write_bytes(Path('/etc/' + name).read_bytes())
    mount('/etc/ssl', root + '/etc/ssl')
    Path(root + '/proc').mkdir(exist_ok=True)
    subprocess.run(['mount', '-t', 'proc', '-o', 'hidepid=2,nosuid,nodev,noexec', 'proc', root + '/proc'], check=True)
    Path(root + '/dev').mkdir(exist_ok=True)
    for name in ('null', 'zero', 'random', 'urandom'):
        target = root + '/dev/' + name
        Path(target).touch()
        subprocess.run(['mount', '--bind', '/dev/' + name, target], check=True)
    Path(root + '/tmp').mkdir(exist_ok=True)
    subprocess.run(['mount', '-t', 'tmpfs', '-o', 'size=64m,mode=1777,nosuid,nodev', 'tmpfs', root + '/tmp'], check=True)
    processes = []
    limit = config['storageBytes']
    processes.append(quota('/input/files', root + '/workspace/files', ['/input/files', '/input/home'], '/locks/' + config['workspaceId'], limit, config['maxFiles'], config.get('readOnlyFiles', False)))
    processes.append(quota('/input/home', root + '/home/user', ['/input/files', '/input/home'], '/locks/' + config['workspaceId'], limit, config['maxFiles']))
    Path(root + '/workspace/shared').mkdir(parents=True, exist_ok=True)
    for index, item in enumerate(config['mounts']):
        source = '/input/shared/' + str(index)
        target = root + '/workspace/shared/' + item['name']
        if item['writable']:
            processes.append(quota(source, target, [source], '/locks/' + item['id'], item['storageBytes'], config['maxFiles']))
        else:
            mount(source, target)
    if Path('/control/managed').is_dir():
        mount('/control/managed', root + '/workspace/.dsh')
    if config.get('modelProxy'):
        from model_capacity import prepare
        prepare(root, config['modelProxy'])
    from model_proxy import firewall
    firewall()
    from net_proxy import ingress, package_proxy
    preview = ingress(config['preview']) if config.get('preview') else None
    packages = package_proxy(config['networkHosts']) if config.get('networkHosts') else None
    broker = None
    if config.get('modelProxy'):
        from model_proxy import start
        broker = start(config['modelProxy'])
    command = config['command']
    env = {'PATH': '/home/user/.npm-global/bin:/home/user/.local/bin:/usr/local/bin:/usr/bin:/bin', 'HOME': '/home/user', 'USER': 'user', 'LANG': 'C.UTF-8', 'TERM': 'xterm-256color', 'NPM_CONFIG_PREFIX': '/home/user/.npm-global', 'PYTHONUSERBASE': '/home/user/.local', 'DSH_HOME': '/home/user/.dsh'}
    if packages:
        env.update({key: 'http://127.0.0.1:8767' for key in ('HTTPS_PROXY', 'https_proxy', 'HTTP_PROXY', 'http_proxy')})
        env['NO_PROXY'] = env['no_proxy'] = 'localhost,127.0.0.1'
    if broker:
        env['DSH_ISOLATED_MODEL_KEY'] = 'execution-local'
        # The outer container owns hard filesystem/network isolation. DSH
        # must not require interactive approval or nested namespace privileges.
        env['DSH_PERMISSION_MODE'] = 'danger-full-access'
    def execute():
        if broker:
            descriptor = os.open('/control/prompt.txt', os.O_RDONLY)
            os.dup2(descriptor, 0); os.close(descriptor)
        os.chroot(root)
        os.chdir('/workspace/files')
        os.setgroups([])
        os.setgid(1000)
        os.setuid(1000)
        # No setuid binary, file capability or nested namespace may regain root.
        if ctypes.CDLL(None).prctl(38, 1, 0, 0, 0) != 0:
            raise RuntimeError('cannot enforce no-new-privileges')
        os.execvpe(command[0], command, env)
    try:
        if config.get('terminal'):
            pid, descriptor = pty.fork()
            if pid == 0:
                try:
                    execute()
                except Exception:
                    os._exit(125)
            buffer = b''
            while True:
                ready, _, _ = select.select([sys.stdin.buffer, descriptor], [], [], 1)
                if descriptor in ready:
                    try:
                        data = os.read(descriptor, 8192)
                    except OSError:
                        break
                    if not data:
                        break
                    sys.stdout.buffer.write(data); sys.stdout.buffer.flush()
                if sys.stdin.buffer in ready:
                    data = os.read(sys.stdin.fileno(), 8192)
                    if not data:
                        break
                    buffer += data
                    while b'\n' in buffer:
                        line, buffer = buffer.split(b'\n', 1)
                        event = json.loads(line)
                        if event.get('type') == 'resize':
                            rows, cols = max(1, min(200, event['rows'])), max(1, min(500, event['cols']))
                            fcntl.ioctl(descriptor, termios.TIOCSWINSZ, struct.pack('HHHH', rows, cols, 0, 0))
                        else:
                            os.write(descriptor, event.get('data', '').encode())
            os.kill(pid, signal.SIGTERM)
            os.waitpid(pid, 0)
        else:
            pid = os.fork()
            if pid == 0:
                try:
                    execute()
                except Exception:
                    os._exit(125)
            _, status = os.waitpid(pid, 0)
            return os.waitstatus_to_exitcode(status)
    finally:
        if broker:
            broker.shutdown()
        if preview:
            preview.shutdown()
        if packages:
            packages.shutdown()
        for process in processes:
            process.terminate(); process.join(timeout=2)
    return 0


if __name__ == '__main__':
    try:
        sys.exit(main())
    except Exception as error:
        # Avoid returning root configuration or secrets in failure traces.
        message = '隔离环境初始化失败，执行已拒绝'
        if isinstance(error, RuntimeError) and str(error) == 'private upstream forbidden':
            message = '模型域名解析为非公网地址，请管理员检查 DNS/VPN 的 fake-IP 配置'
        elif isinstance(error, RuntimeError) and str(error) == 'upstream must be a public HTTPS endpoint':
            message = '模型地址必须使用公网 HTTPS 接口，请管理员检查模型配置'
        print(json.dumps({'type': 'error', 'message': message}, ensure_ascii=False), flush=True)
        sys.exit(125)
