"""Trusted FUSE filesystem: atomically enforce byte/inode budgets across jobs.

Backing directories and the lock directory are outside the user's chroot.
Only this supervisor can access them. The caller never receives host paths.
"""
import errno
import fcntl
import os
import stat
import json
import uuid
from contextlib import contextmanager
from pathlib import Path
from fusepy import FUSE, FuseOSError, Operations


class QuotaFS(Operations):
    def __init__(self, source, budget_roots, lock, byte_limit, inode_limit):
        self.source = Path(source).resolve()
        self.roots = [Path(root) for root in budget_roots]
        self.lock_path = lock
        self.limit = byte_limit
        self.inodes = inode_limit
        self.owner = os.environ.get('SUPERVISOR_EXECUTION', '') + ':' + uuid.uuid4().hex
        self.handles = {}

    def state(self):
        try:
            with open(self.lock_path + '.json') as source:
                return json.load(source)
        except FileNotFoundError:
            return {}

    def save(self, state):
        temporary = self.lock_path + '.json.tmp'
        with open(temporary, 'w') as target:
            json.dump(state, target)
        os.replace(temporary, self.lock_path + '.json')

    def usage(self):
        seen = set()
        used = count = 0
        for root in self.roots:
            for folder, dirs, files in os.walk(root, followlinks=False):
                count += len(dirs)
                for name in files:
                    try:
                        entry = os.lstat(os.path.join(folder, name))
                    except FileNotFoundError:
                        # Docker Desktop can briefly return a removed entry
                        # from its directory cache. Open inode accounting below
                        # still retains the bytes for an unlinked descriptor.
                        continue
                    count += 1
                    if stat.S_ISREG(entry.st_mode):
                        inode = str(entry.st_dev) + ':' + str(entry.st_ino)
                        if inode not in seen:
                            used += entry.st_size
                        seen.add(inode)
        orphaned = {}
        state = self.state(); changed = False
        for entry in state.values():
            unlinked = entry['inode'] not in seen
            if entry.get('unlinked') != unlinked:
                entry['unlinked'] = unlinked; changed = True
            if entry['inode'] not in seen:
                orphaned[entry['inode']] = max(orphaned.get(entry['inode'], 0), entry['size'])
        if changed:
            self.save(state)
        return used + sum(orphaned.values()), count + len(orphaned)

    def track(self, fh):
        value = os.fstat(fh)
        state = self.state(); key = self.owner + ':' + str(fh)
        state[key] = {'inode': str(value.st_dev) + ':' + str(value.st_ino), 'size': value.st_size}
        self.save(state); self.handles[fh] = key
        return fh

    def path(self, relative):
        path = self.source / relative.lstrip('/')
        # The kernel resolves symlinks in the user's namespace. Never follow
        # backing-directory symlinks in trusted filesystem operations.
        parent = path.parent.resolve()
        if path != self.source and not parent.is_relative_to(self.source):
            raise FuseOSError(errno.EACCES)
        return str(path)

    @contextmanager
    def reserve(self, growth=0, new_inode=0):
        with open(self.lock_path, 'a') as lock:
            fcntl.flock(lock, fcntl.LOCK_EX)
            used, count = self.usage()
            if growth > 0 and used + growth > self.limit:
                raise FuseOSError(errno.ENOSPC)
            if new_inode > 0 and count + new_inode > self.inodes:
                raise FuseOSError(errno.ENOSPC)
            try:
                yield
            finally:
                self.usage()

    def getattr(self, path, fh=None):
        with self.reserve():
            value = os.fstat(fh) if fh in self.handles else os.lstat(self.path(path))
        result = {name: getattr(value, name) for name in ('st_atime', 'st_ctime', 'st_gid', 'st_mode', 'st_mtime', 'st_nlink', 'st_size', 'st_uid')}
        result['st_uid'] = result['st_gid'] = 1000
        return result

    def readdir(self, path, fh):
        with self.reserve():
            return ['.', '..'] + os.listdir(self.path(path))

    def statfs(self, path):
        with self.reserve():
            used, count = self.usage()
        free = max(0, self.limit - used) // 4096
        return {'f_bsize': 4096, 'f_frsize': 4096, 'f_blocks': self.limit // 4096, 'f_bavail': free, 'f_bfree': free, 'f_files': self.inodes, 'f_ffree': max(0, self.inodes - count), 'f_namemax': 255}

    def open(self, path, flags):
        with self.reserve():
            return self.track(os.open(self.path(path), (flags & ~os.O_APPEND) | os.O_NOFOLLOW))

    def create(self, path, mode, fi=None):
        with self.reserve(new_inode=1):
            return self.track(os.open(self.path(path), os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, mode))

    def read(self, path, size, offset, fh):
        return os.pread(fh, size, offset)

    def write(self, path, data, offset, fh):
        with self.reserve():
            growth = max(0, offset + len(data) - os.fstat(fh).st_size)
            # Holding the same flock across size check and write prevents
            # concurrent environments from exceeding a shared budget.
            used, _ = self.usage()
            if growth and used + growth > self.limit:
                raise FuseOSError(errno.ENOSPC)
            written = os.pwrite(fh, data, offset)
            self.track(fh)
            return written

    def truncate(self, path, length, fh=None):
        descriptor = fh if fh is not None else os.open(self.path(path), os.O_WRONLY | os.O_NOFOLLOW)
        try:
            with self.reserve(growth=max(0, length - os.fstat(descriptor).st_size)):
                os.ftruncate(descriptor, length)
                if fh is not None:
                    self.track(fh)
        finally:
            if fh is None:
                os.close(descriptor)

    def mkdir(self, path, mode):
        with self.reserve(new_inode=1):
            os.mkdir(self.path(path), mode)

    def unlink(self, path):
        with self.reserve():
            os.unlink(self.path(path))

    def rmdir(self, path):
        with self.reserve():
            os.rmdir(self.path(path))

    def rename(self, old, new):
        with self.reserve():
            os.rename(self.path(old), self.path(new))

    def chmod(self, path, mode):
        with self.reserve():
            os.chmod(self.path(path), mode & 0o777, follow_symlinks=False)

    def chown(self, path, uid, gid):
        if uid not in (-1, 1000) or gid not in (-1, 1000):
            raise FuseOSError(errno.EPERM)

    def utimens(self, path, times=None):
        with self.reserve():
            os.utime(self.path(path), times, follow_symlinks=False)

    def readlink(self, path):
        with self.reserve():
            return os.readlink(self.path(path))

    def symlink(self, target, source):
        with self.reserve(new_inode=1):
            os.symlink(source, self.path(target))

    def link(self, target, source):
        # DSH and package installers use link+unlink for atomic publication.
        # Charge each directory entry, but byte usage counts the inode once.
        with self.reserve(new_inode=1):
            original = self.path(source)
            if not stat.S_ISREG(os.lstat(original).st_mode):
                raise FuseOSError(errno.EPERM)
            os.link(original, self.path(target), follow_symlinks=False)

    def flush(self, path, fh):
        os.fsync(fh)

    def release(self, path, fh):
        with self.reserve():
            state = self.state(); state.pop(self.handles.pop(fh, ''), None)
            self.save(state); os.close(fh)


def serve(source, mount, roots, lock, limit, inodes, readonly=False):
    FUSE(QuotaFS(source, roots, lock, limit, inodes), mount, foreground=True,
         allow_other=True, default_permissions=True, nothreads=True, ro=readonly, fsname='workspace-quota')
