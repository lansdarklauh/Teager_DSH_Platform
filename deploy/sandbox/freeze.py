"""Pause only untrusted processes; keep FUSE and trusted brokers responsive."""
import json
import os
import signal
import socket
import sys
import time

JOURNAL = '/locks/.paused-' + socket.gethostname() + '.freeze'


def processes():
    result = {}
    for name in os.listdir('/proc'):
        if not name.isdigit():
            continue
        try:
            with open('/proc/' + name + '/status') as file:
                rows = dict(line.rstrip().split(':', 1) for line in file if ':' in line)
            if rows.get('Uid', '').split()[:1] == ['1000']:
                result[int(name)] = rows['State'].strip()[0]
        except (FileNotFoundError, ProcessLookupError):
            pass
    return result


def resume():
    try:
        with open(JOURNAL) as file:
            stopped = json.load(file)
    except FileNotFoundError:
        return
    for pid in stopped:
        if pid in processes():
            try:
                os.kill(pid, signal.SIGCONT)
            except ProcessLookupError:
                pass
    os.unlink(JOURNAL)


if sys.argv[1] == 'resume':
    resume()
else:
    stopped = set()
    deadline = time.monotonic() + 10
    try:
        while True:
            current = processes()
            active = {pid for pid, state in current.items() if state not in ('T', 't', 'Z')}
            if not active:
                break
            stopped.update(active)
            with open(JOURNAL, 'w') as file:
                json.dump(list(stopped), file)
            for pid in active:
                try:
                    os.kill(pid, signal.SIGSTOP)
                except ProcessLookupError:
                    pass
            if time.monotonic() > deadline:
                raise TimeoutError('user process did not suspend')
            time.sleep(.02)
    except BaseException:
        resume()
        raise
